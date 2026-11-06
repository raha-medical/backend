using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Doctors;
using AlRahma.Application.Files;
using AlRahma.Application.Patients;
using AlRahma.Application.Queue;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Bookings;

public record CreateBookingRequest(Guid? BookingId, Guid? PatientId, Guid? SessionId, string? VisitKind);
public record BookingDto(Guid BookingId, Guid PatientId, string PatientName, Guid SessionId, Guid DoctorId, string DoctorName,
    DateOnly Date, short SequenceNumber, string? TicketNumber, DateTimeOffset? EstimatedTime, short? QueuePosition,
    string VisitKind, long Price, string Source, string Status, string PaymentStatus, IReadOnlyList<string> PaymentOptions,
    string? CancelReason, DateTimeOffset CreatedAt, IReadOnlyList<BookingWarning>? Warnings = null)
{
    /// <summary>عدد مرات تغيير موعد الحجز (المرحلة 3).</summary>
    public int RescheduleCount { get; init; }
    /// <summary>فرع الموعد: لزر «التوجّه إلى المجمع» بعد الحجز (المرحلة 4).</summary>
    public Guid? BranchId { get; init; }
    public string? BranchName { get; init; }
    /// <summary>reception | self_qr | null (لم يُثبت حضوره).</summary>
    public string? CheckInMethod { get; init; }
    public DateTimeOffset? CheckedInAt { get; init; }
    /// <summary>patient | staff | system | null.</summary>
    public string? CancelledBy { get; init; }
    /// <summary>5.1-ب: credit | rebooked | refunded | null.</summary>
    public string? CancelResolution { get; init; }
    /// <summary>ألغى الطبيب جدوله وصار المدفوع رصيداً، والمراجع لم يختر بعد: اعرض الخيارات الثلاثة.</summary>
    public bool CancelOptionsPending { get; init; }
    public Guid? RebookedBookingId { get; init; }
}
/// <summary>تنبيه لا يمنع الحجز، مثل BOOKING_TIME_OVERLAP (حجز آخر عند طبيب آخر قريب من نفس الوقت).</summary>
public record BookingWarning(string Code, string Message, Guid? BookingId = null);
/// <summary>waive_fee + waive_reason: إعفاء الموظف (bookings.policy_override) من رسوم الإلغاء المتأخر واحتسابه غياباً.</summary>
public record CancelBookingRequest(string? Reason, bool? WaiveFee = null, string? WaiveReason = null);
public record CancelBookingResponse(string Status, bool CountedAsNoShow, long CreditAdded)
{
    /// <summary>الإلغاء داخل مدة booking.cancel_min_hours قبل الموعد.</summary>
    public bool Late { get; init; }
    /// <summary>رسوم الإلغاء المتأخر المضافة لسلة المريض في الفرع (0 = لا رسوم).</summary>
    public long FeeAmount { get; init; }
    public Guid? FeeInvoiceId { get; init; }
    public Guid? FeeInvoiceItemId { get; init; }
    public bool FeeWaived { get; init; }
    /// <summary>المبلغ المُرجع لوسيلة الدفع الإلكتروني (booking.cancel_paid_to = refund).</summary>
    public long RefundAmount { get; init; }
    /// <summary>credit | refund | none.</summary>
    public string PaidAmountTo { get; init; } = "none";
}

/// <summary>override_policy + override_reason: تجاوز الموظف (bookings.policy_override) لحد ومهلة تغيير الموعد.</summary>
public record RescheduleBookingRequest(Guid? SessionId, string? Reason = null, bool? OverridePolicy = null, string? OverrideReason = null);

/// <summary>سياسة الإلغاء وتغيير الموعد الفعالة لفرع (إعداد الفرع، وإلا العام، وإلا الافتراضي).</summary>
public record BookingPolicyDto(long FreeCancelHours, long LateCancelFee, bool LateCancelCountsAsNoShow, long MaxReschedules,
    long RescheduleMinHours, string CancelPaidTo);

public record CancelPreviewDto(Guid BookingId, bool Allowed, string? BlockedCode, string? BlockedReasonAr, DateTimeOffset AppointmentAt,
    DateTimeOffset FreeCancelUntil, double HoursBeforeAppointment, long FreeCancelHours, bool IsLate, long FeeAmount, bool CountsAsNoShow,
    long PaidAmount, string PaidAmountTo, long CreditAmount, long RefundAmount, long DepositCredit, bool CanWaive,
    IReadOnlyList<string> ConditionsAr, string SummaryAr, BookingPolicyDto Policy);

public record RescheduleTargetDto(Guid SessionId, Guid DoctorId, DateOnly Date, string Start, string End, DateTimeOffset? EstimatedTime,
    int OnlineRemaining, string Status);

public record ReschedulePreviewDto(Guid BookingId, bool Allowed, string? BlockedCode, string? BlockedReasonAr, int RescheduleCount,
    long MaxReschedules, long RemainingReschedules, long RemainingAfter, long MinNoticeHours, DateTimeOffset AppointmentAt,
    DateTimeOffset RescheduleDeadline, long FeeAmount, long PaidAmount, string PaymentEffect, bool CanOverride, RescheduleTargetDto? Target,
    IReadOnlyList<string> ConditionsAr, BookingPolicyDto Policy);

public record PublicDoctorDto(Guid DoctorId, string FullName, string Specialty, string? JobTitle, string ClinicName, string? PhotoUrl,
    long? NewVisitPrice, long? FollowUpPrice);
public record AvailabilityDay(DateOnly Date, Guid SessionId, string Start, string End, int CapacityTotal, int OnlineRemaining,
    int NextSequenceNumber, DateTimeOffset? NextEstimatedTime, string Status, string? CancelReason);
public record AvailabilityDto(Guid DoctorId, IReadOnlyList<AvailabilityDay> Days);

