using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Queue;

/// <summary>
/// منطق الطابور المشترك بين الحجز والاستقبال:
/// ترتيب التذاكر، حساب الأوقات التخمينية، تحديد نوع الزيارة والسعر، وإنشاء فاتورة الكشفية.
/// </summary>
public class QueueEngine(IAppDbContext db, ICurrentUser current, IClock clock, NumberSequences numbers, SettingsReader settings)
{
    /// <summary>أقل عدد معاينات اليوم لاعتماد المتوسط الفعلي بدل مدة المعاينة المجدولة.</summary>
    public const int MinSamplesForAverage = 3;
    /// <summary>تغير الوقت التقديري الذي يستحق إشعار المريض (booking.eta_changed).</summary>
    public const int EtaNotifyMinutes = 15;

    public record EtaChange(Guid BookingId, Guid PatientId, Guid BranchId, DateTimeOffset EstimatedTime);
    /// <summary>تغيرات الوقت التقديري الكبيرة منذ آخر قراءة (يرسلها QueueNotifier ثم يفرغها).</summary>
    public List<EtaChange> PendingEtaChanges { get; } = [];

    // ------------------------------------------------------------------ الوقت

    public static DateTimeOffset SessionStartUtc(DoctorSession s) => ClockExtensions.ToUtc(s.SessionDate, s.StartTime);
    /// <summary>النهاية الفعلية: «تمديد اليوم» إن وُجد، وإلا نهاية الجدول.</summary>
    public static DateTimeOffset SessionEndUtc(DoctorSession s) => ClockExtensions.ToUtc(s.SessionDate, s.EffectiveEndTime);
    /// <summary>
    /// بداية العمل الفعلية: وقت «الطبيب وصل» إن تأخر عن بداية الوجبة. قبل وصوله: البداية + «تأخر الطبيب» المعلن إن وُجد.
    /// </summary>
    public static DateTimeOffset WorkStartUtc(DoctorSession s)
    {
        var start = SessionStartUtc(s);
        if (s.DoctorArrivedAt is { } a) return a > start ? a : start;
        return s.DelayMinutes > 0 ? start.AddMinutes(s.DelayMinutes) : start;
    }

    public static DateTimeOffset ScheduledTime(DoctorSession s, int sequence) =>
        SessionStartUtc(s).AddMinutes((sequence - 1) * s.SlotMinutes);

    /// <summary>متوسط مدة المعاينة الفعلية للطبيب اليوم (بالدقائق)، أو null إذا كانت العينات قليلة.</summary>
    public async Task<double?> AverageConsultMinutesAsync(Guid doctorId, CancellationToken ct)
    {
        var today = clock.BaghdadToday();
        var durations = await db.QueueTickets
            .Where(t => t.DoctorId == doctorId && t.Status == TicketStatuses.Completed && t.Session!.SessionDate == today
                        && t.StartedAt != null && t.CompletedAt != null)
            .Select(t => new { t.StartedAt, t.CompletedAt })
            .ToListAsync(ct);
        if (durations.Count < MinSamplesForAverage) return null;
        return Math.Round(durations.Average(d => (d.CompletedAt!.Value - d.StartedAt!.Value).TotalMinutes), 1);
    }

    // ------------------------------------------------------------------ الترتيب

    /// <summary>
    /// ترتيب الطابور: في الغرفة، ثم المُستدعى، ثم الطوارئ، ثم العائدون بالنتائج (حسب وقت عودتهم)، ثم الباقون حسب queue_position.
    /// المتخطّون والمنتهون و«بانتظار قسم» خارج الترتيب.
    /// </summary>
    public static List<QueueTicket> Ordered(IEnumerable<QueueTicket> tickets) =>
        tickets.Where(t => t.Status is TicketStatuses.InRoom or TicketStatuses.Called or TicketStatuses.Arrived or TicketStatuses.Waiting)
            .OrderBy(t => t.Status switch
            {
                TicketStatuses.InRoom => 0,
                TicketStatuses.Called => 1,
                _ => t.Priority == TicketPriorities.Emergency ? 2 : t.Priority == TicketPriorities.Returning ? 3 : 4,
            })
            .ThenBy(t => t.Status is TicketStatuses.InRoom or TicketStatuses.Called ? t.CalledAt : null)
            .ThenBy(t => t.Priority == TicketPriorities.Emergency ? t.ArrivedAt : null)
            .ThenBy(t => t.Priority == TicketPriorities.Returning ? t.ReturnedAt : null)
            .ThenBy(t => t.QueuePosition ?? t.SequenceNumber)
            .ThenBy(t => t.SequenceNumber)
            .ToList();

