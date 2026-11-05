using System.Globalization;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Files;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Doctors;

public class DoctorService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, FileService files, IObjectStorage storage,
    Notifications.NotificationService notifications, SettingsReader settings, Queue.QueueNotifier notifier)
{
    public const int MaxSessionRangeDays = 62;
    public static readonly string[] WeekdayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    // ------------------------------------------------------------------ الأطباء

    public async Task<IReadOnlyList<DoctorDto>> ListAsync(Guid? clinicId, string? specialty, string? q, CancellationToken ct)
    {
        var query = db.Doctors.Include(d => d.Clinic)
            .Where(d => d.IsActive && d.Clinic!.BranchId == current.BranchId);
        if (clinicId is { } cid) query = query.Where(d => d.ClinicId == cid);
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
        return list.Select(ToDto).ToList();
    }

    public async Task<DoctorDto> GetAsync(Guid doctorId, CancellationToken ct) => ToDto(await LoadDoctorAsync(doctorId, ct));

    public async Task<DoctorSettingsDto> GetSettingsAsync(Guid doctorId, CancellationToken ct) => await SettingsDtoAsync(await LoadDoctorAsync(doctorId, ct), ct);

    /// <summary>إعدادات الطبيب كاملة (استبدال): إرسال null لحقل يعيده لإعداد الفرع.</summary>
    public async Task<DoctorSettingsDto> PutSettingsAsync(Guid doctorId, PutDoctorSettingsRequest req, CancellationToken ct)
    {
        new Validator()
            .Check(req.LateGraceMinutes is null or >= 0 and <= 180, "late_grace_minutes", "من 0 إلى 180")
            .Check(req.FollowUpDays is null or >= 0 and <= 180, "follow_up_days", "من 0 إلى 180")
            .Check(req.FollowUpPrice is null or >= 0, "follow_up_price", "صفر أو أكثر")
            .Check(req.OnlineDepositAmount is null or >= 0, "online_deposit_amount", "صفر أو أكثر")
            .ThrowIfInvalid();
        var d = await LoadDoctorAsync(doctorId, ct);
        var before = new { d.AllowExtra, d.LateGraceMinutes, d.FollowUpDays, d.FollowUpPrice, d.OnlineDepositAmount };
        d.AllowExtra = req.AllowExtra ?? true;
        d.LateGraceMinutes = req.LateGraceMinutes;
        d.FollowUpDays = req.FollowUpDays;
        d.FollowUpPrice = req.FollowUpPrice;
        d.OnlineDepositAmount = req.OnlineDepositAmount;
        audit.Add("DOCTOR_SETTINGS_CHANGED", "doctor", d.DoctorId, before: before,
            after: new { d.AllowExtra, d.LateGraceMinutes, d.FollowUpDays, d.FollowUpPrice, d.OnlineDepositAmount });
        await db.SaveChangesAsync(ct);
        return await SettingsDtoAsync(d, ct);
    }

    private async Task<DoctorSettingsDto> SettingsDtoAsync(Doctor d, CancellationToken ct)
    {
        var branch = d.Clinic!.BranchId;
        return new DoctorSettingsDto(d.AllowExtra, d.LateGraceMinutes, d.FollowUpDays, d.FollowUpPrice, d.OnlineDepositAmount,
            await settings.GetLongAsync(SettingsReader.NoShowGraceMinutes, branch, 20, ct),
            await settings.GetLongAsync(SettingsReader.FollowUpWindowDays, branch, 14, ct),
            await settings.GetLongAsync(SettingsReader.OnlineDepositAmount, branch, 5000, ct));
    }

    private DoctorDto ToDto(Doctor d) =>
        new(d.DoctorId, d.FullName, d.Specialty, d.JobTitle, d.ClinicId, d.Clinic!.Name, d.DefaultSlotMinutes, files.PhotoUrl(d.PhotoObjectKey));

    // ------------------------------------------------------------------ الصورة

    public async Task<DoctorDto> SetPhotoAsync(Guid doctorId, SetPhotoRequest req, CancellationToken ct)
    {
        new Validator().Check(req.FileId is not null, "file_id", "الملف مطلوب").ThrowIfInvalid();
        var doctor = await files.LoadDoctorForPhotoAsync(doctorId, ct);
        var file = await db.Files.FirstOrDefaultAsync(f => f.FileId == req.FileId && f.BranchId == current.BranchId, ct)
                   ?? throw new AppException(404, "FILE_NOT_FOUND", "الملف غير موجود");
        if (file.Purpose != FilePurposes.DoctorPhoto || !file.ObjectKey.StartsWith($"doctors/{doctorId:N}/"))
            throw new AppException(422, "INVALID_FILE_PURPOSE", "الملف ليس صورة لهذا الطبيب");
        if (file.Status != FileStatuses.Stored)
            throw new AppException(422, "FILE_NOT_STORED", "لم يكتمل رفع الملف", "أكّد الرفع أولاً عبر /files/{file_id}/complete");

        var oldKey = doctor.PhotoObjectKey;
        if (oldKey == file.ObjectKey) return ToDto(doctor);
        doctor.PhotoObjectKey = file.ObjectKey;
        audit.Add("DOCTOR_PHOTO_CHANGED", "doctor", doctorId, new { photo = oldKey }, new { photo = file.ObjectKey });
        await db.SaveChangesAsync(ct);
        await TryDeleteObjectAsync(oldKey);
        return ToDto(doctor);
    }

    public async Task DeletePhotoAsync(Guid doctorId, CancellationToken ct)
    {
        var doctor = await files.LoadDoctorForPhotoAsync(doctorId, ct);
        var oldKey = doctor.PhotoObjectKey;
        if (oldKey is null) return;
        doctor.PhotoObjectKey = null;
        audit.Add("DOCTOR_PHOTO_CHANGED", "doctor", doctorId, new { photo = oldKey }, new { photo = (string?)null });
        await db.SaveChangesAsync(ct);
        await TryDeleteObjectAsync(oldKey);
    }

    /// <summary>حذف الصورة القديمة لا يجب أن يُفشل العملية؛ إن تعذر يبقى الكائن يتيماً دون ضرر.</summary>
    private async Task TryDeleteObjectAsync(string? key)
    {
        if (key is null) return;
        try { await storage.DeleteAsync(key, CancellationToken.None); }
        catch { /* يُنظف لاحقاً */ }
    }

    // ------------------------------------------------------------------ النسب

    public async Task<CommissionHistoryDto> GetCommissionsAsync(Guid doctorId, CancellationToken ct)
    {
        await LoadDoctorAsync(doctorId, ct);
        var items = await db.DoctorCommissionRates.Where(r => r.DoctorId == doctorId)
            .OrderByDescending(r => r.EffectiveFrom).ToListAsync(ct);
        var today = clock.BaghdadToday();
        var currentRate = items.FirstOrDefault(r => r.EffectiveFrom <= today);
        return new CommissionHistoryDto(doctorId, currentRate is null ? null : ToDto(currentRate), items.Select(ToDto).ToList());
    }

    /// <summary>النسبة السارية في تاريخ معين (تُستخدم عند إنشاء بنود الفواتير). null = لا توجد نسبة.</summary>
    public async Task<DoctorCommissionRate?> RateOnAsync(Guid doctorId, DateOnly date, CancellationToken ct) =>
        await db.DoctorCommissionRates.Where(r => r.DoctorId == doctorId && r.EffectiveFrom <= date)
            .OrderByDescending(r => r.EffectiveFrom).FirstOrDefaultAsync(ct);

    public async Task<CommissionRateDto> AddCommissionAsync(Guid doctorId, AddCommissionRequest req, CancellationToken ct)
    {
        await LoadDoctorAsync(doctorId, ct);
        new Validator()
            .Check(req.NewVisitRate is >= 0 and <= 100 && decimal.Round(req.NewVisitRate.Value, 2) == req.NewVisitRate, "new_visit_rate", "نسبة بين 0 و 100 (رقمان عشريان كحد أقصى)")
            .Check(req.FollowUpRate is >= 0 and <= 100 && decimal.Round(req.FollowUpRate.Value, 2) == req.FollowUpRate, "follow_up_rate", "نسبة بين 0 و 100 (رقمان عشريان كحد أقصى)")
            .Check(req.EffectiveFrom is not null, "effective_from", "تاريخ السريان مطلوب")
            .ThrowIfInvalid();

        var today = clock.BaghdadToday();
        var hasAny = await db.DoctorCommissionRates.AnyAsync(r => r.DoctorId == doctorId, ct);
        // أول نسبة للطبيب يُسمح أن تكون بتاريخ سابق (إدخال البيانات الأولية)، بعدها التعديل يسري مستقبلاً فقط
        if (hasAny && req.EffectiveFrom < today)
            throw new AppException(422, "EFFECTIVE_DATE_IN_PAST", "تاريخ السريان في الماضي",
                "النسبة الجديدة تسري من اليوم أو بعده، ولا تغير الفواتير السابقة");

        var existing = await db.DoctorCommissionRates
            .FirstOrDefaultAsync(r => r.DoctorId == doctorId && r.EffectiveFrom == req.EffectiveFrom, ct);
        if (existing is not null && existing.EffectiveFrom <= today)
            throw new AppException(409, "DUPLICATE_EFFECTIVE_DATE", "توجد نسبة سارية بنفس التاريخ",
                "اختر تاريخ سريان لاحقاً");

        var now = clock.UtcNow;
        DoctorCommissionRate rate;
        if (existing is not null)
        {
            // نسبة مستقبلية لم تسرِ بعد: تُستبدل
            var before = ToDto(existing);
            existing.NewVisitRate = req.NewVisitRate!.Value;
            existing.FollowUpRate = req.FollowUpRate!.Value;
            existing.CreatedBy = current.UserId;
            existing.CreatedAt = now;
            rate = existing;
            audit.Add("COMMISSION_CHANGED", "doctor", doctorId, before, ToDto(rate));
        }
        else
        {
            rate = new DoctorCommissionRate
            {
                RateId = UuidV7.New(),
                DoctorId = doctorId,
                NewVisitRate = req.NewVisitRate!.Value,
                FollowUpRate = req.FollowUpRate!.Value,
                EffectiveFrom = req.EffectiveFrom!.Value,
                CreatedBy = current.UserId,
                CreatedAt = now,
            };
            db.DoctorCommissionRates.Add(rate);
            audit.Add("COMMISSION_CHANGED", "doctor", doctorId, after: ToDto(rate));
        }
        await db.SaveChangesAsync(ct);
        return ToDto(rate);
    }

    // ------------------------------------------------------------------ الجدول الأسبوعي

    public async Task<WeeklyScheduleDto> GetScheduleAsync(Guid doctorId, CancellationToken ct)
    {
        await LoadDoctorAsync(doctorId, ct);
        var templates = await ActiveTemplates(doctorId).ToListAsync(ct);
        return new WeeklyScheduleDto(doctorId, templates.Select(ToDto).ToList());
    }

    /// <summary>
    /// يستبدل الجدول الأسبوعي كاملاً. الوجبات المستقبلية التي لم تُحجز ولم يُقطع لها تذاكر تُحذف لتُولد من الجدول الجديد؛
    /// الوجبات التي عليها حجوزات تبقى كما هي (عدّلها أو ألغها يدوياً).
    /// </summary>
    public async Task<WeeklyScheduleDto> PutScheduleAsync(Guid doctorId, PutScheduleRequest req, CancellationToken ct)
    {
        var doctor = await LoadDoctorAsync(doctorId, ct);
        var slots = req.Weekly ?? [];
        var parsed = ValidateSchedule(slots);

        var before = (await ActiveTemplates(doctorId).ToListAsync(ct)).Select(ToDto).ToList();
        var old = await db.DoctorScheduleTemplates.Where(t => t.DoctorId == doctorId && t.BranchId == current.BranchId).ToListAsync(ct);
        db.DoctorScheduleTemplates.RemoveRange(old);

        var created = parsed.Select(p => new DoctorScheduleTemplate
        {
            SessionTemplateId = UuidV7.New(),
            DoctorId = doctorId,
            BranchId = current.BranchId,
            Weekday = p.Weekday,
            StartTime = p.Start,
            EndTime = p.End,
            SlotMinutes = p.Slot,
            OnlineQuotaPercent = p.Quota,
            IsActive = true,
        }).ToList();
        db.DoctorScheduleTemplates.AddRange(created);

        audit.Add("SCHEDULE_CHANGED", "doctor", doctor.DoctorId, before, created.Select(ToDto).ToList());
        await db.SaveChangesAsync(ct);

        var today = clock.BaghdadToday();
        await db.ExecuteAsync($"""
            DELETE FROM doctor_sessions s
             WHERE s.doctor_id = {doctorId} AND s.branch_id = {current.BranchId}
               AND s.session_date > {today} AND s.status = 'open'
               AND NOT EXISTS (SELECT 1 FROM bookings b WHERE b.session_id = s.session_id)
               AND NOT EXISTS (SELECT 1 FROM queue_tickets q WHERE q.session_id = s.session_id)
               AND NOT EXISTS (SELECT 1 FROM fast_forward_offers f WHERE f.session_id = s.session_id)
            """, ct);

        return new WeeklyScheduleDto(doctorId, created.OrderBy(t => t.Weekday).ThenBy(t => t.StartTime).Select(ToDto).ToList());
    }

    // ------------------------------------------------------------------ الوجبات اليومية

    /// <summary>الوجبات في فترة (تُولد تلقائياً من الجدول الأسبوعي عند أول طلب).</summary>
    public async Task<IReadOnlyList<SessionDto>> GetSessionsAsync(Guid doctorId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        await LoadDoctorAsync(doctorId, ct);
        var today = clock.BaghdadToday();
        var start = from ?? today;
        var end = to ?? start.AddDays(6);
        new Validator()
            .Check(end >= start, "to", "يجب أن يكون بعد from")
            .Check(end.DayNumber - start.DayNumber < MaxSessionRangeDays, "to", $"الفترة {MaxSessionRangeDays} يوماً كحد أقصى")
            .ThrowIfInvalid();

        var sessions = await EnsureSessionsAsync(doctorId, start, end, ct);
        var closedDays = sessions.Count == 0 ? [] : await ClosedWeekdaysAsync(sessions[0].BranchId, ct);
        return sessions.Select(s => ToDto(s) with { BranchClosedDay = closedDays.Contains((short)s.SessionDate.DayOfWeek) }).ToList();
    }

    /// <summary>أيام الأسبوع المغلقة حسب جدول دوام الفرع (0 = الأحد). فرع بلا جدول مهيكل: لا أيام مغلقة.</summary>
    public async Task<HashSet<short>> ClosedWeekdaysAsync(Guid branchId, CancellationToken ct)
    {
        var open = await db.BranchWorkingHours.AsNoTracking().Where(h => h.BranchId == branchId).Select(h => h.Weekday).Distinct().ToListAsync(ct);
        return open.Count == 0 ? [] : Enumerable.Range(0, 7).Select(d => (short)d).Except(open).ToHashSet();
    }

    /// <summary>يولد الوجبات الناقصة من الجدول الأسبوعي للأيام من اليوم فصاعداً، ويعيد كل الوجبات في الفترة.</summary>
    public async Task<List<DoctorSession>> EnsureSessionsAsync(Guid doctorId, DateOnly from, DateOnly to, CancellationToken ct, bool allowPast = false)
    {
        var doctor = await db.Doctors.Include(d => d.Clinic).FirstAsync(d => d.DoctorId == doctorId, ct);
        var branchId = doctor.Clinic!.BranchId; // فرع الطبيب (يعمل أيضاً للطلبات العامة بدون توكن)
        var today = clock.BaghdadToday();
        var templates = await db.DoctorScheduleTemplates
            .Where(t => t.DoctorId == doctorId && t.BranchId == branchId && t.IsActive).ToListAsync(ct);
        var existing = await db.DoctorSessions
            .Where(s => s.DoctorId == doctorId && s.BranchId == branchId && s.SessionDate >= from && s.SessionDate <= to)
            .ToListAsync(ct);

        // لا تُولد وجبات لأيام مضت، إلا لعملية أوفلاين حدثت قبل منتصف الليل وزُومنت بعده (الوجبة حدثت فعلاً)
        var genFrom = from < today && !allowPast ? today : from;
        // أيام الإغلاق المؤقت للعيادة: تُولد الوجبة ملغاة بسبب الإغلاق، وتعود مفتوحة عند إعادة الفتح
        var closuresInRange = await db.ClinicClosures.AsNoTracking()
            .Where(c => c.ClinicId == doctor.ClinicId && c.EndedAt == null && c.StartsOn <= to && (c.EndsOn == null || c.EndsOn >= genFrom))
            .ToListAsync(ct);
        var branchClosuresInRange = await db.BranchClosures.AsNoTracking()
            .Where(c => c.BranchId == branchId && c.EndedAt == null && c.StartsOn <= to && (c.EndsOn == null || c.EndsOn >= genFrom))
            .ToListAsync(ct);
        var reasonNames = closuresInRange.Count == 0 && branchClosuresInRange.Count == 0 ? new Dictionary<string, string>()
            : await db.LookupValues.AsNoTracking().Where(l => l.Category == LookupCategories.ClosureReason)
                .ToDictionaryAsync(l => l.Code, l => l.NameAr, ct);
        var added = new List<DoctorSession>();
        // ما حدث أوفلاين حدث فعلاً، فلا يمنعه جدول الدوام
        var closedDays = allowPast ? [] : await ClosedWeekdaysAsync(branchId, ct);
        for (var day = genFrom; day <= to; day = day.AddDays(1))
        {
            if (closedDays.Contains((short)day.DayOfWeek)) continue;
            foreach (var t in templates.Where(t => t.Weekday == (short)day.DayOfWeek))
            {
                // وجبة موجودة لنفس اليوم تتداخل مع وقت القالب (حتى لو عُدّل وقتها أو تغير الجدول اليوم): لا تُولد وجبة مكررة
                if (existing.Any(s => s.SessionDate == day && (s.StartTime == t.StartTime || (s.StartTime < t.EndTime && t.StartTime < s.EffectiveEndTime)))) continue;
                var capacity = DoctorSession.Capacity(t.StartTime, t.EndTime, t.SlotMinutes);
                added.Add(new DoctorSession
                {
                    SessionId = UuidV7.New(),
                    DoctorId = doctorId,
                    BranchId = branchId,
                    ClinicId = doctor.ClinicId,
                    SessionDate = day,
                    StartTime = t.StartTime,
                    EndTime = t.EndTime,
                    SlotMinutes = t.SlotMinutes,
                    CapacityTotal = capacity,
                    OnlineQuota = DoctorSession.OnlineQuotaFor(capacity, t.OnlineQuotaPercent),
                    Status = closuresInRange.Any(c => c.Covers(day)) || branchClosuresInRange.Any(c => c.Covers(day))
                        ? SessionStatuses.Cancelled : SessionStatuses.Open,
                    CancelReason = closuresInRange.FirstOrDefault(c => c.Covers(day)) is { } cl
                        ? Branches.ClosureService.ClosureMarker + Branches.ClosureService.Label(cl, reasonNames)
                        : branchClosuresInRange.FirstOrDefault(c => c.Covers(day)) is { } bc
                            ? Branches.ClosureService.BranchClosureMarker + Branches.ClosureService.Label(bc.Reason, bc.Note, reasonNames)
                            : null,
                    CreatedAt = clock.UtcNow,
                });
            }
        }

        if (added.Count > 0)
        {
            // إدراج آمن مع التزامن: إذا ولّد طلب آخر نفس الوجبة في نفس اللحظة يُتجاهل التكرار
            foreach (var s in added)
            {
                await db.ExecuteAsync($"""
                    INSERT INTO doctor_sessions (session_id, doctor_id, branch_id, clinic_id, session_date, start_time, end_time,
                                                 slot_minutes, capacity_total, online_quota, status, cancel_reason, created_at)
                    VALUES ({s.SessionId}, {s.DoctorId}, {s.BranchId}, {s.ClinicId}, {s.SessionDate}, {s.StartTime}, {s.EndTime},
                            {s.SlotMinutes}, {s.CapacityTotal}, {s.OnlineQuota}, {s.Status}, {s.CancelReason}, {s.CreatedAt})
                    ON CONFLICT (doctor_id, session_date, start_time) DO NOTHING
                    """, ct);
            }
            existing = await db.DoctorSessions.AsNoTracking()
                .Where(s => s.DoctorId == doctorId && s.BranchId == branchId && s.SessionDate >= from && s.SessionDate <= to)
                .ToListAsync(ct);
        }

        return existing.OrderBy(s => s.SessionDate).ThenBy(s => s.StartTime).ToList();
    }

    /// <summary>إلغاء كل وجبات الطبيب في يوم معين (إجازة، سفر...).</summary>
    public async Task<CancelSessionResponse> CancelDayAsync(Guid doctorId, DateOnly date, CancelSessionRequest req, CancellationToken ct)
    {
        await LoadDoctorAsync(doctorId, ct);
        new Validator().Check(!string.IsNullOrWhiteSpace(req.Reason), "reason", "سبب الإلغاء مطلوب").ThrowIfInvalid();
        if (date < clock.BaghdadToday())
            throw new AppException(422, "DATE_IN_PAST", "لا يمكن إلغاء وجبة يوم مضى");

        await EnsureSessionsAsync(doctorId, date, date, ct);
        var sessions = await db.DoctorSessions
            .Where(s => s.DoctorId == doctorId && s.BranchId == current.BranchId && s.SessionDate == date && s.Status != SessionStatuses.Cancelled)
            .ToListAsync(ct);
        if (sessions.Count == 0)
            throw AppException.NotFound("لا توجد وجبة للطبيب في هذا اليوم");

        var ids = sessions.Select(s => s.SessionId).ToArray();

        var hasTickets = await db.ExistsAsync($"""
            SELECT EXISTS (SELECT 1 FROM queue_tickets WHERE session_id = ANY({ids})
                            AND status NOT IN ('waiting', 'cancelled', 'no_show')) AS "Value"
            """, ct);
        if (hasTickets)
            throw new AppException(409, "SESSION_STARTED", "الوجبة بدأت وفيها مرضى في الطابور");

        var notify = await notifications.ActiveBookingsAsync(ids, ct);
        foreach (var s in sessions)
        {
            s.Status = SessionStatuses.Cancelled;
            s.CancelReason = req.Reason!.Trim();
        }
        var now = clock.UtcNow;
        // المبالغ المدفوعة للحجوزات الملغاة تتحول إلى رصيد للمريض
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
        var affected = await db.ExecuteAsync($"""
            UPDATE bookings SET status = 'cancelled', cancel_reason = {"إلغاء وجبة الطبيب: " + req.Reason!.Trim()}, cancelled_at = {now},
                   cancelled_by = 'staff'
             WHERE session_id = ANY({ids}) AND status = 'booked'
            """, ct);
        await db.ExecuteAsync($"""
            UPDATE queue_tickets SET status = 'cancelled', queue_position = NULL
             WHERE session_id = ANY({ids}) AND status = 'waiting'
            """, ct);

        audit.Add("SESSION_CANCELLED", "doctor", doctorId, after: new { date, session_ids = ids, affected_bookings = affected, credited_bookings = credited }, reason: req.Reason);
        await db.SaveChangesAsync(ct);
        await notifications.BookingsCancelledAsync(notify, "إلغاء وجبة الطبيب: " + req.Reason!.Trim(), ct);
        // المرحلة 3: تحديث مباشر للاستقبال وتطبيق المريض والشاشات
        foreach (var s in sessions) await notifier.SessionChangedAsync(s.SessionId, ct);
        foreach (var b in notify) await notifier.BookingChangedAsync(b.BookingId, Queue.BookingChangeActions.Cancelled, null, ct);
        return new CancelSessionResponse(sessions.Count, affected, 0);
    }

    // ------------------------------------------------------------------ أدوات

    private IQueryable<DoctorScheduleTemplate> ActiveTemplates(Guid doctorId) =>
        db.DoctorScheduleTemplates
            .Where(t => t.DoctorId == doctorId && t.BranchId == current.BranchId && t.IsActive)
            .OrderBy(t => t.Weekday).ThenBy(t => t.StartTime);

    private async Task<Doctor> LoadDoctorAsync(Guid doctorId, CancellationToken ct) =>
        await db.Doctors.Include(d => d.Clinic)
            .FirstOrDefaultAsync(d => d.DoctorId == doctorId && d.Clinic!.BranchId == current.BranchId, ct)
        ?? throw AppException.NotFound("الطبيب غير موجود في هذا الفرع");

    private record ParsedSlot(short Weekday, TimeOnly Start, TimeOnly End, short Slot, short Quota);

    private static List<ParsedSlot> ValidateSchedule(List<ScheduleSlotDto> slots)
    {
        var v = new Validator();
        var parsed = new List<ParsedSlot>();
        v.Check(slots.Count <= 21, "weekly", "21 وجبة أسبوعياً كحد أقصى");

        for (var i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            var key = $"weekly[{i}]";
            var dayIdx = Array.IndexOf(WeekdayNames, s.Weekday?.Trim().ToLowerInvariant());
            var startOk = TimeOnly.TryParseExact(s.Start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start);
            var endOk = TimeOnly.TryParseExact(s.End, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end);
            v.Check(dayIdx >= 0, $"{key}.weekday", "اليوم: sun, mon, tue, wed, thu, fri, sat")
             .Check(startOk, $"{key}.start", "الوقت بصيغة HH:mm")
             .Check(endOk, $"{key}.end", "الوقت بصيغة HH:mm")
             .Check(!startOk || !endOk || end > start, $"{key}.end", "نهاية الوجبة بعد بدايتها (الوجبة لا تتجاوز منتصف الليل)")
             .Check(s.SlotMinutes is > 0 and <= 240, $"{key}.slot_minutes", "مدة المعاينة بين 1 و 240 دقيقة")
             .Check(s.OnlineQuotaPercent is null or >= 0 and <= 100, $"{key}.online_quota_percent", "النسبة بين 0 و 100");

            if (dayIdx >= 0 && startOk && endOk && end > start && s.SlotMinutes is > 0 and <= 240)
            {
                v.Check(DoctorSession.Capacity(start, end, s.SlotMinutes.Value) >= 1, $"{key}.slot_minutes", "مدة المعاينة أطول من الوجبة");
                var overlap = parsed.Any(p => p.Weekday == dayIdx && start < p.End && p.Start < end);
                v.Check(!overlap, $"{key}", "تتداخل مع وجبة أخرى في نفس اليوم");
                parsed.Add(new ParsedSlot((short)dayIdx, start, end, s.SlotMinutes.Value, s.OnlineQuotaPercent ?? 60));
            }
        }
        v.ThrowIfInvalid();
        return parsed;
    }

    private static ScheduleSlotDto ToDto(DoctorScheduleTemplate t)
    {
        var capacity = DoctorSession.Capacity(t.StartTime, t.EndTime, t.SlotMinutes);
        return new ScheduleSlotDto(t.SessionTemplateId, WeekdayNames[t.Weekday], t.StartTime.ToString("HH:mm"), t.EndTime.ToString("HH:mm"),
            t.SlotMinutes, t.OnlineQuotaPercent, capacity, DoctorSession.OnlineQuotaFor(capacity, t.OnlineQuotaPercent));
    }

    private static SessionDto ToDto(DoctorSession s) => new(
        s.SessionId, s.SessionDate, WeekdayNames[(int)s.SessionDate.DayOfWeek], s.StartTime.ToString("HH:mm"), s.EndTime.ToString("HH:mm"),
        ClockExtensions.ToUtc(s.SessionDate, s.StartTime), s.SlotMinutes, s.CapacityTotal, s.OnlineQuota, s.Status, s.CancelReason);

    private static CommissionRateDto ToDto(DoctorCommissionRate r) => new(r.NewVisitRate, r.FollowUpRate, r.EffectiveFrom, r.CreatedAt);
}