public class BookingService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, QueueEngine engine,
    DoctorService doctors, FileService files, SettingsReader settings, BanService bans, QueueNotifier notifier,
    AlRahma.Application.Branches.ClosureService closures, PatientApp.PatientPortalService portal,
    Microsoft.Extensions.Options.IOptions<OnlinePaymentOptions> paymentOptions, Notifications.NotificationService notifications,
    NumberSequences numbers, PatientApp.Gateways.PaymentGatewayRegistry gateways)
{
    public const int MaxAvailabilityDays = 31;

    /// <summary>طرق الدفع المتاحة للحجز: الهاتفي في العيادة فقط، وحجز التطبيق يضيف الدفع الإلكتروني (القسم 6).</summary>
    public static readonly string[] PhonePaymentOptions = ["pay_at_clinic"];
    public static readonly string[] AppPaymentOptions = ["pay_at_clinic", "pay_full_online", "pay_deposit_online"];
    public const string SourceOnline = "online";
    public const string SourcePhone = "phone";

    // ------------------------------------------------------------------ عام (تطبيق المريض)

    public async Task<IReadOnlyList<PublicDoctorDto>> PublicDoctorsAsync(Guid branchId, string? specialty, string? q, CancellationToken ct)
    {
        // الفرع الموقوف نهائياً لا يظهر في تطبيق المريض
        if (!await db.Branches.AnyAsync(b => b.BranchId == branchId && b.IsActive, ct))
            throw new AppException(404, "BRANCH_NOT_FOUND", "الفرع غير موجود");
        var query = db.Doctors.AsNoTracking().Include(d => d.Clinic)
            .Where(d => d.IsActive && d.Clinic!.BranchId == branchId && d.Clinic.IsActive);
        if (!string.IsNullOrWhiteSpace(specialty))
        {
            // يقبل رمز الاختصاص (internal) أو اسمه (باطنية)
            var spec = specialty.Trim();
            var name = await db.LookupValues.Where(l => l.Category == LookupCategories.DoctorSpecialty && l.Code == spec)
                .Select(l => l.NameAr).FirstOrDefaultAsync(ct) ?? spec;
            query = query.Where(d => d.Specialty == name);
        }
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(d => EF.Functions.ILike(d.FullName, $"%{q.Trim()}%"));
        var list = await query.OrderBy(d => d.FullName).ToListAsync(ct);

        var clinicIds = list.Select(d => d.ClinicId).Distinct().ToList();
        var prices = await db.Services.AsNoTracking()
            .Where(s => s.ClinicId != null && clinicIds.Contains(s.ClinicId.Value) && s.Type == ServiceTypes.Consultation && s.IsActive)
            .Select(s => new { s.ClinicId, s.VisitKind, s.Price }).ToListAsync(ct);
        long? Price(Guid clinic, string kind) => prices.FirstOrDefault(p => p.ClinicId == clinic && p.VisitKind == kind)?.Price;

        return list.Select(d => new PublicDoctorDto(d.DoctorId, d.FullName, d.Specialty, d.JobTitle, d.Clinic!.Name, files.PhotoUrl(d.PhotoObjectKey),
            Price(d.ClinicId, VisitKinds.NewVisit), Price(d.ClinicId, VisitKinds.FollowUp))).ToList();
    }

    public async Task<AvailabilityDto> AvailabilityAsync(Guid doctorId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var doctor = await db.Doctors.AsNoTracking().FirstOrDefaultAsync(d => d.DoctorId == doctorId && d.IsActive
                         && d.Clinic!.IsActive && d.Clinic.Branch!.IsActive, ct)
                     ?? throw AppException.NotFound("الطبيب غير موجود");
        var today = clock.BaghdadToday();
        var start = from is { } f && f > today ? f : today;
        var end = to ?? start.AddDays(6);
        if (end < start || end.DayNumber - start.DayNumber >= MaxAvailabilityDays)
            throw AppException.Validation(new Dictionary<string, string[]> { ["to"] = [$"الفترة {MaxAvailabilityDays} يوماً كحد أقصى"] });

        var closedDays = await doctors.ClosedWeekdaysAsync(doctor.Clinic?.BranchId
            ?? await db.Clinics.Where(c => c.ClinicId == doctor.ClinicId).Select(c => c.BranchId).FirstAsync(ct), ct);
        // وجبات أيام إغلاق الفرع لا تظهر للحجز (الموجودة منها تبقى في القاعدة وحجوزاتها صالحة)
        var sessions = (await doctors.EnsureSessionsAsync(doctor.DoctorId, start, end, ct))
            .Where(s => !closedDays.Contains((short)s.SessionDate.DayOfWeek)).ToList();
        var ids = sessions.Select(s => s.SessionId).ToList();
        var booked = await db.Bookings.Where(b => ids.Contains(b.SessionId) && b.Status != BookingStatuses.Cancelled)
            .GroupBy(b => b.SessionId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var maxSeq = await db.Bookings.Where(b => ids.Contains(b.SessionId)).GroupBy(b => b.SessionId)
            .Select(g => new { g.Key, Max = g.Max(b => b.SequenceNumber) }).ToDictionaryAsync(x => x.Key, x => (int)x.Max, ct);
        var maxTicket = await db.QueueTickets.Where(t => ids.Contains(t.SessionId)).GroupBy(t => t.SessionId)
            .Select(g => new { g.Key, Max = g.Max(t => t.SequenceNumber) }).ToDictionaryAsync(x => x.Key, x => (int)x.Max, ct);
        var now = clock.UtcNow;

        var days = sessions.Select(s =>
        {
            var remaining = Math.Max(0, s.OnlineQuota - booked.GetValueOrDefault(s.SessionId));
            var next = Math.Max(maxSeq.GetValueOrDefault(s.SessionId), maxTicket.GetValueOrDefault(s.SessionId)) + 1;
            var status = s.Status;
            if (status == SessionStatuses.Open && QueueEngine.SessionEndUtc(s) <= now) status = SessionStatuses.Closed;
            else if (status == SessionStatuses.Open && remaining == 0) status = SessionStatuses.Full;
            DateTimeOffset? eta = status == SessionStatuses.Open ? Max(QueueEngine.ScheduledTime(s, next), now) : null;
            return new AvailabilityDay(s.SessionDate, s.SessionId, s.StartTime.ToString("HH:mm"), s.EndTime.ToString("HH:mm"),
                s.CapacityTotal, remaining, next, eta, status, s.Status == SessionStatuses.Cancelled ? s.CancelReason : null);
        }).ToList();
        return new AvailabilityDto(doctor.DoctorId, days);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    // ------------------------------------------------------------------ الحجز

    public async Task<BookingDto> CreateAsync(CreateBookingRequest req, CancellationToken ct)
    {
        new Validator()
            .Check(req.PatientId is not null, "patient_id", "المريض مطلوب")
            .Check(req.SessionId is not null, "session_id", "الوجبة مطلوبة")
            .ThrowIfInvalid();
        var bookingId = req.BookingId ?? UuidV7.New();
        if (await db.Bookings.AnyAsync(b => b.BookingId == bookingId, ct))
            throw new AppException(409, "BOOKING_EXISTS", "يوجد حجز بنفس المعرف");

        var app = current.IsPatient;
        if (app) await portal.EnsureLinkedAsync(req.PatientId!.Value, ct);
        var patient = await db.Patients.FirstOrDefaultAsync(p => p.PatientId == req.PatientId, ct)
                      ?? throw new AppException(404, "PATIENT_NOT_FOUND", "المريض غير موجود");
        if (patient.MergedIntoPatientId is not null)
            throw new AppException(409, "PATIENT_MERGED", "هذا الملف مدموج في ملف آخر");
        if (patient.IsBanned)
        {
            var deposit = await settings.GetLongAsync(SettingsReader.UnbanDepositAmount, app ? null : current.BranchId, 10000, ct);
            throw new AppException(403, "PATIENT_BANNED", "المريض محظور من الحجز",
                "تجاوز حد الغياب. يُفك الحظر بدفع العربون.")
            { Extensions = new Dictionary<string, object?> { ["unban_deposit_amount"] = deposit } };
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        await engine.LockSessionAsync(req.SessionId!.Value, ct);
        // تطبيق المريض يحجز في أي فرع، والاستقبال في فرعه الحالي
        var session = await db.DoctorSessions.FirstOrDefaultAsync(s => s.SessionId == req.SessionId && (app || s.BranchId == current.BranchId), ct)
                      ?? throw new AppException(404, "SESSION_NOT_FOUND", "الوجبة غير موجودة في هذا الفرع");
        await closures.EnsureOpenAsync(session.ClinicId, session.SessionDate, ct);
        if ((await doctors.ClosedWeekdaysAsync(session.BranchId, ct)).Contains((short)session.SessionDate.DayOfWeek)) throw ClosedDay();
        if (session.Status == SessionStatuses.Cancelled)
            throw new AppException(409, "SESSION_CANCELLED", "الوجبة ملغاة");
        if (QueueEngine.SessionEndUtc(session) <= clock.UtcNow || session.Status == SessionStatuses.Closed)
            throw new AppException(409, "SESSION_CLOSED", "انتهى وقت الوجبة");

        if (await db.Bookings.AnyAsync(b => b.SessionId == session.SessionId && b.PatientId == patient.PatientId && b.Status != BookingStatuses.Cancelled, ct)
            || await db.QueueTickets.AnyAsync(t => t.SessionId == session.SessionId && t.PatientId == patient.PatientId
                                                   && TicketStatuses.Active.Contains(t.Status), ct))
            throw new AppException(409, "DUPLICATE_BOOKING", "المريض محجوز مسبقاً في هذه الوجبة");
        // الحجز المكرر: حجز قائم واحد عند نفس الطبيب، وحد للحجوزات القائمة للمريض (يمنع حجز مقاعد وهمية)
        var openBookings = await db.Bookings.Where(b => b.PatientId == patient.PatientId && b.Status == BookingStatuses.Booked
                                                         && b.Session!.SessionDate >= clock.BaghdadToday())
            .Select(b => new { b.BookingId, b.Session!.DoctorId }).ToListAsync(ct);
        if (openBookings.FirstOrDefault(b => b.DoctorId == session.DoctorId) is { } sameDoctor)
            throw new AppException(409, "DUPLICATE_DOCTOR_BOOKING", "للمريض حجز قائم عند نفس الطبيب", "ألغِ الحجز القائم أو غيّر موعده")
                { Extensions = new Dictionary<string, object?> { ["booking_id"] = sameDoctor.BookingId } };
        var maxOpen = await settings.GetLongAsync(SettingsReader.MaxOpenBookings, session.BranchId, 4, ct);
        if (maxOpen > 0 && openBookings.Count >= maxOpen)
            throw new AppException(409, "BOOKING_LIMIT", $"للمريض {openBookings.Count} حجوزات قائمة (الحد {maxOpen})", "ألغِ حجزاً قبل إضافة آخر");

        var bookedCount = await db.Bookings.CountAsync(b => b.SessionId == session.SessionId && b.Status != BookingStatuses.Cancelled, ct);
        var occupied = await db.QueueTickets.CountAsync(t => t.SessionId == session.SessionId && t.Status != TicketStatuses.Cancelled, ct);
        if (bookedCount >= session.OnlineQuota || occupied >= session.CapacityTotal)
            throw new AppException(409, "SESSION_FULL", "اكتملت حصة الحجز في هذه الوجبة");

        var doctor = await db.Doctors.FirstAsync(d => d.DoctorId == session.DoctorId, ct);
        var visitKind = await engine.ResolveVisitKindAsync(req.VisitKind, patient.PatientId, doctor.DoctorId, session.SessionDate,
            staffMayOverride: !app, ct);
        var service = await engine.ConsultationServiceAsync(session.ClinicId, visitKind, ct);
        var sequence = await engine.NextSequenceAsync(session.SessionId, ct);
        var now = clock.UtcNow;

        var booking = new Booking
        {
            BookingId = bookingId,
            PatientId = patient.PatientId,
            SessionId = session.SessionId,
            SequenceNumber = sequence,
            VisitKind = visitKind,
            Price = QueueEngine.ConsultationPrice(doctor, service, visitKind),
            Source = app ? SourceOnline : SourcePhone,
            BookedByUserId = current.StaffUserId,
            BookedByAccountId = current.PatientAccountId,
            EstimatedTime = QueueEngine.ScheduledTime(session, sequence),
            CreatedAt = now,
        };
        db.Bookings.Add(booking);
        // تذكرة "بانتظار الوصول" تحجز مكان المريض في الطابور من الآن
        db.QueueTickets.Add(new QueueTicket
        {
            TicketId = UuidV7.New(),
            BranchId = session.BranchId,
            SessionId = session.SessionId,
            DoctorId = doctor.DoctorId,
            PatientId = patient.PatientId,
            BookingId = booking.BookingId,
            TicketNumber = await engine.TicketNumberAsync(doctor, sequence, ct),
            SequenceNumber = sequence,
            QueuePosition = sequence,
            Source = booking.Source,
            Status = TicketStatuses.Waiting,
            EstimatedTime = booking.EstimatedTime,
            CreatedBy = current.StaffUserId,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await engine.RecalculateAsync(session, ct);
        audit.Add("BOOKING_CREATED", "booking", booking.BookingId, after: new { booking.SessionId, booking.SequenceNumber, booking.VisitKind, booking.Price });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);

        await notifier.SessionChangedAsync(session.SessionId, ct);
        // تأكيد الحجز (تطبيق، أو SMS لمن ليس له تطبيق)
        var at = (booking.EstimatedTime ?? QueueEngine.ScheduledTime(session, booking.SequenceNumber)).ToOffset(ClockExtensions.BaghdadOffset);
        await notifications.NotifyPatientAsync(patient.PatientId, session.BranchId, Notifications.NotificationTypes.BookingConfirmed, "تم تأكيد حجزك",
            $"موعدك مع {doctor.FullName} يوم {session.SessionDate:yyyy-MM-dd} الساعة {at:HH:mm}، رقمك {booking.SequenceNumber}.",
            "booking", booking.BookingId, ct);
        // تداخل مع حجز عند طبيب آخر في نفس اليوم (فرق أقل من 45 دقيقة): تنبيه فقط
        var warnings = new List<BookingWarning>();
        if (booking.EstimatedTime is { } mine)
        {
            var sameDay = await db.Bookings.AsNoTracking()
                .Where(b => b.PatientId == patient.PatientId && b.BookingId != booking.BookingId && b.Status == BookingStatuses.Booked
                            && b.Session!.SessionDate == session.SessionDate && b.Session.DoctorId != session.DoctorId && b.EstimatedTime != null)
                .Join(db.Doctors, b => b.Session!.DoctorId, d => d.DoctorId, (b, d) => new { b.BookingId, b.EstimatedTime, Doctor = d.FullName }).ToListAsync(ct);
            foreach (var o in sameDay.Where(o => Math.Abs((o.EstimatedTime!.Value - mine).TotalMinutes) < 45))
                warnings.Add(new BookingWarning("BOOKING_TIME_OVERLAP",
                    $"للمريض حجز آخر مع {o.Doctor} الساعة {o.EstimatedTime!.Value.ToOffset(ClockExtensions.BaghdadOffset):HH:mm}", o.BookingId));
        }
        var dto = await GetAsync(booking.BookingId, ct);
        return warnings.Count > 0 ? dto with { Warnings = warnings } : dto;
    }

    public async Task<BookingDto> GetAsync(Guid bookingId, CancellationToken ct)
    {
        var q = db.Bookings.AsNoTracking().Include(x => x.Patient).Include(x => x.Session).Where(x => x.BookingId == bookingId);
        q = current.IsPatient ? q.Where(x => portal.LinkedPatientIds().Contains(x.PatientId)) : q.Where(x => x.Session!.BranchId == current.BranchId);
        var b = await q.FirstOrDefaultAsync(ct) ?? throw new AppException(404, "BOOKING_NOT_FOUND", "الحجز غير موجود");
        return (await ToDtosAsync([b], ct))[0];
    }

    public async Task<IReadOnlyList<BookingDto>> ListAsync(Guid? patientId, Guid? sessionId, CancellationToken ct)
    {
        if (patientId is null && sessionId is null)
            throw AppException.Validation(new Dictionary<string, string[]> { ["patient_id"] = ["أرسل patient_id أو session_id"] });
        var q = db.Bookings.AsNoTracking().Include(x => x.Patient).Include(x => x.Session)
            .Where(b => b.Session!.BranchId == current.BranchId);
        if (patientId is { } pid) q = q.Where(b => b.PatientId == pid);
        if (sessionId is { } sid) q = q.Where(b => b.SessionId == sid);
        var list = await q.OrderByDescending(b => b.Session!.SessionDate).ThenBy(b => b.SequenceNumber).Take(200).ToListAsync(ct);
        return await ToDtosAsync(list, ct);
    }

    /// <summary>حجوزات مرضى حساب التطبيق: القادمة (الأقرب أولاً) أو السابقة (الأحدث أولاً).</summary>
    public async Task<IReadOnlyList<BookingDto>> MyAsync(Guid? patientId, string? scope, CancellationToken ct)
    {
        new Validator().Check(scope is null or "upcoming" or "past", "scope", "upcoming | past").ThrowIfInvalid();
        if (patientId is { } pid) await portal.EnsureLinkedAsync(pid, ct);
        var today = clock.BaghdadToday();
        var q = db.Bookings.AsNoTracking().Include(x => x.Patient).Include(x => x.Session)
            .Where(b => portal.LinkedPatientIds().Contains(b.PatientId));
        if (patientId is { } p) q = q.Where(b => b.PatientId == p);
        var upcoming = scope is null or "upcoming";
        q = upcoming
            ? q.Where(b => b.Session!.SessionDate >= today && (b.Status == BookingStatuses.Booked || b.Status == BookingStatuses.CheckedIn))
            : q.Where(b => !(b.Session!.SessionDate >= today && (b.Status == BookingStatuses.Booked || b.Status == BookingStatuses.CheckedIn)));
        var list = upcoming
            ? await q.OrderBy(b => b.Session!.SessionDate).ThenBy(b => b.Session!.StartTime).ThenBy(b => b.SequenceNumber).Take(50).ToListAsync(ct)
            : await q.OrderByDescending(b => b.Session!.SessionDate).ThenByDescending(b => b.SequenceNumber).Take(50).ToListAsync(ct);
        return await ToDtosAsync(list, ct);
    }

    // ------------------------------------------------------------------ سياسة الإلغاء وتغيير الموعد (المرحلة 3)

    public const string FeeServiceName = "رسوم إلغاء متأخر";

    public async Task<BookingPolicyDto> PolicyAsync(Guid branchId, CancellationToken ct)
    {
        var paidTo = await settings.GetStringAsync(SettingsReader.CancelPaidTo, branchId, ct);
        if (paidTo is null || !SettingsReader.CancelPaidToValues.All.Contains(paidTo)) paidTo = SettingsReader.CancelPaidToValues.Credit;
        return new BookingPolicyDto(
            await settings.GetLongAsync(SettingsReader.CancelMinHours, branchId, 2, ct),
            await settings.GetLongAsync(SettingsReader.LateCancelFee, branchId, 0, ct),
            await settings.GetLongAsync(SettingsReader.LateCancelCountsNoShow, branchId, 1, ct) == 1,
            await settings.GetLongAsync(SettingsReader.MaxReschedules, branchId, 2, ct),
            await settings.GetLongAsync(SettingsReader.RescheduleMinHours, branchId, 2, ct),
            paidTo);
    }

    /// <summary>حجز ضمن نطاق المستخدم: مرضى حساب التطبيق، أو فرع الموظف الحالي.</summary>
    private IQueryable<Booking> Scoped() =>
        current.IsPatient
            ? db.Bookings.Include(b => b.Session).Include(b => b.Patient).Where(b => portal.LinkedPatientIds().Contains(b.PatientId))
            : db.Bookings.Include(b => b.Session).Include(b => b.Patient).Where(b => b.Session!.BranchId == current.BranchId);

    private bool CanOverride => !current.IsPatient && current.Has(Permissions.BookingsPolicyOverride);

    private void EnsureOverride(bool requested, string? reason, string field)
    {
        if (!requested) return;
        if (!CanOverride)
            throw new AppException(403, "POLICY_OVERRIDE_FORBIDDEN", "لا تملك صلاحية تجاوز سياسة الحجز", "يلزم bookings.policy_override");
        new Validator().Check(!string.IsNullOrWhiteSpace(reason), field, "سبب الإعفاء أو التجاوز مطلوب").ThrowIfInvalid();
    }

    private static DateTimeOffset AppointmentOf(Booking b) => b.EstimatedTime ?? QueueEngine.ScheduledTime(b.Session!, b.SequenceNumber);

    private sealed record CancelEvaluation(DateTimeOffset Appointment, bool Late, long Fee, bool NoShow, long Paid, string PaidTo, long Deposit);

    private async Task<CancelEvaluation> EvaluateCancelAsync(Booking b, BookingPolicyDto p, bool waive, CancellationToken ct)
    {
        var appointment = AppointmentOf(b);
        var late = appointment - clock.UtcNow < TimeSpan.FromHours(p.FreeCancelHours);
        var paid = b.PaymentStatus == PaymentStatuses.Paid ? b.Price : 0;
        var deposit = b.PaymentStatus == PaymentStatuses.DepositPaid
            ? await db.OnlinePayments.Where(o => o.BookingId == b.BookingId && o.Purpose == OnlinePaymentPurposes.BookingDeposit
                                                 && o.Status == OnlinePaymentStatuses.Succeeded).SumAsync(o => (long?)o.Amount, ct) ?? 0
            : 0;
        return new CancelEvaluation(appointment, late, late && !waive ? p.LateCancelFee : 0, late && !waive && p.LateCancelCountsAsNoShow,
            paid, paid > 0 ? p.CancelPaidTo : "none", deposit);
    }

    private static string Local(DateTimeOffset t) => t.ToOffset(ClockExtensions.BaghdadOffset).ToString("HH:mm 'يوم' yyyy-MM-dd");

    /// <summary>معاينة الإلغاء قبل التأكيد: الرسوم، الاحتساب غياباً، أين يذهب المدفوع، والشروط بالعربي.</summary>
    public async Task<CancelPreviewDto> CancelPreviewAsync(Guid bookingId, CancellationToken ct)
    {
        var b = await Scoped().AsNoTracking().FirstOrDefaultAsync(x => x.BookingId == bookingId, ct)
                ?? throw new AppException(404, "BOOKING_NOT_FOUND", "الحجز غير موجود");
        var p = await PolicyAsync(b.Session!.BranchId, ct);
        var e = await EvaluateCancelAsync(b, p, waive: false, ct);
        var allowed = b.Status == BookingStatuses.Booked;
        var until = e.Appointment.AddHours(-p.FreeCancelHours);
        var conditions = new List<string>
        {
            p.FreeCancelHours > 0
                ? $"الإلغاء مجاني حتى {p.FreeCancelHours} ساعة قبل الموعد (قبل {Local(until)})."
                : "الإلغاء مجاني في أي وقت قبل تثبيت الحضور.",
        };
        if (p.LateCancelFee > 0) conditions.Add($"الإلغاء بعد ذلك عليه رسوم {p.LateCancelFee:N0} د.ع تُضاف لحسابك في الفرع وتُدفع في زيارتك القادمة.");
        if (p.LateCancelCountsAsNoShow) conditions.Add("الإلغاء المتأخر يُحتسب غياباً، وتكرار الغياب قد يوقف الحجز من التطبيق.");
        if (e.Paid > 0)
            conditions.Add(e.PaidTo == SettingsReader.CancelPaidToValues.Refund
                ? $"المبلغ المدفوع ({e.Paid:N0} د.ع) يُرجع إلى وسيلة الدفع الإلكتروني."
                : $"المبلغ المدفوع ({e.Paid:N0} د.ع) يتحول رصيداً لك يُستخدم في زيارتك القادمة.");
        if (e.Deposit > 0) conditions.Add($"العربون ({e.Deposit:N0} د.ع) يبقى رصيداً لك.");
        conditions.Add("لا يمكن الإلغاء بعد تثبيت الحضور.");
        var summary = !allowed ? "لا يمكن إلغاء هذا الحجز."
            : !e.Late ? "الإلغاء الآن مجاني."
            : e.Fee > 0 ? $"الإلغاء الآن متأخر: رسوم {e.Fee:N0} د.ع" + (e.NoShow ? " ويُحتسب غياباً." : ".")
            : e.NoShow ? "الإلغاء الآن متأخر ويُحتسب غياباً."
            : "الإلغاء الآن متأخر بلا رسوم.";
        return new CancelPreviewDto(b.BookingId, allowed, allowed ? null : "BOOKING_NOT_CANCELLABLE",
            allowed ? null : "لا يمكن إلغاء الحجز بعد تثبيت الحضور أو إنهائه أو إلغائه", e.Appointment, until,
            Math.Round((e.Appointment - clock.UtcNow).TotalHours, 1), p.FreeCancelHours, e.Late, e.Fee, e.NoShow, e.Paid, e.PaidTo,
            e.PaidTo == SettingsReader.CancelPaidToValues.Credit ? e.Paid : 0, e.PaidTo == SettingsReader.CancelPaidToValues.Refund ? e.Paid : 0,
            e.Deposit, CanOverride && e.Late, conditions, summary, p);
    }

    public async Task<CancelBookingResponse> CancelAsync(Guid bookingId, CancelBookingRequest req, CancellationToken ct)
    {
        new Validator().Check(!string.IsNullOrWhiteSpace(req.Reason), "reason", "سبب الإلغاء مطلوب").ThrowIfInvalid();
        var waive = req.WaiveFee == true;
        EnsureOverride(waive, req.WaiveReason, "waive_reason");

        await using var tx = await db.BeginTransactionAsync(ct);
        // نفس ترتيب الأقفال في تثبيت الحضور وتغيير الموعد: الوجبة أولاً ثم الحجز
        var sid = await Scoped().AsNoTracking().Where(b => b.BookingId == bookingId).Select(b => (Guid?)b.SessionId).FirstOrDefaultAsync(ct)
                  ?? throw new AppException(404, "BOOKING_NOT_FOUND", "الحجز غير موجود");
        await engine.LockSessionAsync(sid, ct);
        var booking = await Scoped().FirstOrDefaultAsync(b => b.BookingId == bookingId, ct)
                      ?? throw new AppException(404, "BOOKING_NOT_FOUND", "الحجز غير موجود");
        if (booking.SessionId != sid) throw new AppException(409, "CONCURRENT_UPDATE", "تغير موعد الحجز للتو", "أعد المحاولة");
        if (booking.Status != BookingStatuses.Booked)
            throw new AppException(409, "BOOKING_NOT_CANCELLABLE", "لا يمكن إلغاء الحجز بعد تثبيت الحضور أو إنهائه");

        var now = clock.UtcNow;
        var branchId = booking.Session!.BranchId;
        var policy = await PolicyAsync(branchId, ct);
        var e = await EvaluateCancelAsync(booking, policy, waive, ct);

        booking.Status = BookingStatuses.Cancelled;
        booking.CancelReason = req.Reason!.Trim();
        booking.CancelledAt = now;
        booking.CountedAsNoShow = e.NoShow;
        booking.CancelledBy = current.IsPatient ? "patient" : "staff";
        booking.CancelFeeWaived = waive && e.Late && (policy.LateCancelFee > 0 || policy.LateCancelCountsAsNoShow);

        long credit = 0, refund = 0;
        if (e.Paid > 0 && e.PaidTo == SettingsReader.CancelPaidToValues.Refund)
        {
            // إرجاع لوسيلة الدفع الإلكتروني عبر البوابة (5.1-ج)
            refund = e.Paid;
            booking.PaymentStatus = PaymentStatuses.Refunded;
            foreach (var op in await db.OnlinePayments.Where(o => o.BookingId == booking.BookingId && o.Purpose == OnlinePaymentPurposes.BookingFull
                                                                  && o.Status == OnlinePaymentStatuses.Succeeded).ToListAsync(ct))
            {
                await gateways.RefundAsync(op, op.Amount, ct);
                op.Status = OnlinePaymentStatuses.Refunded;
            }
        }
        else if (e.Paid > 0)
        {
            credit = e.Paid;
            booking.PaymentStatus = PaymentStatuses.Credited;
            db.PatientCredits.Add(new PatientCreditEntry
            {
                EntryId = UuidV7.New(), PatientId = booking.PatientId, BranchId = branchId, Amount = credit, Kind = "booking_cancel_credit",
                BookingId = booking.BookingId, CreatedBy = current.StaffUserId, CreatedAt = now,
            });
        }

        InvoiceItem? fee = null;
        if (e.Fee > 0)
        {
            fee = await AddCancelFeeAsync(booking, e.Fee, ct);
            booking.CancelFeeItemId = fee.InvoiceItemId;
        }

        var ticket = await db.QueueTickets.FirstOrDefaultAsync(t => t.BookingId == booking.BookingId, ct);
        if (ticket is not null) { ticket.Status = TicketStatuses.Cancelled; ticket.QueuePosition = null; }

        audit.Add("BOOKING_CANCELLED", "booking", booking.BookingId, after: new
        {
            late = e.Late, credit, refund, fee = e.Fee, no_show = e.NoShow, waived = booking.CancelFeeWaived, by = booking.CancelledBy,
        }, reason: booking.CancelReason);
        if (booking.CancelFeeWaived)
            audit.Add("BOOKING_POLICY_WAIVED", "booking", booking.BookingId,
                after: new { waived_fee = policy.LateCancelFee, waived_no_show = policy.LateCancelCountsAsNoShow }, reason: req.WaiveReason!.Trim());
        var banned = e.NoShow && await bans.RecordNoShowAsync(booking.Patient!, ticket?.TicketId, booking.BookingId, ct);
        await db.SaveChangesAsync(ct);
        await engine.RecalculateAsync(booking.Session!, ct);
        if (tx is not null) await tx.CommitAsync(ct);

        await notifier.SessionChangedAsync(booking.SessionId, ct);
        await notifier.BookingChangedAsync(booking.BookingId, BookingChangeActions.Cancelled, null, ct);
        if (banned) await notifier.PatientBannedAsync(booking.PatientId, branchId, ct);
        if (credit > 0) await notifier.CreditAddedAsync(booking.PatientId, branchId, credit, "من حجز ملغى", booking.BookingId, ct);
        if (refund > 0)
            await notifications.NotifyPatientAsync(booking.PatientId, branchId, Notifications.NotificationTypes.PaymentRefunded, "تم إرجاع مبلغ لك",
                $"{refund:N0} د.ع من حجز ملغى إلى وسيلة الدفع الإلكتروني", "booking", booking.BookingId, ct);
        if (fee is not null)
            await notifications.NotifyPatientAsync(booking.PatientId, branchId, Notifications.NotificationTypes.BookingCancelled, "أُلغي حجزك",
                $"إلغاء متأخر: أُضيفت رسوم {fee.NetPrice:N0} د.ع لحسابك تُدفع في زيارتك القادمة.", "booking", booking.BookingId, ct);
        return new CancelBookingResponse(booking.Status, e.NoShow, credit)
        {
            Late = e.Late, FeeAmount = fee?.NetPrice ?? 0, FeeInvoiceId = fee?.InvoiceId, FeeInvoiceItemId = fee?.InvoiceItemId,
            FeeWaived = booking.CancelFeeWaived, RefundAmount = refund, PaidAmountTo = e.PaidTo,
        };
    }

    /// <summary>
    /// بند «رسوم إلغاء متأخر» غير مدفوع في فاتورة جديدة للمريض في فرع الحجز، يظهر في سلته (billable-items).
    /// خدمة الرسوم خدمة نظام واحدة لكل فرع (type = fee، موقوفة فلا تظهر في الكتالوج)، والسعر لقطة على البند.
    /// </summary>
    private async Task<InvoiceItem> AddCancelFeeAsync(Booking booking, long fee, CancellationToken ct)
    {
        var branchId = booking.Session!.BranchId;
        var now = clock.UtcNow;
        await db.ExecuteAsync($"""
            INSERT INTO services (service_id, branch_id, name, type, price, is_commissionable, is_active, created_at, updated_at)
            VALUES ({UuidV7.New()}, {branchId}, {FeeServiceName}, {ServiceTypes.Fee}, 0, FALSE, FALSE, {now}, {now})
            ON CONFLICT (branch_id) WHERE type = 'fee' DO NOTHING
            """, ct);
        var service = await db.Services.AsNoTracking().FirstAsync(x => x.BranchId == branchId && x.Type == ServiceTypes.Fee, ct);
        var seq = await numbers.NextAsync(branchId, "invoice", "all", ct);
        var code = await numbers.BranchCodeAsync(branchId, ct);
        var invoice = new Invoice
        {
            InvoiceId = UuidV7.New(), InvoiceNumber = $"{code}-INV-{seq:D6}", BranchId = branchId, PatientId = booking.PatientId,
            Status = InvoiceStatuses.Unpaid, GrossAmount = fee, NetAmount = fee, PaidAmount = 0, CreatedBy = current.StaffUserId, CreatedAt = now,
        };
        var item = new InvoiceItem
        {
            InvoiceItemId = UuidV7.New(), InvoiceId = invoice.InvoiceId, ServiceId = service.ServiceId, ItemType = InvoiceItemTypes.CancellationFee,
            ServiceName = FeeServiceName, Quantity = 1, UnitPrice = fee, NetPrice = fee, CommissionRate = 0, DoctorShare = 0, ComplexShare = fee,
            Status = InvoiceItemStatuses.Unpaid, CreatedAt = now,
        };
        invoice.Items.Add(item);
        db.Invoices.Add(invoice);
        audit.Add("CANCEL_FEE_ADDED", "invoice", invoice.InvoiceId, after: new { booking_id = booking.BookingId, fee, invoice.InvoiceNumber });
        // البند يُحفظ قبل ربطه بالحجز (bookings.cancel_fee_item_id مفتاح أجنبي لا يعرفه EF)
        await db.SaveChangesAsync(ct);
        return item;
    }

    // ------------------------------------------------------------------ تغيير الموعد

    /// <summary>
    /// سبب منع تغيير الموعد (أو null). نفس الفحص في المعاينة والتنفيذ (التنفيذ يعيده بعد قفل الوجبتين).
    /// target = null: فحص السياسة فقط (المعاينة قبل اختيار الموعد الجديد).
    /// </summary>
    private async Task<AppException?> RescheduleProblemAsync(Booking b, DoctorSession? target, BookingPolicyDto p, bool overridePolicy, CancellationToken ct)
    {
        if (b.Status != BookingStatuses.Booked)
            return new AppException(409, "BOOKING_NOT_RESCHEDULABLE", "لا يمكن تغيير موعد حجز بعد تثبيت الحضور أو إلغائه");
        if (current.IsPatient && b.Patient!.IsBanned)
            return new AppException(403, "PATIENT_BANNED", "المريض محظور من الحجز", "تجاوز حد الغياب. يُفك الحظر بدفع العربون.");
        if (!overridePolicy)
        {
            if (p.MaxReschedules == 0)
                return new AppException(409, "RESCHEDULE_DISABLED", "تغيير الموعد غير متاح في هذا الفرع", "يمكن إلغاء الحجز وحجز موعد جديد");
            if (b.RescheduleCount >= p.MaxReschedules)
                return new AppException(409, "RESCHEDULE_LIMIT_REACHED", "بلغ الحجز الحد الأقصى لتغيير الموعد",
                    $"غُيّر موعد هذا الحجز {b.RescheduleCount} مرة (الحد {p.MaxReschedules})");
            if (AppointmentOf(b) - clock.UtcNow < TimeSpan.FromHours(p.RescheduleMinHours))
                return new AppException(409, "RESCHEDULE_TOO_LATE", "فات وقت تغيير الموعد",
                    $"يُغيّر الموعد قبل {p.RescheduleMinHours} ساعة على الأقل من موعده. يمكن إلغاء الحجز (قد تُطبق شروط الإلغاء المتأخر).");
        }
        if (target is null) return null;
        if (target.SessionId == b.SessionId) return new AppException(422, "SAME_SESSION", "الموعد الجديد هو نفس الموعد الحالي");
        if (target.DoctorId != b.Session!.DoctorId)
            return new AppException(422, "RESCHEDULE_DIFFERENT_DOCTOR", "تغيير الموعد لنفس الطبيب فقط", "لطبيب آخر: ألغِ الحجز واحجز من جديد");
        if ((await doctors.ClosedWeekdaysAsync(target.BranchId, ct)).Contains((short)target.SessionDate.DayOfWeek)) return ClosedDay();
        if (target.Status == SessionStatuses.Cancelled) return new AppException(409, "SESSION_CANCELLED", "الوجبة ملغاة");
        if (QueueEngine.SessionEndUtc(target) <= clock.UtcNow || target.Status == SessionStatuses.Closed)
            return new AppException(409, "SESSION_CLOSED", "انتهى وقت الوجبة");
        try { await closures.EnsureOpenAsync(target.ClinicId, target.SessionDate, ct); }
        catch (AppException e) { return e; }
        if (await db.Bookings.AnyAsync(x => x.SessionId == target.SessionId && x.PatientId == b.PatientId && x.BookingId != b.BookingId
                                            && x.Status != BookingStatuses.Cancelled, ct)
            || await db.QueueTickets.AnyAsync(t => t.SessionId == target.SessionId && t.PatientId == b.PatientId && t.BookingId != b.BookingId
                                                   && TicketStatuses.Active.Contains(t.Status), ct))
            return new AppException(409, "DUPLICATE_BOOKING", "المريض محجوز مسبقاً في هذه الوجبة");
        var bookedCount = await db.Bookings.CountAsync(x => x.SessionId == target.SessionId && x.Status != BookingStatuses.Cancelled, ct);
        var occupied = await db.QueueTickets.CountAsync(t => t.SessionId == target.SessionId && t.Status != TicketStatuses.Cancelled, ct);
        if (bookedCount >= target.OnlineQuota || occupied >= target.CapacityTotal)
            return new AppException(409, "SESSION_FULL", "اكتملت حصة الحجز في هذه الوجبة");
        return null;
    }

    /// <summary>معاينة تغيير الموعد: المرات المتبقية، المهلة، أثر الدفع، والشروط بالعربي. sessionId اختياري (الموعد الجديد المقترح).</summary>
    public async Task<ReschedulePreviewDto> ReschedulePreviewAsync(Guid bookingId, Guid? sessionId, CancellationToken ct)
    {
        var b = await Scoped().AsNoTracking().FirstOrDefaultAsync(x => x.BookingId == bookingId, ct)
                ?? throw new AppException(404, "BOOKING_NOT_FOUND", "الحجز غير موجود");
        var p = await PolicyAsync(b.Session!.BranchId, ct);
        DoctorSession? target = null;
        if (sessionId is { } sid)
            target = await db.DoctorSessions.AsNoTracking().FirstOrDefaultAsync(x => x.SessionId == sid && x.BranchId == b.Session.BranchId, ct)
                     ?? throw new AppException(404, "SESSION_NOT_FOUND", "الوجبة غير موجودة");
        var problem = await RescheduleProblemAsync(b, target, p, overridePolicy: false, ct);
        var appointment = AppointmentOf(b);
        var deadline = appointment.AddHours(-p.RescheduleMinHours);
        var remaining = Math.Max(0, p.MaxReschedules - b.RescheduleCount);
        var paid = b.PaymentStatus == PaymentStatuses.Paid ? b.Price : 0;

        RescheduleTargetDto? targetDto = null;
        if (target is not null)
        {
            var next = await engine.NextSequenceAsync(target.SessionId, ct);
            var booked = await db.Bookings.CountAsync(x => x.SessionId == target.SessionId && x.Status != BookingStatuses.Cancelled, ct);
            var eta = QueueEngine.ScheduledTime(target, next);
            targetDto = new RescheduleTargetDto(target.SessionId, target.DoctorId, target.SessionDate, target.StartTime.ToString("HH:mm"),
                target.EffectiveEndTime.ToString("HH:mm"), eta > clock.UtcNow ? eta : clock.UtcNow, Math.Max(0, target.OnlineQuota - booked),
                target.Status);
        }

        var conditions = new List<string>
        {
            p.MaxReschedules == 0
                ? "تغيير الموعد غير متاح في هذا الفرع، ويمكن إلغاء الحجز وحجز موعد جديد."
                : $"يمكن تغيير موعد الحجز {p.MaxReschedules} {(p.MaxReschedules == 1 ? "مرة" : "مرات")} كحد أقصى (بقي {remaining}).",
        };
        if (p.RescheduleMinHours > 0) conditions.Add($"التغيير متاح حتى {p.RescheduleMinHours} ساعة قبل الموعد (قبل {Local(deadline)}).");
        conditions.Add("التغيير لموعد آخر عند نفس الطبيب فقط، ويأخذ الحجز رقماً جديداً في الوجبة الجديدة.");
        if (paid > 0) conditions.Add($"المبلغ المدفوع ({paid:N0} د.ع) يبقى على الحجز بعد تغيير موعده.");
        if (b.PaymentStatus == PaymentStatuses.DepositPaid) conditions.Add("العربون المدفوع يبقى رصيداً لك ويُستخدم عند وصولك.");
        conditions.Add("لا توجد رسوم لتغيير الموعد.");

        var policyBlocked = problem?.Code is "RESCHEDULE_DISABLED" or "RESCHEDULE_LIMIT_REACHED" or "RESCHEDULE_TOO_LATE";
        return new ReschedulePreviewDto(b.BookingId, problem is null, problem?.Code, problem is null ? null : Describe(problem), b.RescheduleCount,
            p.MaxReschedules, remaining, problem is null && target is not null ? Math.Max(0, remaining - 1) : remaining, p.RescheduleMinHours,
            appointment, deadline, 0, paid, paid > 0 ? "kept" : "none", CanOverride && (problem is null || policyBlocked), targetDto, conditions, p);
    }

    private static AppException ClosedDay() =>
        new(409, "BRANCH_CLOSED_DAY", "الفرع لا يعمل في هذا اليوم", "اختر يوماً آخر من أيام دوام الفرع");

    private static string Describe(AppException e) => string.IsNullOrWhiteSpace(e.Detail) ? e.Title : $"{e.Title}. {e.Detail}";

    /// <summary>
    /// تغيير موعد الحجز لوجبة أخرى عند نفس الطبيب: نفس الحجز (booking_id) بدفعه وسعره ونوع زيارته، برقم جديد في الوجبة الجديدة.
    /// يقفل الوجبتين بترتيب ثابت (لا جمود مع تثبيت الحضور أو تغيير موعد آخر متزامن)، ويعيد فحص السعة والتكرار تحت القفل.
    /// </summary>
    public async Task<BookingDto> RescheduleAsync(Guid bookingId, RescheduleBookingRequest req, CancellationToken ct)
    {
        new Validator().Check(req.SessionId is not null, "session_id", "الموعد الجديد مطلوب").ThrowIfInvalid();
        var overridePolicy = req.OverridePolicy == true;
        EnsureOverride(overridePolicy, req.OverrideReason, "override_reason");
        var oldId = await Scoped().AsNoTracking().Where(b => b.BookingId == bookingId).Select(b => (Guid?)b.SessionId).FirstOrDefaultAsync(ct)
                    ?? throw new AppException(404, "BOOKING_NOT_FOUND", "الحجز غير موجود");

        await using var tx = await db.BeginTransactionAsync(ct);
        foreach (var id in new[] { oldId, req.SessionId!.Value }.Distinct().Order())
            await engine.LockSessionAsync(id, ct);
        var booking = await Scoped().FirstOrDefaultAsync(b => b.BookingId == bookingId, ct)
                      ?? throw new AppException(404, "BOOKING_NOT_FOUND", "الحجز غير موجود");
        if (booking.SessionId != oldId) throw new AppException(409, "CONCURRENT_UPDATE", "تغير موعد الحجز للتو", "أعد المحاولة");
        var oldSession = booking.Session!;
        var target = await db.DoctorSessions.FirstOrDefaultAsync(x => x.SessionId == req.SessionId && x.BranchId == oldSession.BranchId, ct)
                     ?? throw new AppException(404, "SESSION_NOT_FOUND", "الوجبة غير موجودة");
        var policy = await PolicyAsync(oldSession.BranchId, ct);
        if (await RescheduleProblemAsync(booking, target, policy, overridePolicy, ct) is { } problem) throw problem;

        var now = clock.UtcNow;
        var doctor = await db.Doctors.FirstAsync(d => d.DoctorId == target.DoctorId, ct);
        var ticket = await db.QueueTickets.FirstAsync(t => t.BookingId == booking.BookingId, ct);
        var sequence = await engine.NextSequenceAsync(target.SessionId, ct);
        var before = new { session_id = oldSession.SessionId, date = oldSession.SessionDate, sequence = booking.SequenceNumber, ticket.TicketNumber };
        var overridden = overridePolicy && await RescheduleProblemAsync(booking, null, policy, false, ct) is not null;

        booking.OriginalSessionId ??= oldSession.SessionId;
        booking.Session = target;
        booking.SessionId = target.SessionId;
        booking.SequenceNumber = sequence;
        booking.EstimatedTime = QueueEngine.ScheduledTime(target, sequence);
        booking.RescheduleCount++;
        booking.LastRescheduledAt = now;
        booking.ReminderSentAt = null;
        booking.DayBeforeSentAt = null;
        booking.SoonSentAt = null;

        ticket.SessionId = target.SessionId;
        ticket.SequenceNumber = sequence;
        ticket.QueuePosition = sequence;
        ticket.TicketNumber = await engine.TicketNumberAsync(doctor, sequence, ct);
        ticket.EstimatedTime = booking.EstimatedTime;
        ticket.Status = TicketStatuses.Waiting;
        if (ticket.Priority == TicketPriorities.LateReinserted) ticket.Priority = TicketPriorities.Normal;
        ticket.TurnSoonNotifiedAt = null;

        audit.Add("BOOKING_RESCHEDULED", "booking", booking.BookingId, before: before, after: new
        {
            session_id = target.SessionId, date = target.SessionDate, sequence, ticket.TicketNumber, reschedule_count = booking.RescheduleCount,
            by = current.IsPatient ? "patient" : "staff",
        }, reason: req.Reason?.Trim());
        if (overridden)
            audit.Add("BOOKING_POLICY_OVERRIDDEN", "booking", booking.BookingId,
                after: new { max_reschedules = policy.MaxReschedules, reschedule_min_hours = policy.RescheduleMinHours }, reason: req.OverrideReason!.Trim());
        await db.SaveChangesAsync(ct);
        await engine.RecalculateAsync(oldSession, ct);
        await engine.RecalculateAsync(target, ct);
        if (tx is not null) await tx.CommitAsync(ct);

        await notifier.SessionChangedAsync(oldSession.SessionId, ct);
        await notifier.SessionChangedAsync(target.SessionId, ct);
        await notifier.BookingChangedAsync(booking.BookingId, BookingChangeActions.Rescheduled, oldSession.SessionId, ct);
        var at = (booking.EstimatedTime ?? QueueEngine.ScheduledTime(target, sequence)).ToOffset(ClockExtensions.BaghdadOffset);
        await notifications.NotifyPatientAsync(booking.PatientId, target.BranchId, Notifications.NotificationTypes.BookingRescheduled, "تم تغيير موعدك",
            $"موعدك الجديد مع {doctor.FullName} يوم {target.SessionDate:yyyy-MM-dd} الساعة {at:HH:mm}، رقمك {sequence}.", "booking", booking.BookingId, ct);
        return await GetAsync(booking.BookingId, ct);
    }

    private async Task<List<BookingDto>> ToDtosAsync(List<Booking> list, CancellationToken ct)
    {
        var ids = list.Select(b => b.BookingId).ToList();
        var tickets = await db.QueueTickets.AsNoTracking().Where(t => t.BookingId != null && ids.Contains(t.BookingId.Value))
            .ToDictionaryAsync(t => t.BookingId!.Value, ct);
        var doctorIds = list.Select(b => b.Session!.DoctorId).Distinct().ToList();
        var names = await db.Doctors.Where(d => doctorIds.Contains(d.DoctorId)).ToDictionaryAsync(d => d.DoctorId, d => d.FullName, ct);
        // طبيب بلا عربون (0): لا يُعرض خيار العربون الإلكتروني
        var noDeposit = await db.Doctors.Where(d => doctorIds.Contains(d.DoctorId) && d.OnlineDepositAmount == 0).Select(d => d.DoctorId).ToListAsync(ct);
        var appOptionsNoDeposit = AppPaymentOptions.Where(o => o != "pay_deposit_online").ToArray();
        var branchIds = list.Select(b => b.Session!.BranchId).Distinct().ToList();
        var branchNames = await db.Branches.AsNoTracking().Where(x => branchIds.Contains(x.BranchId)).ToDictionaryAsync(x => x.BranchId, x => x.Name, ct);
        return list.Select(b =>
        {
            tickets.TryGetValue(b.BookingId, out var t);
            return new BookingDto(b.BookingId, b.PatientId, b.Patient!.FullName, b.SessionId, b.Session!.DoctorId,
                names.GetValueOrDefault(b.Session.DoctorId, ""), b.Session.SessionDate, b.SequenceNumber, t?.TicketNumber,
                t?.EstimatedTime ?? b.EstimatedTime, t?.QueuePosition, b.VisitKind, b.Price, b.Source, b.Status, b.PaymentStatus,
                b.Status == BookingStatuses.Booked && b.PaymentStatus == PaymentStatuses.Unpaid
                    ? b.Source == SourceOnline && paymentOptions.Value.OnlineAvailable
                        ? noDeposit.Contains(b.Session.DoctorId) ? appOptionsNoDeposit : AppPaymentOptions
                        : PhonePaymentOptions
                    : b.Status == BookingStatuses.Booked && b.PaymentStatus == PaymentStatuses.DepositPaid ? PhonePaymentOptions : [],
                b.CancelReason, b.CreatedAt)
            {
                RescheduleCount = b.RescheduleCount, CheckInMethod = b.CheckInMethod, CheckedInAt = b.CheckedInAt, CancelledBy = b.CancelledBy,
                CancelResolution = b.CancelResolution, RebookedBookingId = b.RebookedBookingId,
                CancelOptionsPending = b.Status == BookingStatuses.Cancelled && b.CancelledBy != "patient" && b.Session.Status == SessionStatuses.Cancelled
                                       && b.PaymentStatus == PaymentStatuses.Credited && b.CancelResolution == null,
                BranchId = b.Session.BranchId, BranchName = branchNames.GetValueOrDefault(b.Session.BranchId),
            };
        }).ToList();
    }
}