    /// <summary>
    /// يعيد ترقيم الطابور ويحسب الوقت التخميني لكل تذكرة منتظرة، ويحدّث وقت الحجز المرتبط.
    /// الوقت = max(الآن، بداية الوجبة، الموعد المجدول) + عدد من قبله × متوسط المعاينة.
    /// </summary>
    public async Task<List<QueueTicket>> RecalculateAsync(DoctorSession session, CancellationToken ct)
    {
        var tickets = await db.QueueTickets.Where(t => t.SessionId == session.SessionId).ToListAsync(ct);
        var avg = await AverageConsultMinutesAsync(session.DoctorId, ct) ?? session.SlotMinutes;
        var now = clock.UtcNow;
        var start = SessionStartUtc(session);
        var ordered = Ordered(tickets);
        var bookingIds = ordered.Where(t => t.BookingId != null).Select(t => t.BookingId!.Value).ToList();
        var bookings = await db.Bookings.Where(b => bookingIds.Contains(b.BookingId)).ToDictionaryAsync(b => b.BookingId, ct);

        // من قبل عرض التقديم الذكي يأخذ الوقت المتاح فوراً ولا ينتظر موعده المجدول
        var fastForwarded = await db.FastForwardOffers.Where(o => o.SessionId == session.SessionId && o.AcceptedBookingId != null)
            .Select(o => o.AcceptedBookingId!.Value).ToListAsync(ct);
        var workStart = WorkStartUtc(session);
        var cursor = now > workStart ? now : workStart;
        short position = 0;
        // الغرفة والمُستدعى أولاً كما هم
        foreach (var t in ordered.Where(t => t.Status is TicketStatuses.InRoom or TicketStatuses.Called))
        {
            if (t.Status is TicketStatuses.InRoom)
            {
                // المريض في الغرفة: الدور التالي بعد انتهاء متوسط المعاينة من بدايته
                var expectedEnd = (t.StartedAt ?? now).AddMinutes(avg);
                if (expectedEnd > cursor) cursor = expectedEnd;
            }
            else cursor = cursor.AddMinutes(avg);
            t.QueuePosition = null;
        }

        // Q1: طابور واحد. المحجوز يأخذ موعده، والحاضرون (بدون حجز، أو متأخر أُعيد إدراجه، أو محجوز وصل قبل موعده)
        // يملؤون الوقت الفارغ قبل الموعد التالي بترتيب وصولهم. الطوارئ قبل الجميع، ثم العائدون بالنتائج.
        var pool = ordered.Where(t => t.Status is TicketStatuses.Arrived or TicketStatuses.Waiting).ToList();
        var emergencies = pool.Where(t => t.Priority == TicketPriorities.Emergency).OrderBy(t => t.ArrivedAt).ThenBy(t => t.SequenceNumber).ToList();
        var returning = pool.Where(t => t.Priority == TicketPriorities.Returning).OrderBy(t => t.ReturnedAt).ThenBy(t => t.SequenceNumber).ToList();
        bool WalkInLike(QueueTicket t) => t.BookingId is null || t.Priority == TicketPriorities.LateReinserted;
        bool Regular(QueueTicket t) => t.Priority is not (TicketPriorities.Emergency or TicketPriorities.Returning);
        var walkIns = pool.Where(t => Regular(t) && WalkInLike(t))
            .OrderBy(t => t.ArrivedAt ?? t.CreatedAt).ThenBy(t => t.SequenceNumber).ToList();
        var booked = pool.Where(t => Regular(t) && !WalkInLike(t)).OrderBy(t => t.SequenceNumber).ToList();
        // 2.3-ج: الحاضر غير الدافع (عدا الطوارئ والعائد) يبقى في مكانه، لكنه لا يُحسب في الوقت التقديري لمن بعده حتى يدفع
        var paid = await PaidTicketIdsAsync(pool.Where(t => t.Status == TicketStatuses.Arrived && Regular(t)).Select(t => t.TicketId).ToList(), ct);
        bool AwaitingPayment(QueueTicket t) => t.Status == TicketStatuses.Arrived && Regular(t) && !paid.Contains(t.TicketId);
        DateTimeOffset Slot(QueueTicket t) =>
            t.BookingId is { } fb && fastForwarded.Contains(fb) ? start : ScheduledTime(session, t.SequenceNumber);

        void Place(QueueTicket t)
        {
            position++;
            t.QueuePosition = position;
            // لا يُعطى المحجوز الذي لم يصل وقتاً قبل موعده (لا يُطلب منه الحضور أبكر)
            var eta = t.Status == TicketStatuses.Waiting && Slot(t) > cursor ? Slot(t) : cursor;
            t.EstimatedTime = eta;
            if (t.BookingId is { } bid && bookings.TryGetValue(bid, out var b))
            {
                if (b.EstimatedTime is { } old && Math.Abs((eta - old).TotalMinutes) >= EtaNotifyMinutes && b.Status == BookingStatuses.Booked)
                    PendingEtaChanges.Add(new EtaChange(b.BookingId, b.PatientId, session.BranchId, eta));
                b.EstimatedTime = eta;
            }
            if (!AwaitingPayment(t)) cursor = eta.AddMinutes(avg);
        }

        foreach (var t in emergencies) Place(t);
        foreach (var t in returning) Place(t);
        while (walkIns.Count > 0 || booked.Count > 0)
        {
            // محجوز حان موعده: له الدور (لو لم يصل بعد يبقى مكانه في الحساب، والاستدعاء يتجاوزه للحاضر التالي)
            var due = booked.FirstOrDefault(t => Slot(t) <= cursor);
            if (due is not null) { booked.Remove(due); Place(due); continue; }
            // وقت فارغ قبل الموعد التالي: أقدم الحاضرين وصولاً
            var present = walkIns.Concat(booked.Where(t => t.Status == TicketStatuses.Arrived))
                .OrderBy(t => t.ArrivedAt ?? t.CreatedAt).ThenBy(t => t.SequenceNumber).FirstOrDefault();
            if (present is not null)
            {
                walkIns.Remove(present);
                booked.Remove(present);
                Place(present);
                continue;
            }
            var next = booked[0];
            booked.RemoveAt(0);
            Place(next);
        }
        foreach (var t in tickets.Where(t => !ordered.Contains(t))) t.QueuePosition = null;
        await db.SaveChangesAsync(ct);
        return tickets;
    }

