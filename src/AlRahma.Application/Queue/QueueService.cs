using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Doctors;
using AlRahma.Application.Patients;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Queue;

/// <summary>`allow_extra`: تأكيد الاستقبال لقبول المراجع «إضافي» فوق السعة بعد رد 409 SESSION_FULL_EXTRA_POSSIBLE.</summary>
/// <summary>
/// `visit_mode` (التدقيق 1.2، قرار المالك): للمراجع الذي ينتظر نتائج الطبيب نفسه أو له عودة مجانية صالحة،
/// يجب اختيار "return" (عودة بالنتائج، بلا فاتورة) أو "new" (زيارة جديدة). بدونه يرد 409 RETURN_OR_NEW_REQUIRED.
/// </summary>
public record CheckInRequest(Guid? TicketId, Guid? BookingId, string? BookingQr, Guid? PatientId, Guid? DoctorId, string? VisitKind,
    DateTimeOffset? CreatedOfflineAt, bool? AllowExtra = null, string? VisitMode = null);
/// <summary>external = نتائج الأقسام المتبقية من خارج المجمع: تُعلَّم تحويلاتها «خارجي».</summary>
public record ReturnedRequest(bool? External);
public record CheckInResponse(Guid TicketId, string TicketNumber, string Status, short? QueuePosition, DateTimeOffset? EstimatedTime,
    Guid InvoiceId, string VisitKind, long AmountDue, string PaymentStatus, bool IsExtra = false)
{
    /// <summary>لطباعة التذكرة: بتوقيت بغداد.</summary>
    public string? IssuedDateAr { get; init; }
    public string? IssuedTimeAr { get; init; }
    public string? EstimatedTimeAr { get; init; }
    /// <summary>عربون الحجز المتاح المطروح من amount_due (2.3-ب). يُخصم فعلياً من رصيد المريض عند التحصيل (use_deposit_credit).</summary>
    public long DepositCredit { get; init; }
}
/// <summary>«تمديد اليوم»: النهاية الفعلية الجديدة HH:mm (الجدول الأسبوعي لا يتغير).</summary>
public record ExtendSessionRequest(string? End);
public record SessionRegistrationRequest(bool? Closed);
public record SessionControlDto(Guid SessionId, string Start, string End, string ScheduledEnd, DateTimeOffset? DoctorArrivedAt, bool RegistrationClosed);
public record TicketActionResponse(Guid TicketId, string TicketNumber, string Status, string Priority, short? QueuePosition,
    DateTimeOffset? EstimatedTime, DateTimeOffset? CalledAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt)
{
    /// <summary>في الإنهاء فقط: المريض التالي الذي استُدعي تلقائياً (أو null).</summary>
    public TicketActionResponse? NextTicket { get; init; }
}
/// <summary>call_next: استدعاء التالي تلقائياً بعد الإنهاء. null = إعداد الفرع queue.auto_call_next.
/// expect_return: هل يعود المراجع بالنتائج؟ null = تلقائياً (للزيارة تحويل مختبر/أشعة/سونار غير مكتمل)،
/// true = «بانتظار قسم» حتى لو كان التحويل للصيدلية فقط، false = إنهاء نهائي.</summary>
public record CompleteTicketRequest(bool? CallNext, bool? ExpectReturn = null);
public record NoShowRequest(string? Mode);
public record NoShowResponse(Guid TicketId, string Status, bool QueueRecalculated, bool CountedAsNoShow, bool PatientBanned);
public record EmergencyRequest(string? Reason);
public record EmergencyResponse(Guid TicketId, string Priority, int Position, int AffectedTickets);

/// <summary>تثبيت الحضور الذاتي من تطبيق المريض: token = الرمز أو نص QR الشاشة كما هو. booking_id اختياري في /me/self-check-in.</summary>
public record SelfCheckInRequest(string? Token, Guid? BookingId = null);
public record SelfCheckInResponse(Guid BookingId, Guid TicketId, string TicketNumber, string Status, short? QueuePosition, DateTimeOffset? EstimatedTime,
    Guid InvoiceId, string VisitKind, long AmountDue, string PaymentStatus, bool AlreadyCheckedIn, string DoctorName, string ClinicName, short? Floor,
    string MessageAr);
/// <summary>من أين جاء تثبيت الحضور: الاستقبال (فرع التوكن ونطاق الموظف) أو المريض نفسه (فرع الحجز، بلا نطاق موظف).</summary>
internal sealed record CheckInContext(Guid BranchId, string Method);

