using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Branches;

public record ClosureDto(Guid ClosureId, Guid ClinicId, string Reason, string ReasonAr, string? Note, DateOnly StartsOn, DateOnly? EndsOn,
    string Status, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt);
public record CreateClosureRequest(string? Reason, string? Note, DateOnly? StartsOn, DateOnly? EndsOn, bool? CancelBookings);
public record CreateClosureResponse(ClosureDto Closure, int CancelledSessions, int CancelledBookings, int CreditedBookings);
public record BranchClosureDto(Guid ClosureId, Guid BranchId, string Reason, string ReasonAr, string? Note, DateOnly StartsOn, DateOnly? EndsOn,
    string Status, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt);
public record CreateBranchClosureResponse(BranchClosureDto Closure, int CancelledSessions, int CancelledBookings, int CreditedBookings);

/// <summary>
/// الإغلاق المؤقت للعيادة أو المختبر أو الأشعة أو الصيدلية لسبب (سفر، صيانة، عطلة...) لفترة محددة أو حتى إعادة الفتح.
/// يختلف عن الإيقاف الدائم (is_active = false): العيادة المغلقة مؤقتاً تبقى ظاهرة مع سبب الإغلاق، وتعود تلقائياً بعد انتهاء الفترة.
/// </summary>
public class ClosureService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, Lookups.LookupService lookups,
    Notifications.NotificationService notifications, Queue.QueueNotifier notifier)
{
    public const int MaxClosureDays = 366;

    /// <summary>الإغلاق الساري على العيادة في تاريخ معين، أو null إذا كانت مفتوحة.</summary>
    public async Task<ClinicClosure?> ActiveOnAsync(Guid clinicId, DateOnly date, CancellationToken ct) =>
        await db.ClinicClosures.AsNoTracking()
            .Where(c => c.ClinicId == clinicId && c.EndedAt == null && c.StartsOn <= date && (c.EndsOn == null || c.EndsOn >= date))
            .OrderBy(c => c.StartsOn).FirstOrDefaultAsync(ct);

    /// <summary>يرفض العملية إذا كانت العيادة مغلقة في هذا التاريخ (للحجز والاستقبال، ولاحقاً للمختبر والأشعة والصيدلية).</summary>
    public async Task EnsureOpenAsync(Guid clinicId, DateOnly date, CancellationToken ct)
    {
        var branchId = await db.Clinics.Where(c => c.ClinicId == clinicId).Select(c => (Guid?)c.BranchId).FirstOrDefaultAsync(ct);
        if (branchId is { } bid && await BranchActiveOnAsync(bid, date, ct) is { } bc)
        {
            var bnames = await ReasonNamesAsync(ct);
            throw new AppException(409, "BRANCH_CLOSED", "الفرع مغلق مؤقتاً", Label(bc.Reason, bc.Note, bnames))
            {
                Extensions = new Dictionary<string, object?>
                {
                    ["closure_reason"] = bc.Reason,
                    ["closure_reason_ar"] = ReasonName(bc.Reason, bnames),
                    ["closed_until"] = bc.EndsOn?.ToString("yyyy-MM-dd"),
                },
            };
        }
        var closure = await ActiveOnAsync(clinicId, date, ct);
        if (closure is null) return;
        var names = await ReasonNamesAsync(ct);
        throw new AppException(409, "CLINIC_CLOSED", "العيادة مغلقة مؤقتاً", Label(closure, names))
        {
            Extensions = new Dictionary<string, object?>
            {
                ["closure_reason"] = closure.Reason,
                ["closure_reason_ar"] = ReasonName(closure.Reason, names),
                ["closed_until"] = closure.EndsOn?.ToString("yyyy-MM-dd"),
            },
        };
    }

    /// <summary>الإغلاقات السارية أو القادمة لعدة عيادات (لعرضها في القوائم).</summary>
    public async Task<Dictionary<Guid, ClinicClosure>> CurrentOrNextAsync(IEnumerable<Guid> clinicIds, CancellationToken ct)
    {
        var ids = clinicIds.ToList();
        var today = clock.BaghdadToday();
        var list = await db.ClinicClosures.AsNoTracking()
            .Where(c => ids.Contains(c.ClinicId) && c.EndedAt == null && (c.EndsOn == null || c.EndsOn >= today))
            .OrderBy(c => c.StartsOn).ToListAsync(ct);
        return list.GroupBy(c => c.ClinicId).ToDictionary(g => g.Key, g => g.First());
    }

    public async Task<IReadOnlyList<ClosureDto>> ListAsync(Guid clinicId, CancellationToken ct)
    {
        await LoadClinicAsync(clinicId, manage: false, ct);
        var list = await db.ClinicClosures.AsNoTracking().Where(c => c.ClinicId == clinicId)
            .OrderByDescending(c => c.StartsOn).Take(100).ToListAsync(ct);
        var names = await ReasonNamesAsync(ct);
        return list.Select(c => ToDto(c, names)).ToList();
    }

    public async Task<CreateClosureResponse> CreateAsync(Guid clinicId, CreateClosureRequest req, CancellationToken ct)
    {
        var clinic = await LoadClinicAsync(clinicId, manage: true, ct);
        var today = clock.BaghdadToday();
        var starts = req.StartsOn ?? today;
        var reasonItem = await lookups.ResolveActiveAsync(LookupCategories.ClosureReason, req.Reason, ct);
        new Validator()
            .Check(reasonItem is not null, "reason", "اختر سبباً من قائمة أسباب الإغلاق (GET /lookups/closure_reason)")
            .Check(reasonItem?.Code != LookupCategories.OtherReason || !string.IsNullOrWhiteSpace(req.Note), "note", "اكتب سبب الإغلاق")
            .Check(req.Note is null || req.Note.Length <= 500, "note", "500 حرف كحد أقصى")
            .Check(starts >= today, "starts_on", "تاريخ البداية اليوم أو بعده")
            .Check(req.EndsOn is null || req.EndsOn >= starts, "ends_on", "تاريخ النهاية بعد البداية")
            .Check(req.EndsOn is null || req.EndsOn.Value.DayNumber - starts.DayNumber < MaxClosureDays, "ends_on", "سنة كحد أقصى")
            .ThrowIfInvalid();
        var ends = req.EndsOn;
        var farEnd = ends ?? DateOnly.MaxValue;

        var overlaps = await db.ClinicClosures.AnyAsync(c => c.ClinicId == clinicId && c.EndedAt == null
            && c.StartsOn <= farEnd && (c.EndsOn == null || c.EndsOn >= starts), ct);
        if (overlaps)
            throw new AppException(409, "CLOSURE_OVERLAP", "يوجد إغلاق آخر في نفس الفترة", "عدّل الإغلاق الحالي أو أنهِه أولاً");

        // الوجبات المتأثرة (للعيادات فقط؛ المختبر والأشعة والصيدلية بلا وجبات)
        var sessions = await db.DoctorSessions
            .Where(s => s.ClinicId == clinicId && s.SessionDate >= starts && s.SessionDate <= farEnd && s.Status != SessionStatuses.Cancelled)
            .ToListAsync(ct);
        await EnsureCanCancelAsync(sessions, req.CancelBookings, "CLINIC", "العيادة", ct);

        var closure = new ClinicClosure
        {
            ClosureId = UuidV7.New(),
            ClinicId = clinicId,
            Reason = reasonItem!.Code,
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            StartsOn = starts,
            EndsOn = ends,
            CreatedBy = current.UserId,
            CreatedAt = clock.UtcNow,
        };
        db.ClinicClosures.Add(closure);

        var names = new Dictionary<string, string> { [reasonItem.Code] = reasonItem.NameAr };
        var (cancelled, credited) = await CancelSessionsAsync(sessions, ClosureMarker + Label(closure, names), ct);

        audit.Add("CLINIC_CLOSED", "clinic", clinic.ClinicId,
            after: new { closure.Reason, closure.StartsOn, closure.EndsOn, cancelled_bookings = cancelled }, reason: closure.Note);
        await db.SaveChangesAsync(ct);
        return new CreateClosureResponse(ToDto(closure, names), sessions.Count, cancelled, credited);
    }

    /// <summary>يرفض الإغلاق إذا كان في الوجبات مرضى حاضرون، أو حجوزات ولم يُطلب إلغاؤها.</summary>
    public async Task EnsureCanCancelAsync(List<DoctorSession> sessions, bool? cancelBookings, string scope, string scopeAr, CancellationToken ct)
    {
        var ids = sessions.Select(s => s.SessionId).ToArray();
        if (ids.Length == 0) return;
        var present = await db.ExistsAsync($"""
            SELECT EXISTS (SELECT 1 FROM queue_tickets WHERE session_id = ANY({ids})
                            AND status IN ('arrived', 'called', 'in_room')) AS "Value"
            """, ct);
        if (present)
            throw new AppException(409, $"{scope}_HAS_PATIENTS", $"في {scopeAr} مرضى حاضرون الآن",
                "أنهِ طابور اليوم أولاً، أو ابدأ الإغلاق من الغد");
        var bookings = await CountBookedAsync(ids, ct);
        if (bookings > 0 && cancelBookings != true)
            throw new AppException(409, $"{scope}_HAS_BOOKINGS", "توجد حجوزات في فترة الإغلاق",
                "أعد الطلب مع cancel_bookings = true لإلغائها (المدفوع يتحول إلى رصيد للمريض)")
            { Extensions = new Dictionary<string, object?> { ["bookings_count"] = bookings } };
    }

    private Task<int> CountBookedAsync(Guid[] sessionIds, CancellationToken ct) =>
        db.CountAsync($"""
            SELECT count(*)::int AS "Value" FROM bookings WHERE session_id = ANY({sessionIds}) AND status = 'booked'
            """, ct);

    /// <summary>
    /// يلغي الوجبات وحجوزاتها: المدفوع يتحول رصيداً للمريض، والتذاكر المنتظرة تُلغى.
    /// يُستخدم عند إغلاق عيادة أو فرع، وعند إيقاف طبيب أو فرع نهائياً.
    /// </summary>
    public async Task<(int Cancelled, int Credited)> CancelSessionsAsync(List<DoctorSession> sessions, string reason, CancellationToken ct)
    {
        var ids = sessions.Select(s => s.SessionId).ToArray();
        if (ids.Length == 0) return (0, 0);
        var affected = await notifications.ActiveBookingsAsync(ids, ct);
        var now = clock.UtcNow;
        foreach (var s in sessions)
        {
            s.Status = SessionStatuses.Cancelled;
            s.CancelReason = reason;
        }
        var credited = await db.ExecuteAsync($"""
            INSERT INTO patient_credit_ledger (entry_id, patient_id, branch_id, amount, kind, booking_id, created_by, created_at)
            SELECT gen_random_uuid(), b.patient_id, s.branch_id, b.price, 'booking_cancel_credit', b.booking_id, {current.StaffUserId}, {now}
              FROM bookings b JOIN doctor_sessions s ON s.session_id = b.session_id
             WHERE b.session_id = ANY({ids}) AND b.status = 'booked' AND b.payment_status = 'paid' AND b.price > 0
            """, ct);
        await db.ExecuteAsync($"""
            UPDATE bookings SET payment_status = 'credited'
             WHERE session_id = ANY({ids}) AND status = 'booked' AND payment_status = 'paid'
            """, ct);
        var cancelled = await db.ExecuteAsync($"""
            UPDATE bookings SET status = 'cancelled', cancel_reason = {reason}, cancelled_at = {now}, cancelled_by = 'system'
             WHERE session_id = ANY({ids}) AND status = 'booked'
            """, ct);
        await db.ExecuteAsync($"""
            UPDATE queue_tickets SET status = 'cancelled', queue_position = NULL
             WHERE session_id = ANY({ids}) AND status = 'waiting'
            """, ct);
        await notifications.BookingsCancelledAsync(affected, reason, ct);
        // إلغاء تلقائي تابع لإغلاق أو إيقاف: تحديث مباشر للاستقبال وتطبيق المريض والشاشات
        foreach (var id in ids) await notifier.SessionChangedAsync(id, ct);
        foreach (var b in affected) await notifier.BookingChangedAsync(b.BookingId, Queue.BookingChangeActions.Cancelled, null, ct);
        return (cancelled, credited);
    }

    /// <summary>
    /// إعادة الفتح: إغلاق قادم يُلغى، وإغلاق سارٍ ينتهي من اليوم. الوجبات القادمة التي ألغاها الإغلاق تعود مفتوحة
    /// (الحجوزات التي أُلغيت لا تعود؛ يُعاد حجزها).
    /// </summary>
    public async Task<ClosureDto> EndAsync(Guid clinicId, Guid closureId, CancellationToken ct)
    {
        await LoadClinicAsync(clinicId, manage: true, ct);
        var closure = await db.ClinicClosures.FirstOrDefaultAsync(c => c.ClosureId == closureId && c.ClinicId == clinicId, ct)
                      ?? throw AppException.NotFound("الإغلاق غير موجود");
        var today = clock.BaghdadToday();
        if (closure.EndedAt is not null || closure.EndsOn < today)
            throw new AppException(409, "CLOSURE_ENDED", "الإغلاق منتهٍ مسبقاً");

        var now = clock.UtcNow;
        var reopenFrom = closure.StartsOn > today ? closure.StartsOn : today;
        if (closure.StartsOn < today) closure.EndsOn = today.AddDays(-1); // سرى أياماً: يبقى سجله، وينتهي أمس
        else { closure.EndedAt = now; closure.EndedBy = current.UserId; }   // لم يبدأ أو بدأ اليوم: يُلغى

        var marker = ClosureMarker + "%";
        var restored = await db.ExecuteAsync($"""
            UPDATE doctor_sessions s SET status = 'open', cancel_reason = NULL
             WHERE s.clinic_id = {clinicId} AND s.status = 'cancelled' AND s.session_date >= {reopenFrom}
               AND s.cancel_reason LIKE {marker}
               AND NOT EXISTS (SELECT 1 FROM branch_closures b
                                WHERE b.branch_id = s.branch_id AND b.ended_at IS NULL AND b.starts_on <= s.session_date
                                  AND (b.ends_on IS NULL OR b.ends_on >= s.session_date))
            """, ct);
        // الأيام التي يغطيها إغلاق الفرع أيضاً تبقى ملغاة لكن باسم إغلاق الفرع، لتعود عند إعادة فتحه
        var branchMarker = BranchClosureMarker;
        await db.ExecuteAsync($"""
            UPDATE doctor_sessions s SET cancel_reason = {branchMarker} || coalesce(l.name_ar, b.reason)
              FROM branch_closures b LEFT JOIN lookup_values l ON l.category = 'closure_reason' AND l.code = b.reason
             WHERE s.clinic_id = {clinicId} AND s.status = 'cancelled' AND s.session_date >= {reopenFrom}
               AND s.cancel_reason LIKE {marker}
               AND b.branch_id = s.branch_id AND b.ended_at IS NULL AND b.starts_on <= s.session_date
               AND (b.ends_on IS NULL OR b.ends_on >= s.session_date)
            """, ct);
        audit.Add("CLINIC_REOPENED", "clinic", clinicId, after: new { closure_id = closureId, restored_sessions = restored });
        await db.SaveChangesAsync(ct);
        return ToDto(closure, await ReasonNamesAsync(ct));
    }

    /// <summary>بادئة سبب إلغاء الوجبات بسبب الإغلاق، لتمييزها عند إعادة الفتح.</summary>
    public const string ClosureMarker = "إغلاق مؤقت - ";

    private async Task<Clinic> LoadClinicAsync(Guid clinicId, bool manage, CancellationToken ct)
    {
        var clinic = await db.Clinics.FirstOrDefaultAsync(c => c.ClinicId == clinicId, ct) ?? throw AppException.NotFound("العيادة غير موجودة");
        if (current.Role == Roles.Admin) return clinic;
        if (clinic.BranchId != current.BranchId) throw AppException.NotFound("العيادة غير موجودة");
        if (manage && !(current.Has(Permissions.ServicesManage)
                        && await db.UserBranches.AnyAsync(ub => ub.UserId == current.UserId && ub.BranchId == clinic.BranchId, ct)))
            throw AppException.Forbidden("إغلاق العيادة لمدير الفرع أو المدير العام");
        return clinic;
    }

    public Task<Dictionary<string, string>> ReasonNamesAsync(CancellationToken ct) =>
        lookups.NamesAsync(LookupCategories.ClosureReason, ct);

    public static string ReasonName(string code, IReadOnlyDictionary<string, string> names) =>
        names.TryGetValue(code, out var n) ? n : code;

    /// <summary>نص السبب للعرض: "صيانة: تبديل جهاز التكييف".</summary>
    public static string Label(ClinicClosure c, IReadOnlyDictionary<string, string> names) => Label(c.Reason, c.Note, names);

    public static string Label(string reason, string? note, IReadOnlyDictionary<string, string> names) =>
        $"{ReasonName(reason, names)}{(note is null ? "" : ": " + note)}";

    public ClosureDto ToDto(ClinicClosure c, IReadOnlyDictionary<string, string> names)
    {
        var status = StatusOf(c.StartsOn, c.EndsOn, c.EndedAt);
        return new ClosureDto(c.ClosureId, c.ClinicId, c.Reason, ReasonName(c.Reason, names), c.Note, c.StartsOn, c.EndsOn, status, c.CreatedAt, c.EndedAt);
    }

    private string StatusOf(DateOnly starts, DateOnly? ends, DateTimeOffset? endedAt)
    {
        var today = clock.BaghdadToday();
        return endedAt is not null ? "cancelled"
            : ends < today ? "ended"
            : starts > today ? "scheduled"
            : "active";
    }

    // ================================================================== إغلاق الفرع كاملاً

    /// <summary>بادئة سبب إلغاء الوجبات بسبب إغلاق الفرع، لتمييزها عن إغلاق العيادة عند إعادة الفتح.</summary>
    public const string BranchClosureMarker = "إغلاق الفرع - ";

    public async Task<BranchClosure?> BranchActiveOnAsync(Guid branchId, DateOnly date, CancellationToken ct) =>
        await db.BranchClosures.AsNoTracking()
            .Where(c => c.BranchId == branchId && c.EndedAt == null && c.StartsOn <= date && (c.EndsOn == null || c.EndsOn >= date))
            .OrderBy(c => c.StartsOn).FirstOrDefaultAsync(ct);

    /// <summary>الإغلاق الساري أو القادم لعدة فروع.</summary>
    public async Task<Dictionary<Guid, BranchClosure>> BranchCurrentOrNextAsync(IEnumerable<Guid> branchIds, CancellationToken ct)
    {
        var ids = branchIds.ToList();
        var today = clock.BaghdadToday();
        var list = await db.BranchClosures.AsNoTracking()
            .Where(c => ids.Contains(c.BranchId) && c.EndedAt == null && (c.EndsOn == null || c.EndsOn >= today))
            .OrderBy(c => c.StartsOn).ToListAsync(ct);
        return list.GroupBy(c => c.BranchId).ToDictionary(g => g.Key, g => g.First());
    }

    public async Task<IReadOnlyList<BranchClosureDto>> ListBranchAsync(Guid branchId, CancellationToken ct)
    {
        await LoadBranchAsync(branchId, manage: false, ct);
        var list = await db.BranchClosures.AsNoTracking().Where(c => c.BranchId == branchId)
            .OrderByDescending(c => c.StartsOn).Take(100).ToListAsync(ct);
        var names = await ReasonNamesAsync(ct);
        return list.Select(c => ToDto(c, names)).ToList();
    }

    /// <summary>
    /// إغلاق الفرع مؤقتاً: كل عياداته وأقسامه تُغلق في الفترة، ووجبات الأطباء فيها تُلغى
    /// (مع حجوزاتها إذا طُلب cancel_bookings). يعود الفرع مفتوحاً تلقائياً بعد ends_on.
    /// </summary>
    public async Task<CreateBranchClosureResponse> CreateBranchAsync(Guid branchId, CreateClosureRequest req, CancellationToken ct)
    {
        var branch = await LoadBranchAsync(branchId, manage: true, ct);
        var today = clock.BaghdadToday();
        var starts = req.StartsOn ?? today;
        var reasonItem = await lookups.ResolveActiveAsync(LookupCategories.ClosureReason, req.Reason, ct);
        new Validator()
            .Check(reasonItem is not null, "reason", "اختر سبباً من قائمة أسباب الإغلاق (GET /lookups/closure_reason)")
            .Check(reasonItem?.Code != LookupCategories.OtherReason || !string.IsNullOrWhiteSpace(req.Note), "note", "اكتب سبب الإغلاق")
            .Check(req.Note is null || req.Note.Length <= 500, "note", "500 حرف كحد أقصى")
            .Check(starts >= today, "starts_on", "تاريخ البداية اليوم أو بعده")
            .Check(req.EndsOn is null || req.EndsOn >= starts, "ends_on", "تاريخ النهاية بعد البداية")
            .Check(req.EndsOn is null || req.EndsOn.Value.DayNumber - starts.DayNumber < MaxClosureDays, "ends_on", "سنة كحد أقصى")
            .ThrowIfInvalid();
        if (!branch.IsActive)
            throw new AppException(409, "BRANCH_INACTIVE", "الفرع موقوف نهائياً", "أعد تفعيل الفرع أولاً");
        var ends = req.EndsOn;
        var farEnd = ends ?? DateOnly.MaxValue;

        if (await db.BranchClosures.AnyAsync(c => c.BranchId == branchId && c.EndedAt == null
                && c.StartsOn <= farEnd && (c.EndsOn == null || c.EndsOn >= starts), ct))
            throw new AppException(409, "CLOSURE_OVERLAP", "يوجد إغلاق آخر للفرع في نفس الفترة", "عدّل الإغلاق الحالي أو أنهِه أولاً");

        var sessions = await db.DoctorSessions
            .Where(s => s.BranchId == branchId && s.SessionDate >= starts && s.SessionDate <= farEnd && s.Status != SessionStatuses.Cancelled)
            .ToListAsync(ct);
        await EnsureCanCancelAsync(sessions, req.CancelBookings, "BRANCH", "الفرع", ct);

        var closure = new BranchClosure
        {
            ClosureId = UuidV7.New(),
            BranchId = branchId,
            Reason = reasonItem!.Code,
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            StartsOn = starts,
            EndsOn = ends,
            CreatedBy = current.UserId,
            CreatedAt = clock.UtcNow,
        };
        db.BranchClosures.Add(closure);
        var names = new Dictionary<string, string> { [reasonItem.Code] = reasonItem.NameAr };
        var (cancelled, credited) = await CancelSessionsAsync(sessions, BranchClosureMarker + Label(closure.Reason, closure.Note, names), ct);

        audit.Add("BRANCH_CLOSED", "branch", branchId,
            after: new { closure.Reason, closure.StartsOn, closure.EndsOn, cancelled_bookings = cancelled }, reason: closure.Note);
        await db.SaveChangesAsync(ct);
        return new CreateBranchClosureResponse(ToDto(closure, names), sessions.Count, cancelled, credited);
    }

    /// <summary>إعادة فتح الفرع: الوجبات التي ألغاها إغلاق الفرع تعود (عدا الأيام التي تقع في إغلاق عيادتها الخاص).</summary>
    public async Task<BranchClosureDto> EndBranchAsync(Guid branchId, Guid closureId, CancellationToken ct)
    {
        await LoadBranchAsync(branchId, manage: true, ct);
        var closure = await db.BranchClosures.FirstOrDefaultAsync(c => c.ClosureId == closureId && c.BranchId == branchId, ct)
                      ?? throw AppException.NotFound("الإغلاق غير موجود");
        var today = clock.BaghdadToday();
        if (closure.EndedAt is not null || closure.EndsOn < today)
            throw new AppException(409, "CLOSURE_ENDED", "الإغلاق منتهٍ مسبقاً");

        var reopenFrom = closure.StartsOn > today ? closure.StartsOn : today;
        if (closure.StartsOn < today) closure.EndsOn = today.AddDays(-1);
        else { closure.EndedAt = clock.UtcNow; closure.EndedBy = current.UserId; }

        var marker = BranchClosureMarker + "%";
        var restored = await db.ExecuteAsync($"""
            UPDATE doctor_sessions s SET status = 'open', cancel_reason = NULL
             WHERE s.branch_id = {branchId} AND s.status = 'cancelled' AND s.session_date >= {reopenFrom}
               AND s.cancel_reason LIKE {marker}
               AND NOT EXISTS (SELECT 1 FROM clinic_closures c
                                WHERE c.clinic_id = s.clinic_id AND c.ended_at IS NULL AND c.starts_on <= s.session_date
                                  AND (c.ends_on IS NULL OR c.ends_on >= s.session_date))
            """, ct);
        // الأيام التي يغطيها إغلاق عيادة خاص تبقى ملغاة باسم إغلاق العيادة، لتعود عند إعادة فتحها
        var clinicMarker = ClosureMarker;
        await db.ExecuteAsync($"""
            UPDATE doctor_sessions s SET cancel_reason = {clinicMarker} || coalesce(l.name_ar, c.reason)
              FROM clinic_closures c LEFT JOIN lookup_values l ON l.category = 'closure_reason' AND l.code = c.reason
             WHERE s.branch_id = {branchId} AND s.status = 'cancelled' AND s.session_date >= {reopenFrom}
               AND s.cancel_reason LIKE {marker}
               AND c.clinic_id = s.clinic_id AND c.ended_at IS NULL AND c.starts_on <= s.session_date
               AND (c.ends_on IS NULL OR c.ends_on >= s.session_date)
            """, ct);
        audit.Add("BRANCH_REOPENED", "branch", branchId, after: new { closure_id = closureId, restored_sessions = restored });
        await db.SaveChangesAsync(ct);
        return ToDto(closure, await ReasonNamesAsync(ct));
    }

    public BranchClosureDto ToDto(BranchClosure c, IReadOnlyDictionary<string, string> names) =>
        new(c.ClosureId, c.BranchId, c.Reason, ReasonName(c.Reason, names), c.Note, c.StartsOn, c.EndsOn,
            StatusOf(c.StartsOn, c.EndsOn, c.EndedAt), c.CreatedAt, c.EndedAt);

    /// <summary>المدير العام لكل الفروع، ومدير الفرع لفرعه المسند إليه. القراءة لموظفي الفرع.</summary>
    private async Task<Branch> LoadBranchAsync(Guid branchId, bool manage, CancellationToken ct)
    {
        var branch = await db.Branches.FirstOrDefaultAsync(b => b.BranchId == branchId, ct) ?? throw AppException.NotFound("الفرع غير موجود");
        if (current.Role == Roles.Admin) return branch;
        if (branch.BranchId != current.BranchId) throw AppException.NotFound("الفرع غير موجود");
        if (manage && !(current.Has(Permissions.ServicesManage)
                        && await db.UserBranches.AnyAsync(ub => ub.UserId == current.UserId && ub.BranchId == branchId, ct)))
            throw AppException.Forbidden("إغلاق الفرع لمديره أو المدير العام");
        return branch;
    }
}