    /// <summary>التذاكر التي دُفعت كشفيتها أو أُجّلت (تدخل بلا تحصيل).</summary>
    public async Task<HashSet<Guid>> PaidTicketIdsAsync(List<Guid> ticketIds, CancellationToken ct) =>
        ticketIds.Count == 0 ? [] :
        (await db.InvoiceItems.Where(i => i.TicketId != null && ticketIds.Contains(i.TicketId.Value)
                                          && (i.Status == InvoiceItemStatuses.Paid || i.Status == InvoiceItemStatuses.Deferred
                                              || i.Status == InvoiceItemStatuses.PartiallyRefunded))
            .Select(i => i.TicketId!.Value).ToListAsync(ct)).ToHashSet();

    // ------------------------------------------------------------------ العائد بالنتائج (التدقيق 1.2)

    /// <summary>تحويلات زيارة كل تذكرة: طلبات المختبر والأشعة والسونار من معاينتها، ووصفتها للصيدلية.</summary>
    public async Task<Dictionary<Guid, List<Referral>>> VisitReferralsAsync(IReadOnlyCollection<Guid> ticketIds, CancellationToken ct)
    {
        var result = ticketIds.Distinct().ToDictionary(id => id, _ => new List<Referral>());
        if (result.Count == 0) return result;
        var ids = result.Keys.ToList();
        var cons = await db.Consultations.AsNoTracking().Where(c => ids.Contains(c.TicketId))
            .Select(c => new { c.ConsultationId, c.TicketId }).ToListAsync(ct);
        if (cons.Count == 0) return result;
        var byConsultation = cons.ToDictionary(c => c.ConsultationId, c => c.TicketId);
        var cids = byConsultation.Keys.ToList();
        var fromOrders = await (from r in db.Referrals.AsNoTracking()
                                join m in db.MedicalOrders.AsNoTracking() on r.OrderItemId equals (Guid?)m.OrderItemId
                                where m.ConsultationId != null && cids.Contains(m.ConsultationId.Value)
                                select new { Referral = r, Cid = m.ConsultationId!.Value }).ToListAsync(ct);
        var fromRx = await (from r in db.Referrals.AsNoTracking()
                            join p in db.Prescriptions.AsNoTracking() on r.PrescriptionId equals (Guid?)p.PrescriptionId
                            where cids.Contains(p.ConsultationId)
                            select new { Referral = r, Cid = p.ConsultationId }).ToListAsync(ct);
        foreach (var x in fromOrders.Concat(fromRx)) result[byConsultation[x.Cid]].Add(x.Referral);
        return result;
    }