public class QueueService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, QueueEngine engine,
    QueueReader reader, QueueNotifier notifier, DoctorService doctors, BanService bans,
    AlRahma.Application.Branches.ClosureService closures, SettingsReader settings, OperationClock op,
    CheckInTokenService checkInTokens, PatientApp.PatientPortalService portal)
{
    // ------------------------------------------------------------------ القراءة

    public async Task<LiveQueueDto?> LiveForDoctorAsync(Guid doctorId, DateOnly? date, CancellationToken ct)
    {
        var doctor = await LoadDoctorAsync(doctorId, ct);
        await EnsureScopeAsync(doctor, ct);
        var day = date ?? clock.BaghdadToday();
        if (day >= clock.BaghdadToday()) await doctors.EnsureSessionsAsync(doctor.DoctorId, day, day, ct);
        var session = await reader.CurrentSessionAsync(doctor.DoctorId, day, ct);
        return session is null ? null : await reader.LiveAsync(session, ct);
    }

    /// <summary>طوابير عدة أطباء: عيادة محددة، أو عيادات موظف الاستقبال، أو الفرع كاملاً للمدير.</summary>
    public async Task<IReadOnlyList<LiveQueueDto>> LiveListAsync(Guid? clinicId, DateOnly? date, CancellationToken ct)
    {
        var query = db.Doctors.AsNoTracking().Include(d => d.Clinic)
            .Where(d => d.IsActive && d.Clinic!.BranchId == current.BranchId);
        if (clinicId is { } cid) query = query.Where(d => d.ClinicId == cid);
        if (current.Role == Roles.Doctor) query = query.Where(d => d.UserId == current.UserId);
        else if (current.Role == Roles.Receptionist)
        {
            var mine = await MyClinicsAsync(ct);
            query = query.Where(d => mine.Contains(d.ClinicId));
        }
        var list = await query.OrderBy(d => d.Clinic!.Name).ThenBy(d => d.FullName).ToListAsync(ct);
        var day = date ?? clock.BaghdadToday();
        var result = new List<LiveQueueDto>();
        foreach (var d in list)
        {
            if (day >= clock.BaghdadToday()) await doctors.EnsureSessionsAsync(d.DoctorId, day, day, ct);
            var session = await reader.CurrentSessionAsync(d.DoctorId, day, ct);
            if (session is not null) result.Add(await reader.LiveAsync(session, ct));
        }
        return result;
    }

    // ------------------------------------------------------------------ تثبيت الحضور

    public async Task<CheckInResponse> CheckInAsync(CheckInRequest req, CancellationToken ct)
    {
        var bookingId = req.BookingId;
        if (bookingId is null && !string.IsNullOrWhiteSpace(req.BookingQr))
        {
            // رمز QR في تطبيق المريض يحمل معرف الحجز (يقبل الصيغة "booking:<id>" أو المعرف مباشرة)
            var raw = req.BookingQr.Trim();
            if (raw.StartsWith("booking:", StringComparison.OrdinalIgnoreCase)) raw = raw[8..];
            if (!Guid.TryParse(raw, out var parsed))
                throw AppException.Validation(new Dictionary<string, string[]> { ["booking_qr"] = ["رمز الحجز غير صالح"] });
            bookingId = parsed;
        }

        return bookingId is { } bid
            ? await CheckInBookingAsync(bid, ct)
            : await CheckInWalkInAsync(req, ct);
    }

    private async Task<CheckInResponse> CheckInBookingAsync(Guid bookingId, CancellationToken ct) =>
        (await CheckInBookingAsync(bookingId, null, ct)).Response;

    /// <summary>
    /// تثبيت حضور الحجز (الاستقبال أو المريض بمسح QR الشاشة): نفس النتيجة تماماً، تذكرة «واصل» وفاتورة الكشفية والأحداث المباشرة.
    /// تكرار التثبيت يعيد نفس النتيجة (Already = true).
    /// </summary>
    private async Task<(CheckInResponse Response, bool Already)> CheckInBookingAsync(Guid bookingId, CheckInContext? ctx, CancellationToken ct)
    {
        var branchId = ctx?.BranchId ?? current.BranchId;
        await using var tx = await db.BeginTransactionAsync(ct);
        // M3: قفل الوجبة قبل قراءة الحجز، فمسحان متزامنان لنفس الحجز لا يُنشئان فاتورتين
        var lockId = await db.Bookings.AsNoTracking().Where(b => b.BookingId == bookingId).Select(b => (Guid?)b.SessionId).FirstOrDefaultAsync(ct);
        if (lockId is { } sid) await engine.LockSessionAsync(sid, ct);
        var booking = await db.Bookings.Include(b => b.Session)
                          .FirstOrDefaultAsync(b => b.BookingId == bookingId && b.Session!.BranchId == branchId, ct)
                      ?? throw new AppException(404, "BOOKING_NOT_FOUND", "الحجز غير موجود");
        // تغيير موعد متزامن نقل الحجز لوجبة أخرى بعد قراءتنا: القفل لم يعد على وجبته
        if (lockId != booking.SessionId)
            throw new AppException(409, "CONCURRENT_UPDATE", "تغير موعد الحجز للتو", "أعد المحاولة");
        var ticket = await db.QueueTickets.FirstAsync(t => t.BookingId == booking.BookingId, ct);
        var doctor = await LoadDoctorAsync(booking.Session!.DoctorId, branchId, ct);
        if (ctx is null) await EnsureScopeAsync(doctor, ct);

        if (booking.Status == BookingStatuses.CheckedIn && ticket.Status != TicketStatuses.Waiting)
        {
            // تكرار المسح: يعيد نفس النتيجة
            var existing = await engine.ConsultationItemAsync(ticket.TicketId, ct);
            if (existing is not null) return (await ResponseAsync(ticket, existing, ct), true);
        }
        if (booking.Status != BookingStatuses.Booked)
            throw new AppException(409, "BOOKING_NOT_ACTIVE", "الحجز ملغى أو منتهٍ");
        if (!op.IsOffline) await closures.EnsureOpenAsync(booking.Session.ClinicId, op.Today, ct); // ما حدث أوفلاين حدث فعلاً
        if (booking.Session.SessionDate != op.Today)
            throw new AppException(422, "BOOKING_NOT_TODAY", "الحجز ليس لهذا اليوم", $"موعد الحجز {booking.Session.SessionDate:yyyy-MM-dd}");

        var now = op.Now;
        // تجاوز مهلة التأخر فتخطاه النظام: يُعامل كمتأخر ويُدرج في أول فجوة (قرار Q1)
        if (ticket.Status == TicketStatuses.Skipped && ticket.Priority != TicketPriorities.Emergency) ticket.Priority = TicketPriorities.LateReinserted;
        booking.Status = BookingStatuses.CheckedIn;
        booking.CheckedInAt = now;
        booking.CheckInMethod = ctx?.Method ?? CheckInMethods.Reception;
        ticket.Status = TicketStatuses.Arrived;
        ticket.ArrivedAt = now;
        var service = await engine.ConsultationServiceAsync(booking.Session.ClinicId, booking.VisitKind, ct);
        var invoice = await engine.CreateConsultationInvoiceAsync(ticket, doctor, service, booking.VisitKind, booking.Price,
            alreadyPaid: booking.PaymentStatus == PaymentStatuses.Paid, ct);
        if (booking.PaymentStatus == PaymentStatuses.Paid)
        {
            // الحجز المدفوع إلكترونياً: الدفعة تُربط ببند الكشفية (للإيصال والاسترجاع)
            var online = await db.OnlinePayments.Where(o => o.BookingId == booking.BookingId && o.Purpose == OnlinePaymentPurposes.BookingFull
                                                           && o.Status == OnlinePaymentStatuses.Succeeded)
                .Join(db.Payments, o => o.OnlinePaymentId, p => p.OnlinePaymentId, (o, p) => p.PaymentId).FirstOrDefaultAsync(ct);
            if (online != Guid.Empty)
                db.PaymentAllocations.Add(new PaymentAllocation { PaymentId = online, InvoiceItemId = invoice.Items[0].InvoiceItemId, Amount = invoice.Items[0].NetPrice });
        }
        audit.Add("CHECKED_IN", "queue_ticket", ticket.TicketId, after: new { booking_id = booking.BookingId, invoice.InvoiceNumber, method = booking.CheckInMethod });
        await db.SaveChangesAsync(ct);
        await engine.RecalculateAsync(booking.Session, ct);
        if (tx is not null) await tx.CommitAsync(ct);

        await notifier.SessionChangedAsync(booking.SessionId, ct);
        await notifier.BookingChangedAsync(booking.BookingId,
            booking.CheckInMethod == CheckInMethods.SelfQr ? BookingChangeActions.SelfCheckedIn : BookingChangeActions.CheckedIn, null, ct);
        return (await ResponseAsync(ticket, invoice.Items[0], ct), false);
    }

    // ------------------------------------------------------------------ تثبيت الحضور الذاتي (QR الشاشة)

    /// <summary>
    /// المريض يمسح رمز QR الدوار على شاشة الانتظار من تطبيقه. الرمز يثبت أنه داخل المجمع الآن (يتغير كل 45 ثانية)،
    /// ويحدد الشاشة وعيادتها وفرعها. bookingId = null: يُختار حجز اليوم الوحيد لمرضى الحساب في عيادة/فرع الشاشة.
    /// </summary>
    public async Task<SelfCheckInResponse> SelfCheckInAsync(Guid? bookingId, SelfCheckInRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Token))
            throw AppException.Validation(new Dictionary<string, string[]> { ["token"] = ["امسح رمز QR على شاشة الانتظار"] });
        var deviceId = checkInTokens.Validate(req.Token);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.Kind == "display" && d.IsActive && d.ClinicId != null, ct)
                     ?? throw new AppException(422, "CHECKIN_TOKEN_INVALID", "رمز تثبيت الحضور غير صالح", "امسح الرمز الظاهر على شاشة الانتظار داخل المجمع");
        var scope = await settings.GetStringAsync(SettingsReader.CheckInQrScope, device.BranchId, ct);
        var clinicScope = scope != SettingsReader.CheckInQrScopeValues.Branch;
        var today = clock.BaghdadToday();
        var linked = portal.LinkedPatientIds();

        bookingId ??= req.BookingId;
        if (bookingId is null)
        {
            var mine = await db.Bookings.AsNoTracking().Include(b => b.Session)
                .Where(b => linked.Contains(b.PatientId) && b.Session!.SessionDate == today && b.Session.BranchId == device.BranchId
                            && (!clinicScope || b.Session.ClinicId == device.ClinicId)
                            && (b.Status == BookingStatuses.Booked || b.Status == BookingStatuses.CheckedIn))
                .ToListAsync(ct);
            var pending = mine.Where(b => b.Status == BookingStatuses.Booked).ToList();
            var pick = pending.Count == 1 ? pending[0] : pending.Count == 0 && mine.Count == 1 ? mine[0] : null;
            if (pick is null && pending.Count == 0)
                throw new AppException(404, "NO_BOOKING_TODAY", "لا يوجد لك حجز اليوم في هذه العيادة", "راجع الاستقبال إذا كان لديك موعد");
            if (pick is null)
            {
                var doctorIds = pending.Select(b => b.Session!.DoctorId).Distinct().ToList();
                var names = await db.Doctors.AsNoTracking().Where(d => doctorIds.Contains(d.DoctorId)).ToDictionaryAsync(d => d.DoctorId, d => d.FullName, ct);
                throw new AppException(409, "MULTIPLE_BOOKINGS_TODAY", "لديك أكثر من حجز اليوم", "اختر الحجز ثم امسح الرمز مرة أخرى")
                {
                    Extensions = new Dictionary<string, object?>
                    {
                        ["bookings"] = pending.Select(b => new Dictionary<string, object?>
                        {
                            ["booking_id"] = b.BookingId, ["patient_id"] = b.PatientId, ["doctor_name"] = names.GetValueOrDefault(b.Session!.DoctorId),
                            ["estimated_time"] = b.EstimatedTime,
                        }).ToList(),
                    },
                };
            }
            bookingId = pick.BookingId;
        }

        var booking = await db.Bookings.AsNoTracking().Include(b => b.Session).Include(b => b.Patient)
                          .FirstOrDefaultAsync(b => b.BookingId == bookingId && linked.Contains(b.PatientId), ct)
                      ?? throw new AppException(404, "BOOKING_NOT_FOUND", "الحجز غير موجود");
        var session = booking.Session!;
        if (await settings.GetLongAsync(SettingsReader.SelfCheckInEnabled, session.BranchId, 1, ct) != 1)
            throw new AppException(403, "SELF_CHECKIN_DISABLED", "تثبيت الحضور من التطبيق غير متاح في هذا الفرع", "ثبّت حضورك من الاستقبال");
        if (booking.Patient!.IsBanned)
            throw new AppException(403, "PATIENT_BANNED", "المريض محظور", "راجع الاستقبال لتثبيت حضورك");
        var clinic = await db.Clinics.AsNoTracking().FirstAsync(c => c.ClinicId == session.ClinicId, ct);
        if (device.BranchId != session.BranchId)
            throw new AppException(409, "CHECKIN_WRONG_BRANCH", "هذا الرمز لفرع آخر", $"حجزك في {await BranchNameAsync(session.BranchId, ct)}");
        if (clinicScope && device.ClinicId != session.ClinicId)
            throw new AppException(409, "CHECKIN_WRONG_CLINIC", "هذا الرمز لعيادة أخرى", $"حجزك في {clinic.Name}، امسح رمز شاشتها")
                { Extensions = new Dictionary<string, object?> { ["clinic_id"] = clinic.ClinicId, ["clinic_name"] = clinic.Name, ["floor"] = clinic.Floor } };
        if (session.SessionDate != today)
            throw new AppException(422, "BOOKING_NOT_TODAY", "الحجز ليس لهذا اليوم", $"موعد الحجز {session.SessionDate:yyyy-MM-dd}");

        var (r, already) = await CheckInBookingAsync(booking.BookingId, new CheckInContext(session.BranchId, CheckInMethods.SelfQr), ct);
        var doctorName = await db.Doctors.AsNoTracking().Where(d => d.DoctorId == session.DoctorId).Select(d => d.FullName).FirstAsync(ct);
        var message = r.AmountDue > 0
            ? (r.DepositCredit > 0
                ? $"تم تثبيت حضورك، رقمك {r.TicketNumber}. ادفع الباقي {r.AmountDue:N0} د.ع في الاستقبال قبل دخولك (بعد خصم عربونك {r.DepositCredit:N0} د.ع)."
                : $"تم تثبيت حضورك، رقمك {r.TicketNumber}. ادفع {r.AmountDue:N0} د.ع في الاستقبال قبل دخولك.")
            : r.DepositCredit > 0
                ? $"تم تثبيت حضورك، رقمك {r.TicketNumber}. عربونك يغطي المبلغ، راجع الاستقبال لتثبيت الدفع قبل دخولك."
                : $"تم تثبيت حضورك، رقمك {r.TicketNumber}. انتظر النداء على الشاشة.";
        return new SelfCheckInResponse(booking.BookingId, r.TicketId, r.TicketNumber, r.Status, r.QueuePosition, r.EstimatedTime, r.InvoiceId,
            r.VisitKind, r.AmountDue, r.PaymentStatus, already, doctorName, clinic.Name, clinic.Floor, message);
    }

    private Task<string> BranchNameAsync(Guid branchId, CancellationToken ct) =>
        db.Branches.AsNoTracking().Where(b => b.BranchId == branchId).Select(b => b.Name).FirstAsync(ct);

    private async Task<CheckInResponse> CheckInWalkInAsync(CheckInRequest req, CancellationToken ct)
    {
        new Validator()
            .Check(req.PatientId is not null, "patient_id", "المريض مطلوب للحضور المباشر")
            .Check(req.DoctorId is not null, "doctor_id", "الطبيب مطلوب للحضور المباشر")
            .ThrowIfInvalid();
        var ticketId = req.TicketId ?? UuidV7.New();
        var existingTicket = await db.QueueTickets.FirstOrDefaultAsync(t => t.TicketId == ticketId, ct);
        if (existingTicket is not null)
        {
            // إعادة إرسال تذكرة أوفلاين وصلت سابقاً
            if (existingTicket.PatientId != req.PatientId || existingTicket.BranchId != current.BranchId)
                throw new AppException(409, "TICKET_EXISTS", "يوجد تذكرة بنفس المعرف");
            var item = await engine.ConsultationItemAsync(existingTicket.TicketId, ct)
                       ?? throw new AppException(409, "TICKET_EXISTS", "يوجد تذكرة بنفس المعرف");
            return await ResponseAsync(existingTicket, item, ct);
        }

        var doctor = await LoadDoctorAsync(req.DoctorId!.Value, ct);
        await EnsureScopeAsync(doctor, ct);
        var patient = await db.Patients.FirstOrDefaultAsync(p => p.PatientId == req.PatientId, ct)
                      ?? throw new AppException(404, "PATIENT_NOT_FOUND", "المريض غير موجود");
        if (patient.MergedIntoPatientId is not null)
            throw new AppException(409, "PATIENT_MERGED", "هذا الملف مدموج في ملف آخر");

        // 1.2 (قرار المالك): المراجع ينتظر نتائج هذا الطبيب أو له عودة مجانية صالحة → الاستقبال يختار «عودة بالنتائج» أو «زيارة جديدة»
        var visitMode = req.VisitMode?.Trim().ToLowerInvariant();
        new Validator().Check(visitMode is null or "return" or "new", "visit_mode", "return أو new").ThrowIfInvalid();
        var prior = op.IsOffline ? null : await ReturnCandidateAsync(patient.PatientId, doctor.DoctorId, ct);
        if (prior is not null && visitMode is null) throw await ReturnOrNewRequiredAsync(prior, ct);
        if (visitMode == "return" && prior is null)
            throw new AppException(409, "NO_RETURN_AVAILABLE", "لا توجد عودة بالنتائج لهذا المراجع عند هذا الطبيب", "سجّله زيارة جديدة");
        if (visitMode == "return" && prior!.Status == TicketStatuses.AwaitingResults && prior.Session!.SessionDate == op.Today)
        {
            // نفس اليوم: نفس الدور ونفس الكشفية، بلا فاتورة ولا رقم جديد
            await using var rtx = await db.BeginTransactionAsync(ct);
            await engine.LockSessionAsync(prior.SessionId, ct);
            // بعد القفل: ضغطة متزامنة ربما أعادته للتو
            if (await db.QueueTickets.AsNoTracking().Where(t => t.TicketId == prior.TicketId).Select(t => t.Status).FirstAsync(ct) != TicketStatuses.AwaitingResults)
                throw new AppException(409, "ALREADY_IN_QUEUE", "المريض في الطابور مسبقاً");
            var returned = await ReturnSameTicketAsync(prior, prior.Session!, false, "check_in", ct);
            if (rtx is not null) await rtx.CommitAsync(ct);
            return returned;
        }
        var returnNew = visitMode == "return"; // يوم أو وجبة أخرى: دور جديد بكشفية مجانية مربوط بالأصلي

        // C2: الأوفلاين يُطبق بوقت حدوثه (op.Today/op.Now) لا بوقت المزامنة
        var today = op.Today;
        if (!op.IsOffline) await closures.EnsureOpenAsync(doctor.ClinicId, today, ct);
        await doctors.EnsureSessionsAsync(doctor.DoctorId, today, today, ct, allowPast: op.IsOffline);
        var now = op.Now;
        var daySessions = await db.DoctorSessions.AsNoTracking()
            .Where(s => s.DoctorId == doctor.DoctorId && s.SessionDate == today && s.Status != SessionStatuses.Cancelled)
            .OrderBy(s => s.StartTime).ToListAsync(ct);
        var autoClose = await settings.GetLongAsync(SettingsReader.AutoCloseAfterMinutes, current.BranchId, 120, ct);
        var sessionId = daySessions.FirstOrDefault(s => QueueEngine.SessionEndUtc(s) > now)?.SessionId
            // انتهى الوقت المخطط والطبيب ما زال يعاين: يبقى التسجيل مفتوحاً حتى «إغلاق التسجيل» أو الإغلاق التلقائي
            // (الأوفلاين يُقبل دائماً في آخر وجبة بدأت قبل وقت حدوثه)
            ?? daySessions.LastOrDefault(s => QueueEngine.SessionStartUtc(s) <= now
                                               && (op.IsOffline || (s.RegistrationClosedAt is null && QueueEngine.SessionEndUtc(s).AddMinutes(autoClose) > now)))?.SessionId
            ?? throw new AppException(409, "NO_ACTIVE_SESSION", "لا توجد وجبة متاحة للطبيب اليوم",
                daySessions.Count > 0 ? "انتهت وجبات اليوم أو أُغلق التسجيل. يمكن «تمديد اليوم» أو فتح التسجيل من شاشة الطابور" : null);

        await using var tx = await db.BeginTransactionAsync(ct);
        await engine.LockSessionAsync(sessionId, ct);
        var session = await db.DoctorSessions.FirstAsync(s => s.SessionId == sessionId, ct);

        var booked = await db.Bookings.FirstOrDefaultAsync(b => b.SessionId == session.SessionId && b.PatientId == patient.PatientId
                                                                && b.Status == BookingStatuses.Booked, ct);
        if (booked is not null && op.IsOffline)
        {
            // #5: حضور أوفلاين لمريض محجوز في نفس الوجبة: يُدمج مع حجزه بدل الرفض (المريض حاضر فعلاً)
            var merged = await CheckInBookingAsync(booked.BookingId, ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return merged;
        }
        if (booked is not null)
            throw new AppException(409, "PATIENT_HAS_BOOKING", "المريض محجوز في هذه الوجبة",
                "ثبّت حضوره عبر booking_id بدل الحضور المباشر") { Extensions = new Dictionary<string, object?> { ["booking_id"] = booked.BookingId } };
        if (await db.QueueTickets.AnyAsync(t => t.SessionId == session.SessionId && t.PatientId == patient.PatientId
                                                && TicketStatuses.Active.Contains(t.Status), ct))
            throw new AppException(409, "ALREADY_IN_QUEUE", "المريض في الطابور مسبقاً");

        // العائد بالنتائج حقه قائم: لا يُمنع بإغلاق التسجيل ولا بالسعة
        if (session.RegistrationClosedAt is not null && !op.IsOffline && !returnNew)
            throw new AppException(409, "REGISTRATION_CLOSED", "أُغلق التسجيل لهذه الوجبة", "يمكن فتحه من شاشة الطابور");
        var isExtra = !returnNew && await CapacityCheckAsync(session, doctor, req.AllowExtra == true, now, ct);

        var visitKind = returnNew ? VisitKinds.FollowUp
            : await engine.ResolveVisitKindAsync(req.VisitKind, patient.PatientId, doctor.DoctorId, today, staffMayOverride: true, ct);
        Service service;
        try { service = await engine.ConsultationServiceAsync(session.ClinicId, visitKind, ct); }
        catch (AppException e) when (returnNew && e.Code == "SERVICE_NOT_CONFIGURED")
        {
            service = await engine.ConsultationServiceAsync(session.ClinicId, VisitKinds.NewVisit, ct); // العودة مجانية: أي كشفية للعيادة
        }
        var sequence = await engine.NextSequenceAsync(session.SessionId, ct);
        var ticket = new QueueTicket
        {
            TicketId = ticketId,
            BranchId = session.BranchId,
            SessionId = session.SessionId,
            DoctorId = doctor.DoctorId,
            PatientId = patient.PatientId,
            TicketNumber = await engine.TicketNumberAsync(doctor, sequence, ct),
            SequenceNumber = sequence,
            QueuePosition = sequence,
            Source = "walk_in",
            Status = TicketStatuses.Arrived,
            ArrivedAt = now,
            CreatedBy = current.UserId,
            CreatedOfflineAt = req.CreatedOfflineAt,
            CreatedAt = now,
            IsExtra = isExtra,
            ExtraApprovedBy = isExtra ? current.UserId : null,
        };
        if (returnNew)
        {
            ticket.Priority = TicketPriorities.Returning;
            ticket.ReturnedAt = now;
            ticket.OriginalTicketId = prior!.TicketId;
        }
        db.QueueTickets.Add(ticket);
        var price = returnNew ? 0 : QueueEngine.ConsultationPrice(doctor, service, visitKind);
        var invoice = await engine.CreateConsultationInvoiceAsync(ticket, doctor, service, visitKind, price, alreadyPaid: false, ct);
        audit.Add("CHECKED_IN", "queue_ticket", ticket.TicketId, after: new { source = "walk_in", invoice.InvoiceNumber, is_extra = isExtra, offline = op.IsOffline });
        if (prior is not null)
        {
            if (returnNew && prior.Status == TicketStatuses.AwaitingResults)
            {
                // انتظار من يوم سابق لم يُغلق بعد: العودة تُنهيه
                prior.Status = TicketStatuses.Completed;
                prior.CompletedAt = now;
                if (prior.BookingId is { } pb) (await db.Bookings.FirstAsync(b => b.BookingId == pb, ct)).Status = BookingStatuses.Completed;
            }
            audit.Add(returnNew ? "TICKET_RETURN_NEW_SESSION" : "VISIT_NEW_INSTEAD_OF_RETURN", "queue_ticket", ticket.TicketId,
                after: await ConversionLogAsync(returnNew ? "return_new_session" : "new_visit", ticket, prior, now, ct));
        }
        await db.SaveChangesAsync(ct);
        await engine.RecalculateAsync(session, ct);
        if (tx is not null) await tx.CommitAsync(ct);

        await notifier.SessionChangedAsync(session.SessionId, ct);
        return await ResponseAsync(ticket, invoice.Items[0], ct);
    }

    private async Task<CheckInResponse> ResponseAsync(QueueTicket t, InvoiceItem item, CancellationToken ct)
    {
        var status = QueueEngine.PaymentStatusOf(item);
        var due = status is "paid" or "deferred" ? 0 : item.NetPrice;
        // 2.3-ب: عربون هذا الحجز (booking_deposit) يُطرح من المطلوب، بحد رصيد المريض المتاح فعلاً (قد يكون استُخدم في تحصيل آخر)
        var deposit = 0L;
        if (due > 0 && t.BookingId is { } bookingId)
        {
            var booked = await db.PatientCredits.Where(c => c.PatientId == t.PatientId && c.BookingId == bookingId && c.Kind == "booking_deposit")
                .SumAsync(c => (long?)c.Amount, ct) ?? 0;
            if (booked > 0)
            {
                var balance = await db.PatientCredits.Where(c => c.PatientId == t.PatientId).SumAsync(c => (long?)c.Amount, ct) ?? 0;
                deposit = Math.Clamp(Math.Min(booked, balance), 0, due);
            }
        }
        var issued = t.ArrivedAt ?? op.Now;
        return new CheckInResponse(t.TicketId, t.TicketNumber, t.Status, t.QueuePosition, t.EstimatedTime,
            item.InvoiceId, item.ItemType, due - deposit, status, t.IsExtra)
        {
            IssuedDateAr = ArabicFormat.Date(issued), IssuedTimeAr = ArabicFormat.Time(issued),
            EstimatedTimeAr = t.EstimatedTime is { } eta ? ArabicFormat.Time(eta) : null,
            DepositCredit = deposit,
        };
    }

    /// <summary>
    /// H4: السعة المرنة. الغائبون والمتخطون والملغون لا يُحسبون، ومقاعد الحجز الإلكتروني غير المحجوزة محمية حتى بداية الوجبة.
    /// فوق السعة: «ممنوع الإضافي» يرفض، وإلا يطلب تأكيد الاستقبال (allow_extra) ضمن حد الإضافي للفرع. الأوفلاين يُقبل دائماً.
    /// يعيد true إن كانت التذكرة «إضافي».
    /// </summary>
    private async Task<bool> CapacityCheckAsync(DoctorSession session, Doctor doctor, bool allowExtra, DateTimeOffset now, CancellationToken ct)
    {
        var occupied = await db.QueueTickets.CountAsync(t => t.SessionId == session.SessionId
            && t.Status != TicketStatuses.Cancelled && t.Status != TicketStatuses.NoShow && t.Status != TicketStatuses.Skipped, ct);
        var reserved = 0;
        if (now < QueueEngine.SessionStartUtc(session))
        {
            var onlineBooked = await db.Bookings.CountAsync(b => b.SessionId == session.SessionId && b.Source == AlRahma.Application.Bookings.BookingService.SourceOnline
                && (b.Status == BookingStatuses.Booked || b.Status == BookingStatuses.CheckedIn), ct);
            reserved = Math.Max(0, session.OnlineQuota - onlineBooked);
        }
        if (occupied + reserved < session.CapacityTotal) return false;
        if (op.IsOffline) return true; // حدث فعلاً أثناء الانقطاع
        if (!doctor.AllowExtra)
            throw new AppException(409, "SESSION_FULL", "اكتملت سعة الوجبة", "الطبيب لا يقبل مراجعين إضافيين؛ احجز له أقرب يوم متاح");
        var maxPercent = await settings.GetLongAsync(SettingsReader.ExtraMaxPercent, session.BranchId, 50, ct);
        var extras = await db.QueueTickets.CountAsync(t => t.SessionId == session.SessionId && t.IsExtra
            && t.Status != TicketStatuses.Cancelled && t.Status != TicketStatuses.NoShow, ct);
        if (maxPercent > 0 && extras >= Math.Max(1, session.CapacityTotal * maxPercent / 100))
            throw new AppException(409, "SESSION_FULL", "بلغ عدد الإضافي الحد المسموح لهذه الوجبة", "يمكن لمدير الفرع رفع الحد queue.extra_max_percent");
        if (!allowExtra)
        {
            var avg = await engine.AverageConsultMinutesAsync(session.DoctorId, ct) ?? session.SlotMinutes;
            var start = QueueEngine.WorkStartUtc(session);
            var expectedEnd = (now > start ? now : start).AddMinutes((occupied - await CompletedCountAsync(session.SessionId, ct) + 1) * avg);
            throw new AppException(409, "SESSION_FULL_EXTRA_POSSIBLE", "اكتملت السعة: يمكن قبوله «إضافي»", "أعد الإرسال مع allow_extra = true بعد التأكيد")
            {
                Extensions = new Dictionary<string, object?>
                {
                    ["expected_end"] = expectedEnd, ["scheduled_end"] = QueueEngine.SessionEndUtc(session), ["extras_count"] = extras,
                },
            };
        }
        return true;
    }

    private Task<int> CompletedCountAsync(Guid sessionId, CancellationToken ct) =>
        db.QueueTickets.CountAsync(t => t.SessionId == sessionId && t.Status == TicketStatuses.Completed, ct);

    // ------------------------------------------------------------------ يوم العيادة المرن

    /// <summary>«الطبيب وصل»: الأوقات التقديرية تُحسب من وصوله الفعلي إن تأخر. الحجوزات والجدول لا تتغير.</summary>
    public Task<SessionControlDto> DoctorArrivedAsync(Guid sessionId, CancellationToken ct) =>
        SessionChangeAsync(sessionId, "DOCTOR_ARRIVED", s => s.DoctorArrivedAt ??= op.Now, ct);

    /// <summary>«تمديد اليوم»: نهاية فعلية جديدة لهذه الوجبة فقط (الجدول الأسبوعي لا يتغير). تقبل التقصير أيضاً.</summary>
    public Task<SessionControlDto> ExtendAsync(Guid sessionId, ExtendSessionRequest req, CancellationToken ct)
    {
        new Validator().Check(TimeOnly.TryParseExact(req.End ?? "", "HH:mm", out _), "end", "الوقت بصيغة HH:mm").ThrowIfInvalid();
        var end = TimeOnly.ParseExact(req.End!, "HH:mm");
        return SessionChangeAsync(sessionId, "SESSION_EXTENDED", s =>
        {
            if (end <= s.StartTime) throw AppException.Validation(new Dictionary<string, string[]> { ["end"] = ["بعد بداية الوجبة"] });
            s.ExtendedEndTime = end == s.EndTime ? null : end;
            s.RegistrationClosedAt = null; // التمديد يعيد فتح التسجيل
        }, ct);
    }

    /// <summary>«إغلاق التسجيل» / فتحه: لا مراجعين جدد بدون حجز. المحجوزون والطابور الحالي لا يتأثرون.</summary>
    public Task<SessionControlDto> RegistrationAsync(Guid sessionId, SessionRegistrationRequest req, CancellationToken ct)
    {
        new Validator().Check(req.Closed is not null, "closed", "true أو false").ThrowIfInvalid();
        return SessionChangeAsync(sessionId, req.Closed == true ? "REGISTRATION_CLOSED" : "REGISTRATION_OPENED",
            s => s.RegistrationClosedAt = req.Closed == true ? s.RegistrationClosedAt ?? op.Now : null, ct);
    }

    private async Task<SessionControlDto> SessionChangeAsync(Guid sessionId, string action, Action<DoctorSession> change, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await engine.LockSessionAsync(sessionId, ct);
        var session = await db.DoctorSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId && s.BranchId == current.BranchId, ct)
                      ?? throw new AppException(404, "SESSION_NOT_FOUND", "الوجبة غير موجودة");
        await EnsureScopeAsync(await LoadDoctorAsync(session.DoctorId, ct), ct);
        var before = new { session.DoctorArrivedAt, end = session.EffectiveEndTime.ToString("HH:mm"), closed = session.RegistrationClosedAt is not null };
        change(session);
        audit.Add(action, "doctor_session", session.SessionId, before: before,
            after: new { session.DoctorArrivedAt, end = session.EffectiveEndTime.ToString("HH:mm"), closed = session.RegistrationClosedAt is not null });
        await db.SaveChangesAsync(ct);
        await engine.RecalculateAsync(session, ct);
        if (tx is not null) await tx.CommitAsync(ct);
        await notifier.SessionChangedAsync(session.SessionId, ct);
        return new SessionControlDto(session.SessionId, session.StartTime.ToString("HH:mm"), session.EffectiveEndTime.ToString("HH:mm"),
            session.EndTime.ToString("HH:mm"), session.DoctorArrivedAt, session.RegistrationClosedAt is not null);
    }

    // ------------------------------------------------------------------ أزرار الطابور

    public Task<TicketActionResponse> CallAsync(Guid ticketId, CancellationToken ct) => LockedAsync(ticketId, async () =>
    {
        var (ticket, _, _) = await LoadTicketAsync(ticketId, ct);
        await EnsureCallAllowedAsync(ticket.BranchId, ct);
        return await CallCoreAsync(ticketId, ct);
    }, ct);

    /// <summary>
    /// M3: كل أزرار الطابور تقفل سجل الوجبة قبل قراءة التذكرة وتعديلها وإعادة الترتيب، فضغطتان متزامنتان
    /// (نقر مزدوج، أو الطبيب والاستقبال معاً) تُنفذان بالتتابع: الثانية ترى نتيجة الأولى.
    /// </summary>
    private async Task<T> LockedAsync<T>(Guid ticketId, Func<Task<T>> action, CancellationToken ct)
    {
        var sessionId = await db.QueueTickets.AsNoTracking().Where(t => t.TicketId == ticketId && t.BranchId == current.BranchId)
                            .Select(t => (Guid?)t.SessionId).FirstOrDefaultAsync(ct)
                        ?? throw new AppException(404, "TICKET_NOT_FOUND", "التذكرة غير موجودة");
        await using var tx = await db.BeginTransactionAsync(ct);
        await engine.LockSessionAsync(sessionId, ct);
        var result = await action();
        if (tx is not null) await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// إعداد الفرع queue.call_next_by يحدد من يستدعي المرضى (استدعاء، إعادة استدعاء، التالي، الطوارئ):
    /// doctor = الطبيب فقط، reception = الاستقبال فقط، both = الاثنان. المدير العام ومدير الفرع مسموح لهما دائماً.
    /// الاستدعاء التلقائي (queue.auto_call_next) يعمل مستقلاً عن هذا الإعداد.
    /// </summary>
    public async Task EnsureCallAllowedAsync(Guid branchId, CancellationToken ct)
    {
        if (current.Role is not (Roles.Doctor or Roles.Receptionist)) return;
        var policy = await settings.CallPolicyAsync(branchId, ct);
        if (current.Role == Roles.Doctor && policy.CallNextBy == SettingsReader.CallNextByValues.Reception)
            throw CallNotAllowed("استدعاء المرضى في هذا الفرع من صلاحية موظف الاستقبال فقط", policy);
        if (current.Role == Roles.Receptionist && policy.CallNextBy == SettingsReader.CallNextByValues.Doctor)
            throw CallNotAllowed("استدعاء المرضى في هذا الفرع من صلاحية الطبيب فقط", policy);
    }

    private static AppException CallNotAllowed(string message, CallPolicyDto policy) =>
        new(403, "CALL_NOT_ALLOWED_FOR_ROLE", message, "يمكن لمدير الفرع تغيير الإعداد queue.call_next_by")
            { Extensions = new Dictionary<string, object?> { ["call_next_by"] = policy.CallNextBy } };

    private async Task<TicketActionResponse> CallCoreAsync(Guid ticketId, CancellationToken ct)
    {
        var (ticket, session, _) = await LoadTicketAsync(ticketId, ct);
        if (ticket.Status is not (TicketStatuses.Arrived or TicketStatuses.Called))
            throw InvalidTransition(ticket, "استدعاء");
        await EnsurePaidAsync(ticket, ct);
        ticket.Status = TicketStatuses.Called;
        ticket.CalledAt = op.Now;
        // كل استدعاء (أو إعادة استدعاء) يبدأ تنبيهاً جديداً ينتظر "أنا قادم"
        ticket.CallAcknowledgedAt = null;
        ticket.CallAlertsSent = 1;
        ticket.LastCallAlertAt = ticket.CalledAt;
        await db.SaveChangesAsync(ct);
        await engine.RecalculateAsync(session, ct);
        await notifier.TicketCalledAsync(ticket, ct);
        await notifier.SessionChangedAsync(session.SessionId, ct);
        return ToAction(ticket);
    }

    public async Task<TicketActionResponse> CallNextAsync(Guid doctorId, CancellationToken ct)
    {
        var live = await reader.CurrentSessionAsync(doctorId, op.Today, ct);
        if (live is null) return await CallNextAsync(doctorId, true, ct); // يرمي QUEUE_EMPTY بعد فحص الصلاحية
        await using var tx = await db.BeginTransactionAsync(ct);
        await engine.LockSessionAsync(live.SessionId, ct);
        var result = await CallNextAsync(doctorId, true, ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>enforcePolicy = false للاستدعاء التلقائي من النظام بعد إنهاء المعاينة.</summary>
    private async Task<TicketActionResponse> CallNextAsync(Guid doctorId, bool enforcePolicy, CancellationToken ct)
    {
        var doctor = await LoadDoctorAsync(doctorId, ct);
        await EnsureScopeAsync(doctor, ct);
        if (enforcePolicy) await EnsureCallAllowedAsync(doctor.Clinic!.BranchId, ct);
        var session = await reader.CurrentSessionAsync(doctor.DoctorId, op.Today, ct)
                      ?? throw new AppException(404, "QUEUE_EMPTY", "لا يوجد مريض في الانتظار");
        // الترتيب يعتمد على الوقت (موعد المحجوز يحين): يُعاد حسابه قبل اختيار التالي
        await engine.RecalculateAsync(session, ct);
        var tickets = await db.QueueTickets.Where(t => t.SessionId == session.SessionId).ToListAsync(ct);
        var paidIds = await engine.PaidTicketIdsAsync(tickets.Select(t => t.TicketId).ToList(), ct);
        var next = QueueEngine.Ordered(tickets)
                       .FirstOrDefault(t => t.Status == TicketStatuses.Arrived
                                            && (t.Priority is TicketPriorities.Emergency or TicketPriorities.Returning || paidIds.Contains(t.TicketId)))
                   ?? throw new AppException(404, "QUEUE_EMPTY", "لا يوجد مريض جاهز (واصل ومدفوع) في الانتظار");
        return await CallCoreAsync(next.TicketId, ct);
    }

    public Task<TicketActionResponse> StartAsync(Guid ticketId, CancellationToken ct) => LockedAsync(ticketId, () => StartCoreAsync(ticketId, ct), ct);

    private async Task<TicketActionResponse> StartCoreAsync(Guid ticketId, CancellationToken ct)
    {
        var (ticket, session, _) = await LoadTicketAsync(ticketId, ct);
        if (ticket.Status is not (TicketStatuses.Called or TicketStatuses.Arrived))
            throw InvalidTransition(ticket, "بدء المعاينة");
        await EnsurePaidAsync(ticket, ct);
        if (await db.QueueTickets.AnyAsync(t => t.DoctorId == ticket.DoctorId && t.Status == TicketStatuses.InRoom && t.TicketId != ticket.TicketId, ct))
            throw new AppException(409, "DOCTOR_BUSY", "يوجد مريض داخل الغرفة", "أنهِ المعاينة الحالية أولاً");
        var now = op.Now;
        ticket.Status = TicketStatuses.InRoom;
        ticket.CalledAt ??= now;
        ticket.StartedAt = now;
        await db.SaveChangesAsync(ct);
        await engine.RecalculateAsync(session, ct);
        await notifier.SessionChangedAsync(session.SessionId, ct);
        return ToAction(ticket);
    }

    public async Task<TicketActionResponse> CompleteAsync(Guid ticketId, CancellationToken ct) => await CompleteAsync(ticketId, null, ct);

    public Task<TicketActionResponse> CompleteAsync(Guid ticketId, CompleteTicketRequest? req, CancellationToken ct) =>
        LockedAsync(ticketId, () => CompleteCoreAsync(ticketId, req, ct), ct);

    private async Task<TicketActionResponse> CompleteCoreAsync(Guid ticketId, CompleteTicketRequest? req, CancellationToken ct)
    {
        var (ticket, session, _) = await LoadTicketAsync(ticketId, ct);
        // طبيب لا يستخدم النظام: الاستقبال يستدعي المريض ثم ينهيه مباشرة دون "بدء المعاينة"
        var staffFinish = current.Role != Roles.Doctor && ticket.Status == TicketStatuses.Called;
        if (ticket.Status != TicketStatuses.InRoom && !staffFinish) throw InvalidTransition(ticket, "إنهاء المعاينة");
        // الاستقبال ينهي المعاينة (طبيب لا يستخدم النظام) فقط إذا كان الاستدعاء من صلاحيته
        if (current.Role == Roles.Receptionist) await EnsureCallAllowedAsync(ticket.BranchId, ct);
        // طلب الاستدعاء صراحة يتبع صلاحية الدور، أما التلقائي من إعداد الفرع فيعمل دائماً
        if (req?.CallNext == true) await EnsureCallAllowedAsync(ticket.BranchId, ct);
        var now = op.Now;
        if (staffFinish) ticket.StartedAt ??= ticket.CalledAt ?? now;
        // 1.2-أ: للزيارة تحويل لم يكتمل → «بانتظار قسم» (خارج الترتيب، والحجز لا يكتمل). المعاينة الثانية بعد العودة تُنهي نهائياً.
        var awaiting = req?.ExpectReturn == true;
        if (req?.ExpectReturn is null && ticket.ReturnedAt is null)
        {
            var state = QueueEngine.AwaitingState((await engine.VisitReferralsAsync([ticket.TicketId], ct))[ticket.TicketId]);
            awaiting = state.HasClinical && !state.Ready;
        }
        if (awaiting)
        {
            ticket.Status = TicketStatuses.AwaitingResults;
            ticket.AwaitingSince = now;
            ticket.CompletedAt = null;
            audit.Add("TICKET_AWAITING_RESULTS", "queue_ticket", ticket.TicketId, after: new { expect_return = req?.ExpectReturn });
        }
        else
        {
            ticket.Status = TicketStatuses.Completed;
            ticket.CompletedAt = now;
            if (ticket.BookingId is { } bid)
            {
                var booking = await db.Bookings.FirstAsync(b => b.BookingId == bid, ct);
                booking.Status = BookingStatuses.Completed;
            }
        }
        // الكشفية نُفذت عند أول إنهاء فقط (العودة بالنتائج على نفس الكشفية)
        var item = await engine.ConsultationItemAsync(ticket.TicketId, ct);
        if (item is not null) item.PerformedAt ??= now;
        await db.SaveChangesAsync(ct);
        await engine.RecalculateAsync(session, ct);
        await notifier.SessionChangedAsync(session.SessionId, ct);
        var done = ToAction(ticket);
        var callNext = req?.CallNext ?? await settings.GetLongAsync(SettingsReader.AutoCallNext, ticket.BranchId, 0, ct) == 1;
        if (!callNext) return done;
        try
        {
            return done with { NextTicket = await CallNextAsync(ticket.DoctorId, false, ct) };
        }
        catch (AppException e) when (e.Code == "QUEUE_EMPTY")
        {
            return done; // لا يوجد مريض واصل ومدفوع: يبقى الاستدعاء يدوياً
        }
    }

    public Task<NoShowResponse> NoShowAsync(Guid ticketId, NoShowRequest req, CancellationToken ct)
    {
        new Validator().Check(req.Mode is "skip" or "final", "mode", "skip أو final").ThrowIfInvalid();
        return LockedAsync(ticketId, () => NoShowCoreAsync(ticketId, req, ct), ct);
    }

    private async Task<NoShowResponse> NoShowCoreAsync(Guid ticketId, NoShowRequest req, CancellationToken ct)
    {
        var (ticket, session, _) = await LoadTicketAsync(ticketId, ct);
        if (ticket.Status is not (TicketStatuses.Waiting or TicketStatuses.Arrived or TicketStatuses.Called or TicketStatuses.Skipped))
            throw InvalidTransition(ticket, "التخطي");

        var banned = false;
        if (req.Mode == "skip")
        {
            ticket.Status = TicketStatuses.Skipped;
            audit.Add("TICKET_SKIPPED", "queue_ticket", ticket.TicketId);
        }
        else
        {
            ticket.Status = TicketStatuses.NoShow;
            if (ticket.BookingId is { } bid)
            {
                var booking = await db.Bookings.FirstAsync(b => b.BookingId == bid, ct);
                booking.Status = BookingStatuses.NoShow;
                booking.CountedAsNoShow = true;
            }
            await VoidUnpaidConsultationAsync(ticket, "غياب المريض", ct);
            var patient = await db.Patients.FirstAsync(p => p.PatientId == ticket.PatientId, ct);
            audit.Add("TICKET_NO_SHOW", "queue_ticket", ticket.TicketId);
            banned = await bans.RecordNoShowAsync(patient, ticket.TicketId, ticket.BookingId, ct);
        }
        await db.SaveChangesAsync(ct);
        await engine.RecalculateAsync(session, ct);
        await notifier.SessionChangedAsync(session.SessionId, ct);
        if (ticket.BookingId is { } nb)
            await notifier.BookingChangedAsync(nb, req.Mode == "final" ? BookingChangeActions.NoShow : BookingChangeActions.Skipped, null, ct);
        if (banned) await notifier.PatientBannedAsync(ticket.PatientId, ticket.BranchId, ct);
        return new NoShowResponse(ticket.TicketId, ticket.Status, true, req.Mode == "final", banned);
    }

    public Task<EmergencyResponse> EmergencyAsync(Guid ticketId, EmergencyRequest req, CancellationToken ct)
    {
        new Validator().Check(!string.IsNullOrWhiteSpace(req.Reason), "reason", "سبب الطوارئ مطلوب").ThrowIfInvalid();
        return LockedAsync(ticketId, () => EmergencyCoreAsync(ticketId, req, ct), ct);
    }

    private async Task<EmergencyResponse> EmergencyCoreAsync(Guid ticketId, EmergencyRequest req, CancellationToken ct)
    {
        var (ticket, session, _) = await LoadTicketAsync(ticketId, ct);
        if (ticket.Status is not (TicketStatuses.Arrived or TicketStatuses.Waiting or TicketStatuses.Skipped))
            throw InvalidTransition(ticket, "الطوارئ");
        if (ticket.Status != TicketStatuses.Arrived)
        {
            // مريض طارئ لم يُثبت حضوره بعد: يُعتبر واصلاً الآن وتُنشأ فاتورته (تُحصّل لاحقاً)
            await EnsureInvoiceAsync(ticket, session, ct);
            ticket.Status = TicketStatuses.Arrived;
        }
        var now = op.Now;
        ticket.Priority = TicketPriorities.Emergency;
        ticket.EmergencyReason = req.Reason!.Trim();
        // الترتيب داخل الطوارئ حسب وقت إعلانها: الأقدم أولاً
        ticket.ArrivedAt = now;
        audit.Add("TICKET_EMERGENCY", "queue_ticket", ticket.TicketId, reason: ticket.EmergencyReason);
        await db.SaveChangesAsync(ct);
        var tickets = await engine.RecalculateAsync(session, ct);
        await notifier.SessionChangedAsync(session.SessionId, ct);
        var affected = tickets.Count(t => t.QueuePosition > ticket.QueuePosition);
        return new EmergencyResponse(ticket.TicketId, ticket.Priority, ticket.QueuePosition ?? 1, affected);
    }

    /// <summary>
    /// مريض متخطى أو متأخر وصل: لا يدفع رسوماً جديدة، ويُدرج في أول فجوة بعد وصوله:
    /// خلف كل المرضى الحاضرين المنتظرين (لا يتقدم على من انتظر)، وقبل الحجوزات التي لم يصل أصحابها بعد.
    /// </summary>
    public Task<TicketActionResponse> LateArrivalAsync(Guid ticketId, CancellationToken ct) => LockedAsync(ticketId, () => LateArrivalCoreAsync(ticketId, ct), ct);

    private async Task<TicketActionResponse> LateArrivalCoreAsync(Guid ticketId, CancellationToken ct)
    {
        var (ticket, session, _) = await LoadTicketAsync(ticketId, ct);
        if (ticket.Status is not (TicketStatuses.Skipped or TicketStatuses.Waiting))
            throw InvalidTransition(ticket, "إدراج المتأخر");
        var now = op.Now;
        await EnsureInvoiceAsync(ticket, session, ct);
        if (ticket.BookingId is { } bid)
        {
            var booking = await db.Bookings.FirstAsync(b => b.BookingId == bid, ct);
            if (booking.Status == BookingStatuses.Booked)
            {
                booking.Status = BookingStatuses.CheckedIn;
                booking.CheckedInAt ??= now;
                booking.CheckInMethod ??= CheckInMethods.Reception;
            }
        }

        var others = QueueEngine.Ordered(await db.QueueTickets.Where(t => t.SessionId == session.SessionId && t.TicketId != ticket.TicketId).ToListAsync(ct))
            .Where(t => t.Status is TicketStatuses.Arrived or TicketStatuses.Waiting).ToList();
        // الحاضرون المنتظرون يبقون أمامه
        var ahead = others.FindLastIndex(t => t.Status == TicketStatuses.Arrived) + 1;
        short pos = 1;
        foreach (var t in others)
        {
            if (pos == ahead + 1) pos++; // مكان المتأخر
            t.QueuePosition = pos++;
        }
        ticket.QueuePosition = (short)(ahead + 1);
        ticket.Status = TicketStatuses.Arrived;
        ticket.Priority = ticket.Priority == TicketPriorities.Emergency ? ticket.Priority : TicketPriorities.LateReinserted;
        ticket.ArrivedAt = now;
        audit.Add("TICKET_LATE_REINSERTED", "queue_ticket", ticket.TicketId, after: new { position = ticket.QueuePosition });
        await db.SaveChangesAsync(ct);
        await engine.RecalculateAsync(session, ct);
        await notifier.SessionChangedAsync(session.SessionId, ct);
        if (ticket.BookingId is { } changed) await notifier.BookingChangedAsync(changed, BookingChangeActions.CheckedIn, null, ct);
        return ToAction(ticket);
    }

    // ------------------------------------------------------------------ العائد بالنتائج (التدقيق 1.2، 1.3-د)

    /// <summary>
    /// زر «عاد بالنتائج» على دور «بانتظار قسم» في نفس الوجبة: يعود «واصل» بأولوية returning (بعد الطوارئ)، على نفس الكشفية
    /// والمعاينة، بلا فاتورة ولا رقم جديد. external = نتائج المتبقي من خارج المجمع.
    /// </summary>
    public Task<CheckInResponse> ReturnedAsync(Guid ticketId, ReturnedRequest? req, CancellationToken ct) => LockedAsync(ticketId, async () =>
    {
        var (ticket, session, _) = await LoadTicketAsync(ticketId, ct);
        if (ticket.Status != TicketStatuses.AwaitingResults) throw InvalidTransition(ticket, "العودة بالنتائج");
        if (session.SessionDate != op.Today)
            throw new AppException(409, "RETURN_NEEDS_CHECK_IN", "وجبة هذا الدور انتهت", "ثبّت حضوره من «حضور وتسجيل» واختر «عودة بالنتائج»");
        return await ReturnSameTicketAsync(ticket, session, req?.External == true, "returned_button", ct);
    }, ct);

    private async Task<CheckInResponse> ReturnSameTicketAsync(QueueTicket ticket, DoctorSession session, bool external, string via, CancellationToken ct)
    {
        var now = op.Now;
        var marked = external ? await MarkRemainingExternalAsync(ticket.TicketId, now, ct) : 0;
        var before = new { ticket.Status, ticket.Priority, ticket.AwaitingSince };
        ticket.Status = TicketStatuses.Arrived;
        ticket.Priority = ticket.Priority == TicketPriorities.Emergency ? ticket.Priority : TicketPriorities.Returning;
        ticket.ReturnedAt = now;
        ticket.CalledAt = null;
        ticket.CallAcknowledgedAt = null;
        var log = await ConversionLogAsync("return_same_ticket", ticket, null, now, ct);
        log["via"] = via;
        log["external_marked"] = marked;
        audit.Add("TICKET_RETURNED_WITH_RESULTS", "queue_ticket", ticket.TicketId, before: before, after: log);
        await db.SaveChangesAsync(ct);
        await engine.RecalculateAsync(session, ct);
        await notifier.SessionChangedAsync(session.SessionId, ct);
        var item = await engine.ConsultationItemAsync(ticket.TicketId, ct)
                   ?? throw new AppException(409, "INVALID_TICKET_STATE", "لا توجد كشفية لهذا الدور");
        return await ResponseAsync(ticket, item, ct);
    }

    /// <summary>تحويلات المختبر/الأشعة/السونار المعلقة وغير المدفوعة تصبح «خارجي» (المدفوع يبقى: حقه قائم أو يُسترجع).</summary>
    private async Task<int> MarkRemainingExternalAsync(Guid ticketId, DateTimeOffset now, CancellationToken ct)
    {
        var ids = (await engine.VisitReferralsAsync([ticketId], ct))[ticketId]
            .Where(r => r.Service != ReferralServices.Pharmacy && r.Status is ReferralStatuses.Referred or ReferralStatuses.Activated or ReferralStatuses.Expired)
            .Select(r => r.ReferralId).ToList();
        var rows = await db.Referrals.Where(r => ids.Contains(r.ReferralId)
            && !(r.OrderItemId != null && db.MedicalOrders.Any(m => m.OrderItemId == r.OrderItemId
                && db.InvoiceItems.Any(i => i.InvoiceItemId == m.InvoiceItemId && i.Status != InvoiceItemStatuses.Unpaid && i.Status != InvoiceItemStatuses.Void))))
            .ToListAsync(ct);
        foreach (var r in rows)
        {
            r.Status = ReferralStatuses.External;
            r.ClosedAt = now;
            audit.Add("REFERRAL_EXTERNAL", "referral", r.ReferralId, after: new { r.Service, ticket_id = ticketId });
        }
        return rows.Count;
    }

    /// <summary>دور ينتظر نتائج هذا الطبيب، أو آخر دور له عودة مجانية صالحة لم تُستخدم.</summary>
    private async Task<QueueTicket?> ReturnCandidateAsync(Guid patientId, Guid doctorId, CancellationToken ct)
    {
        var awaiting = await db.QueueTickets.Include(t => t.Session)
            .Where(t => t.PatientId == patientId && t.DoctorId == doctorId && t.Status == TicketStatuses.AwaitingResults)
            .OrderByDescending(t => t.AwaitingSince).FirstOrDefaultAsync(ct);
        if (awaiting is not null) return awaiting;
        var now = op.Now;
        return await db.QueueTickets.Include(t => t.Session)
            .Where(t => t.PatientId == patientId && t.DoctorId == doctorId && t.Status == TicketStatuses.Completed
                        && t.FreeReturnUntil != null && t.FreeReturnUntil > now
                        && !db.QueueTickets.Any(x => x.OriginalTicketId == t.TicketId && x.Status != TicketStatuses.Cancelled))
            .OrderByDescending(t => t.FreeReturnUntil).FirstOrDefaultAsync(ct);
    }

    private async Task<AppException> ReturnOrNewRequiredAsync(QueueTicket prior, CancellationToken ct)
    {
        var state = QueueEngine.AwaitingState((await engine.VisitReferralsAsync([prior.TicketId], ct))[prior.TicketId]);
        var awaiting = prior.Status == TicketStatuses.AwaitingResults;
        return new AppException(409, "RETURN_OR_NEW_REQUIRED",
            awaiting ? "المراجع بانتظار نتائج هذا الطبيب" : "للمراجع عودة مجانية بالنتائج عند هذا الطبيب",
            "اختر «عودة بالنتائج» (visit_mode=return، بلا رسوم) أو «زيارة جديدة» (visit_mode=new)")
        {
            Extensions = new Dictionary<string, object?>
            {
                ["ticket_id"] = prior.TicketId, ["ticket_number"] = prior.TicketNumber, ["ticket_status"] = prior.Status,
                ["session_date"] = prior.Session?.SessionDate, ["awaiting_since"] = prior.AwaitingSince, ["free_return_until"] = prior.FreeReturnUntil,
                ["pending_services"] = state.Pending, ["pending_services_ar"] = state.Pending.Select(QueueEngine.ServiceAr).ToList(),
                ["ready_to_return"] = state.Ready, ["same_ticket"] = awaiting && prior.Session?.SessionDate == op.Today,
                ["options"] = new[] { "return", "new" }, ["default"] = "return",
            },
        };
    }

    /// <summary>سجل التحويل إلى عائد أو زيارة جديدة (قرار المالك 3): الموظف ووقت العملية والتذاكر.</summary>
    private async Task<Dictionary<string, object?>> ConversionLogAsync(string mode, QueueTicket ticket, QueueTicket? original, DateTimeOffset at, CancellationToken ct)
    {
        var staff = current.StaffUserId is { } uid
            ? await db.Users.AsNoTracking().Where(u => u.UserId == uid).Select(u => u.FullName).FirstOrDefaultAsync(ct)
            : null;
        return new Dictionary<string, object?>
        {
            ["mode"] = mode, ["ticket_id"] = ticket.TicketId, ["ticket_number"] = ticket.TicketNumber,
            ["original_ticket_id"] = original?.TicketId, ["original_ticket_number"] = original?.TicketNumber,
            ["staff_user_id"] = current.StaffUserId, ["staff_name"] = staff, ["at"] = at,
        };
    }

    // ------------------------------------------------------------------ مساعدات

    private async Task EnsureInvoiceAsync(QueueTicket ticket, DoctorSession session, CancellationToken ct)
    {
        if (await engine.ConsultationItemAsync(ticket.TicketId, ct) is not null) return;
        var doctor = await db.Doctors.FirstAsync(d => d.DoctorId == ticket.DoctorId, ct);
        string visitKind;
        long price;
        var paid = false;
        if (ticket.BookingId is { } bid)
        {
            var booking = await db.Bookings.FirstAsync(b => b.BookingId == bid, ct);
            visitKind = booking.VisitKind;
            price = booking.Price;
            paid = booking.PaymentStatus == PaymentStatuses.Paid;
        }
        else
        {
            visitKind = await engine.ResolveVisitKindAsync(null, ticket.PatientId, ticket.DoctorId, session.SessionDate, true, ct);
            price = QueueEngine.ConsultationPrice(doctor, await engine.ConsultationServiceAsync(session.ClinicId, visitKind, ct), visitKind);
        }
        var service = await engine.ConsultationServiceAsync(session.ClinicId, visitKind, ct);
        await engine.CreateConsultationInvoiceAsync(ticket, doctor, service, visitKind, price, paid, ct);
    }

    private async Task VoidUnpaidConsultationAsync(QueueTicket ticket, string reason, CancellationToken ct)
    {
        var item = await engine.ConsultationItemAsync(ticket.TicketId, ct);
        if (item is null || item.Status != InvoiceItemStatuses.Unpaid) return;
        var invoice = await db.Invoices.Include(i => i.Items).FirstAsync(i => i.InvoiceId == item.InvoiceId, ct);
        if (invoice.PaidAmount > 0) return;
        item.Status = InvoiceItemStatuses.Void;
        if (invoice.Items.All(i => i.Status == InvoiceItemStatuses.Void))
        {
            invoice.Status = InvoiceStatuses.Void;
            invoice.VoidReason = reason;
            invoice.VoidedAt = clock.UtcNow;
            invoice.VoidedBy = current.UserId;
        }
    }

    /// <summary>لا يدخل المريض قبل دفع الكشفية، عدا الطوارئ والدفع الآجل المعتمد والعائد بالنتائج (على كشفيته الأولى).</summary>
    private async Task EnsurePaidAsync(QueueTicket ticket, CancellationToken ct)
    {
        if (ticket.Priority is TicketPriorities.Emergency or TicketPriorities.Returning) return;
        var item = await engine.ConsultationItemAsync(ticket.TicketId, ct);
        var status = QueueEngine.PaymentStatusOf(item);
        if (status is "paid" or "deferred") return;
        throw new AppException(402, "INVOICE_UNPAID", "الكشفية غير مدفوعة", "يُحصّل المبلغ من الاستقبال قبل دخول المريض")
        {
            Extensions = new Dictionary<string, object?> { ["invoice_id"] = item?.InvoiceId, ["amount_due"] = item?.NetPrice },
        };
    }

    private async Task<(QueueTicket Ticket, DoctorSession Session, Doctor Doctor)> LoadTicketAsync(Guid ticketId, CancellationToken ct)
    {
        var ticket = await db.QueueTickets.FirstOrDefaultAsync(t => t.TicketId == ticketId && t.BranchId == current.BranchId, ct)
                     ?? throw new AppException(404, "TICKET_NOT_FOUND", "التذكرة غير موجودة");
        var doctor = await LoadDoctorAsync(ticket.DoctorId, ct);
        await EnsureScopeAsync(doctor, ct);
        var session = await db.DoctorSessions.FirstAsync(s => s.SessionId == ticket.SessionId, ct);
        return (ticket, session, doctor);
    }

    private Task<Doctor> LoadDoctorAsync(Guid doctorId, CancellationToken ct) => LoadDoctorAsync(doctorId, current.BranchId, ct);

    private async Task<Doctor> LoadDoctorAsync(Guid doctorId, Guid branchId, CancellationToken ct) =>
        await db.Doctors.Include(d => d.Clinic)
            .FirstOrDefaultAsync(d => d.DoctorId == doctorId && d.Clinic!.BranchId == branchId, ct)
        ?? throw AppException.NotFound("الطبيب غير موجود في هذا الفرع");

    private async Task<List<Guid>> MyClinicsAsync(CancellationToken ct) =>
        await db.UserClinics.Where(c => c.UserId == current.UserId).Select(c => c.ClinicId).ToListAsync(ct);

    /// <summary>الطبيب يدير طابوره فقط، وموظف الاستقبال عياداته فقط، والمدير كل الفرع.</summary>
    private async Task EnsureScopeAsync(Doctor doctor, CancellationToken ct)
    {
        if (current.Role == Roles.Doctor && doctor.UserId != current.UserId)
            throw AppException.Forbidden("الطبيب يدير طابوره فقط");
        if (current.Role == Roles.Receptionist && !(await MyClinicsAsync(ct)).Contains(doctor.ClinicId))
            throw AppException.Forbidden("هذه العيادة ليست من عياداتك");
    }

    private static AppException InvalidTransition(QueueTicket t, string action) =>
        new(409, "INVALID_TICKET_STATE", "لا يمكن تنفيذ العملية على التذكرة بحالتها الحالية", $"{action} غير متاح والتذكرة بحالة {t.Status}");

    private static TicketActionResponse ToAction(QueueTicket t) =>
        new(t.TicketId, t.TicketNumber, t.Status, t.Priority, t.QueuePosition, t.EstimatedTime, t.CalledAt, t.StartedAt, t.CompletedAt);
}