    /// <summary>
    /// ما ينتظره المراجع: تحويلات المختبر/الأشعة/السونار غير المكتملة (الصيدلية تُحسب فقط إذا لم يكن في الزيارة غيرها).
    /// جاهز للعودة = لا شيء معلق (اكتمل كله أو عُلّم خارجياً أو انتهى). HasClinical = في الزيارة مختبر أو أشعة أو سونار.
    /// </summary>
    public static (List<string> Pending, bool Ready, bool HasClinical) AwaitingState(IEnumerable<Referral> referrals)
    {
        var list = referrals.Where(r => r.Status != ReferralStatuses.Cancelled).ToList();
        var clinical = list.Where(r => r.Service != ReferralServices.Pharmacy).ToList();
        var relevant = clinical.Count > 0 ? clinical : list;
        var pending = relevant.Where(r => r.Status is ReferralStatuses.Referred or ReferralStatuses.Activated)
            .Select(r => r.Service).Distinct().OrderBy(s => Array.IndexOf(ServiceOrder, s)).ToList();
        return (pending, pending.Count == 0, clinical.Count > 0);
    }

    /// <summary>ترتيب ثابت لعرض الأقسام في الشارة.</summary>
    private static readonly string[] ServiceOrder = [ReferralServices.Lab, ReferralServices.Radiology, ReferralServices.Ultrasound, ReferralServices.Pharmacy];

    public static string ServiceAr(string service) => service switch
    {
        ReferralServices.Lab => "المختبر",
        ReferralServices.Radiology => "الأشعة",
        ReferralServices.Ultrasound => "السونار",
        ReferralServices.Pharmacy => "الصيدلية",
        _ => service,
    };

    /// <summary>مدة العودة المجانية بالأيام: الطبيب، ثم العيادة، ثم إعداد الفرع queue.free_return_days (7).</summary>
    public async Task<int> FreeReturnDaysAsync(Guid doctorId, CancellationToken ct)
    {
        var d = await db.Doctors.AsNoTracking().Where(x => x.DoctorId == doctorId)
            .Select(x => new { x.FreeReturnDays, Clinic = x.Clinic!.FreeReturnDays, x.Clinic.BranchId }).FirstAsync(ct);
        return d.FreeReturnDays ?? d.Clinic ?? (int)await settings.GetLongAsync(SettingsReader.FreeReturnDays, d.BranchId, 7, ct);
    }

    // ------------------------------------------------------------------ نوع الزيارة والسعر

    /// <summary>مراجعة إذا كانت للمريض زيارة مكتملة عند نفس الطبيب خلال فترة المراجعة (visit.follow_up_window_days).</summary>
    public async Task<string> ResolveVisitKindAsync(string? requested, Guid patientId, Guid doctorId, DateOnly onDate, bool staffMayOverride, CancellationToken ct)
    {
        if (requested is VisitKinds.NewVisit or VisitKinds.FollowUp)
        {
            if (!staffMayOverride) throw new AppException(403, "FORBIDDEN", "تحديد نوع الزيارة للاستقبال فقط");
            return requested;
        }
        if (requested is not (null or "auto"))
            throw AppException.Validation(new Dictionary<string, string[]> { ["visit_kind"] = ["auto أو new_visit أو follow_up"] });

        var doctorWindow = await db.Doctors.Where(d => d.DoctorId == doctorId).Select(d => d.FollowUpDays).FirstOrDefaultAsync(ct);
        var window = doctorWindow ?? await settings.GetLongAsync(SettingsReader.FollowUpWindowDays, current.BranchId, 14, ct);
        var since = onDate.AddDays(-(int)window);
        var recent = await db.QueueTickets.AnyAsync(t =>
            t.PatientId == patientId && t.DoctorId == doctorId && t.Status == TicketStatuses.Completed
            && t.Session!.SessionDate >= since && t.Session.SessionDate <= onDate, ct);
        return recent ? VisitKinds.FollowUp : VisitKinds.NewVisit;
    }

    /// <summary>سعر الكشفية: سعر المراجعة الخاص بالطبيب إن حُدد (0 = مجانية)، وإلا سعر الخدمة.</summary>
    public static long ConsultationPrice(Doctor doctor, Service service, string visitKind) =>
        visitKind == VisitKinds.FollowUp && doctor.FollowUpPrice is { } p ? p : service.Price;

    public async Task<Service> ConsultationServiceAsync(Guid clinicId, string visitKind, CancellationToken ct) =>
        await db.Services.AsNoTracking().FirstOrDefaultAsync(s =>
            s.ClinicId == clinicId && s.Type == ServiceTypes.Consultation && s.VisitKind == visitKind && s.IsActive, ct)
        ?? throw new AppException(422, "SERVICE_NOT_CONFIGURED", "لم تُحدد كشفية لهذه العيادة",
            $"أضف خدمة كشفية ({visitKind}) للعيادة من شاشة الخدمات");

    // ------------------------------------------------------------------ التذاكر والفواتير

    /// <summary>حرف الطبيب في رقم التذكرة (A، B، ...) حسب ترتيب أطباء العيادة، ليتميز الطابور على شاشة العيادة المشتركة.</summary>
    public async Task<string> TicketNumberAsync(Doctor doctor, short sequence, CancellationToken ct)
    {
        var doctors = await db.Doctors.Where(d => d.ClinicId == doctor.ClinicId)
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.DoctorId).Select(d => d.DoctorId).ToListAsync(ct);
        var index = Math.Max(0, doctors.IndexOf(doctor.DoctorId)) % 26;
        return $"{(char)('A' + index)}-{sequence:D3}";
    }

    /// <summary>أول رقم تسلسل غير مستخدم في الوجبة (الحجوزات والتذاكر معاً).</summary>
    public async Task<short> NextSequenceAsync(Guid sessionId, CancellationToken ct)
    {
        var maxTicket = await db.QueueTickets.Where(t => t.SessionId == sessionId).MaxAsync(t => (short?)t.SequenceNumber, ct) ?? 0;
        var maxBooking = await db.Bookings.Where(b => b.SessionId == sessionId).MaxAsync(b => (short?)b.SequenceNumber, ct) ?? 0;
        return (short)(Math.Max(maxTicket, maxBooking) + 1);
    }

    /// <summary>يقفل صف الوجبة حتى نهاية المعاملة (منع تجاوز السعة أو تكرار الأرقام مع التزامن).</summary>
    public Task LockSessionAsync(Guid sessionId, CancellationToken ct) =>
        db.ExecuteAsync($"SELECT 1 FROM doctor_sessions WHERE session_id = {sessionId} FOR UPDATE", ct);

    /// <summary>
    /// فاتورة الكشفية عند تثبيت الحضور. السعر ونسبة الطبيب لقطة (Snapshot) وقت الإنشاء.
    /// إذا كان الحجز مدفوعاً مسبقاً تُنشأ الفاتورة مدفوعة.
    /// </summary>
    public async Task<Invoice> CreateConsultationInvoiceAsync(QueueTicket ticket, Doctor doctor, Service service, string visitKind,
        long price, bool alreadyPaid, CancellationToken ct)
    {
        // مراجعة مجانية باتفاق الطبيب (سعر 0): لا تحصيل، ويدخل المريض مباشرة
        alreadyPaid |= price == 0;
        var rate = 0m;
        if (service.IsCommissionable)
        {
            var r = await db.DoctorCommissionRates.Where(x => x.DoctorId == doctor.DoctorId && x.EffectiveFrom <= clock.BaghdadToday())
                .OrderByDescending(x => x.EffectiveFrom).FirstOrDefaultAsync(ct);
            if (r is not null) rate = visitKind == VisitKinds.FollowUp ? r.FollowUpRate : r.NewVisitRate;
        }
        var doctorShare = (long)Math.Round(price * rate / 100m, MidpointRounding.AwayFromZero);

        var seq = await numbers.NextAsync(ticket.BranchId, "invoice", "all", ct);
        var code = await numbers.BranchCodeAsync(ticket.BranchId, ct);
        var now = clock.UtcNow;
        var invoice = new Invoice
        {
            InvoiceId = UuidV7.New(),
            InvoiceNumber = $"{code}-INV-{seq:D6}",
            BranchId = ticket.BranchId,
            PatientId = ticket.PatientId,
            Status = alreadyPaid ? InvoiceStatuses.Paid : InvoiceStatuses.Unpaid,
            GrossAmount = price,
            NetAmount = price,
            PaidAmount = alreadyPaid ? price : 0,
            CreatedBy = current.StaffUserId, // المريض (تثبيت الحضور الذاتي) ليس موظفاً
            CreatedAt = now,
        };
        invoice.Items.Add(new InvoiceItem
        {
            InvoiceItemId = UuidV7.New(),
            InvoiceId = invoice.InvoiceId,
            ServiceId = service.ServiceId,
            DoctorId = doctor.DoctorId,
            TicketId = ticket.TicketId,
            ItemType = visitKind,
            ServiceName = service.Name,
            Quantity = 1,
            UnitPrice = price,
            NetPrice = price,
            CommissionRate = rate,
            DoctorShare = doctorShare,
            ComplexShare = price - doctorShare,
            Status = alreadyPaid ? InvoiceItemStatuses.Paid : InvoiceItemStatuses.Unpaid,
            CreatedAt = now,
        });
        db.Invoices.Add(invoice);
        return invoice;
    }

    /// <summary>بند الكشفية المرتبط بالتذكرة (إن وُجد).</summary>
    public Task<InvoiceItem?> ConsultationItemAsync(Guid ticketId, CancellationToken ct) =>
        db.InvoiceItems.Where(i => i.TicketId == ticketId && i.Status != InvoiceItemStatuses.Void)
            .OrderByDescending(i => i.CreatedAt).FirstOrDefaultAsync(ct);

    /// <summary>حالة الدفع للعرض: paid | deferred | unpaid | not_billed (لم يثبت حضوره بعد).</summary>
    public static string PaymentStatusOf(InvoiceItem? item) => item?.Status switch
    {
        null => "not_billed",
        InvoiceItemStatuses.Paid or InvoiceItemStatuses.PartiallyRefunded => "paid",
        InvoiceItemStatuses.Deferred => "deferred",
        InvoiceItemStatuses.Refunded => "refunded",
        _ => "unpaid",
    };
}
