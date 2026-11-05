using System.Text.RegularExpressions;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Branches;

/// <summary>
/// status: open (مفتوح) | closed (مغلق مؤقتاً، والسبب في status_reason) | inactive (موقوف نهائياً).
/// closure: الإغلاق الساري أو القادم إن وُجد.
/// </summary>
public record BranchDto(Guid BranchId, string Code, string Name, string? City, string? Address, string? Phone,
    decimal? Latitude, decimal? Longitude, string? WorkingHours, bool IsActive,
    string Status = "open", string StatusAr = "مفتوح", string? StatusReason = null, string? StatusReasonAr = null,
    DateOnly? ClosedUntil = null, BranchClosureDto? Closure = null,
    IReadOnlyList<WorkingDayDto>? WorkingHoursSchedule = null, string? WorkingHoursSummary = null, IReadOnlyList<WorkingShiftDto>? TodayHours = null,
    string LiveStatus = "open", string LiveStatusAr = "مفتوح",
    IReadOnlyList<BranchManagerDto>? Managers = null, BranchDepartmentCountsDto? DepartmentCounts = null, BranchActivityDto? Activity = null,
    bool IsAssigned = true);

/// <summary>مدير الفرع: موظف فعال بدور branch_manager مسند للفرع.</summary>
public record BranchManagerDto(Guid UserId, string FullName, string? PhoneNumber);
/// <summary>عدد الأقسام الفعالة حسب النوع.</summary>
public record BranchDepartmentCountsDto(int Clinic, int Lab, int Radiology, int Pharmacy, int Total);
/// <summary>نشاط الفرع الآن: ورديات صندوق مفتوحة، ومرضى حاضرون اليوم (في الانتظار بعد الحضور، مستدعون، داخل الغرفة).</summary>
public record BranchActivityDto(int OpenShifts, int PatientsWaiting, int PatientsInRoom);
public record ClinicDto(Guid ClinicId, Guid BranchId, string Name, string Department, short? Floor, bool IsActive,
    bool IsOpenToday = true, ClosureDto? Closure = null)
{
    /// <summary>مدة العودة المجانية بالنتائج لأطباء العيادة؛ null = إعداد الفرع queue.free_return_days.</summary>
    public short? FreeReturnDays { get; init; }
}

public record CreateBranchRequest(string? Code, string? Name, string? City, string? Address, string? Phone,
    decimal? Latitude, decimal? Longitude, string? WorkingHours, List<WorkingDayInput>? WorkingHoursSchedule = null);
/// <summary>working_hours_schedule: null = بلا تغيير، [] = إلغاء الجدول المهيكل، وإلا الأسبوع كاملاً (الأيام غير المذكورة مغلقة).</summary>
public record UpdateBranchRequest(string? Name, string? City, string? Address, string? Phone, bool? IsActive,
    decimal? Latitude, decimal? Longitude, string? WorkingHours, bool? CancelBookings, List<WorkingDayInput>? WorkingHoursSchedule = null);
public record CreateClinicRequest(string? Name, string? Department, short? Floor);
/// <summary>free_return_days: null = بلا تغيير، -1 = إعداد الفرع، 0..90 = قيمة العيادة.</summary>
public record UpdateClinicRequest(string? Name, string? Department, short? Floor, bool? IsActive, short? FreeReturnDays = null);

/// <summary>
/// الفروع (المدير العام فقط) والعيادات والأقسام (المدير العام، ومدير الفرع لفرعه).
/// القسم يحدد نوع العيادة: clinic (عيادة طبيب) | lab (مختبر) | radiology (أشعة وسونار) | pharmacy (صيدلية).
/// </summary>
public partial class BranchService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, ClosureService closures)
{
    public static readonly string[] Departments = ["clinic", "lab", "radiology", "pharmacy"];

    [GeneratedRegex("^[A-Z][A-Z0-9]{0,9}$")]
    private static partial Regex CodePattern();

    private bool IsAdmin => current.Role == Roles.Admin;

    // ------------------------------------------------------------------ الفروع

    /// <summary>
    /// شبكة الفروع. المدير العام: كل الفروع بكل التفاصيل. غيره: الفروع الفعالة (معلوماتها العامة كما في تطبيق المريض)،
    /// والتفاصيل الداخلية (المدراء، عدد الأقسام، النشاط، "يعمل بعد الدوام") لفروعه المسندة إليه فقط (is_assigned).
    /// </summary>
    public async Task<IReadOnlyList<BranchDto>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var list = await db.Branches.AsNoTracking().Where(b => b.IsActive || (includeInactive && IsAdmin)).OrderBy(b => b.Code).ToListAsync(ct);
        return await WithStatusAsync(list, ct, details: true);
    }

    public async Task<BranchDto> GetAsync(Guid branchId, CancellationToken ct)
    {
        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.BranchId == branchId && (IsAdmin || (b.IsActive && b.BranchId == current.BranchId)), ct)
                     ?? throw AppException.NotFound("الفرع غير موجود");
        return (await WithStatusAsync([branch], ct, details: true))[0];
    }

    /// <summary>
    /// يضيف حالة الفرع اليوم (مفتوح، مغلق مؤقتاً مع السبب، موقوف) والإغلاق الساري أو القادم، وجدول الدوام والحالة الحية.
    /// details: المدراء وعدد الأقسام والنشاط للفروع التي يراها المستخدم الحالي (المدير العام: كلها، غيره: المسندة إليه).
    /// </summary>
    public async Task<List<BranchDto>> WithStatusAsync(List<Branch> list, CancellationToken ct, bool details = false)
    {
        var ids = list.Select(b => b.BranchId).ToList();
        var map = await closures.BranchCurrentOrNextAsync(ids, ct);
        var names = map.Count == 0 ? [] : await closures.ReasonNamesAsync(ct);
        var today = clock.BaghdadToday();
        var now = clock.BaghdadNow();
        var hours = (await db.BranchWorkingHours.AsNoTracking().Where(h => ids.Contains(h.BranchId)).ToListAsync(ct))
            .GroupBy(h => h.BranchId).ToDictionary(g => g.Key, g => g.ToList());

        HashSet<Guid> visible = [];
        if (details && current.IsAuthenticated && !current.IsPatient)
            visible = IsAdmin
                ? [.. ids]
                : [.. await db.UserBranches.AsNoTracking().Where(ub => ub.UserId == current.UserId && ids.Contains(ub.BranchId)).Select(ub => ub.BranchId).ToListAsync(ct)];
        var detailIds = visible.ToList();
        Dictionary<Guid, List<BranchManagerDto>> managers = [];
        Dictionary<Guid, BranchDepartmentCountsDto> counts = [];
        Dictionary<Guid, BranchActivityDto> activity = [];
        if (detailIds.Count > 0)
        {
            managers = (await db.Users.AsNoTracking()
                    .Where(u => u.IsActive && u.Role == Roles.BranchManager)
                    .SelectMany(u => u.Branches.Where(ub => detailIds.Contains(ub.BranchId)), (u, ub) => new { ub.BranchId, u.UserId, u.FullName, u.PhoneNumber })
                    .ToListAsync(ct))
                .GroupBy(x => x.BranchId)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.FullName).Select(x => new BranchManagerDto(x.UserId, x.FullName, x.PhoneNumber)).ToList());
            counts = (await db.Clinics.AsNoTracking().Where(c => detailIds.Contains(c.BranchId) && c.IsActive)
                    .GroupBy(c => new { c.BranchId, c.Department }).Select(g => new { g.Key.BranchId, g.Key.Department, Count = g.Count() })
                    .ToListAsync(ct))
                .GroupBy(x => x.BranchId)
                .ToDictionary(g => g.Key, g =>
                {
                    int Of(string d) => g.Where(x => x.Department == d).Sum(x => x.Count);
                    return new BranchDepartmentCountsDto(Of("clinic"), Of("lab"), Of("radiology"), Of("pharmacy"), g.Sum(x => x.Count));
                });
            var shifts = await db.Shifts.AsNoTracking().Where(s => detailIds.Contains(s.BranchId) && s.Status == ShiftStatuses.Open)
                .GroupBy(s => s.BranchId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            var dayStart = ClockExtensions.ToUtc(today, TimeOnly.MinValue);
            var present = new[] { TicketStatuses.Arrived, TicketStatuses.Called, TicketStatuses.InRoom };
            var tickets = await db.QueueTickets.AsNoTracking()
                .Where(t => detailIds.Contains(t.BranchId) && present.Contains(t.Status) && t.CreatedAt >= dayStart)
                .GroupBy(t => new { t.BranchId, InRoom = t.Status == TicketStatuses.InRoom }).Select(g => new { g.Key.BranchId, g.Key.InRoom, Count = g.Count() })
                .ToListAsync(ct);
            activity = detailIds.ToDictionary(id => id, id => new BranchActivityDto(
                shifts.GetValueOrDefault(id),
                tickets.Where(t => t.BranchId == id && !t.InRoom).Sum(t => t.Count),
                tickets.Where(t => t.BranchId == id && t.InRoom).Sum(t => t.Count)));
        }

        return list.Select(b =>
        {
            map.TryGetValue(b.BranchId, out var c);
            var rows = hours.GetValueOrDefault(b.BranchId) ?? [];
            var schedule = BranchHours.Schedule(rows);
            var isVisible = visible.Contains(b.BranchId);
            var act = activity.GetValueOrDefault(b.BranchId);
            var dto = ToDto(b) with
            {
                Closure = c is null ? null : closures.ToDto(c, names),
                WorkingHoursSchedule = schedule,
                WorkingHoursSummary = BranchHours.Summary(schedule),
                TodayHours = schedule is null ? null : BranchHours.Today(rows, today),
                Managers = isVisible ? managers.GetValueOrDefault(b.BranchId) ?? [] : null,
                DepartmentCounts = isVisible ? counts.GetValueOrDefault(b.BranchId) ?? new BranchDepartmentCountsDto(0, 0, 0, 0, 0) : null,
                Activity = isVisible ? act : null,
                IsAssigned = isVisible,
            };
            if (!b.IsActive) return dto with { Status = "inactive", StatusAr = "موقوف", LiveStatus = "inactive", LiveStatusAr = "موقوف" };
            if (c is not null && c.Covers(today))
                return dto with
                {
                    Status = "closed",
                    StatusAr = "مغلق مؤقتاً",
                    StatusReason = c.Reason,
                    StatusReasonAr = ClosureService.Label(c.Reason, c.Note, names),
                    ClosedUntil = c.EndsOn,
                    LiveStatus = "temporarily_closed",
                    LiveStatusAr = "مغلق مؤقتاً",
                };
            var busy = act is { } a && (a.OpenShifts > 0 || a.PatientsWaiting > 0 || a.PatientsInRoom > 0);
            return BranchHours.IsWithinHours(rows, now) switch
            {
                true or null => dto with { LiveStatus = "open", LiveStatusAr = "مفتوح" },
                false when busy => dto with { LiveStatus = "open_after_hours", LiveStatusAr = "يعمل بعد الدوام" },
                false => dto with { LiveStatus = "closed", LiveStatusAr = "مغلق الآن" },
            };
        }).ToList();
    }

    public async Task<BranchDto> CreateBranchAsync(CreateBranchRequest req, CancellationToken ct)
    {
        if (!IsAdmin && !current.Has(Permissions.BranchesCreate)) EnsureAdmin();
        var code = req.Code?.Trim().ToUpperInvariant();
        new Validator()
            .Check(code is not null && CodePattern().IsMatch(code), "code", "رمز الفرع: حرف إنجليزي ثم حروف أو أرقام (10 كحد أقصى)، مثل B2")
            .Check(!string.IsNullOrWhiteSpace(req.Name) && req.Name.Trim().Length <= 100, "name", "اسم الفرع مطلوب (100 حرف كحد أقصى)")
            .Check(req.City is null || req.City.Length <= 100, "city", "100 حرف كحد أقصى")
            .Check(req.Address is null || req.Address.Length <= 255, "address", "255 حرفاً كحد أقصى")
            .Check(req.Phone is null || req.Phone.Length <= 30, "phone", "30 حرفاً كحد أقصى")
            .Check(ValidLocation(req.Latitude, req.Longitude), "latitude", LocationError)
            .Check(req.WorkingHours is null || req.WorkingHours.Length <= 255, "working_hours", "255 حرفاً كحد أقصى")
            .ThrowIfInvalid();
        if (await db.Branches.AnyAsync(b => b.Code == code, ct))
            throw new AppException(409, "DUPLICATE_BRANCH_CODE", "رمز الفرع مستخدم", "رمز الفرع يدخل في أرقام الملفات والفواتير ولا يتكرر");

        var branch = new Branch
        {
            BranchId = UuidV7.New(),
            Code = code!,
            Name = req.Name!.Trim(),
            City = req.City?.Trim(),
            Address = req.Address?.Trim(),
            Phone = req.Phone?.Trim(),
            Latitude = req.Latitude,
            Longitude = req.Longitude,
            WorkingHours = string.IsNullOrWhiteSpace(req.WorkingHours) ? null : req.WorkingHours.Trim(),
            CreatedAt = clock.UtcNow,
        };
        if (req.WorkingHoursSchedule is { } schedule)
        {
            branch.Hours = BranchHours.Parse(branch.BranchId, schedule);
            // النص القديم يبقى مقروءاً (تطبيق المريض): يُولد من الجدول إذا لم يُرسل
            if (branch.WorkingHours is null) branch.WorkingHours = BranchHours.Summary(BranchHours.Schedule(branch.Hours));
        }
        db.Branches.Add(branch);
        audit.Add("BRANCH_CREATED", "branch", branch.BranchId, after: new { branch = ToDto(branch), working_hours_schedule = BranchHours.Schedule(branch.Hours) });
        await db.SaveChangesAsync(ct);
        return (await WithStatusAsync([branch], ct, details: true))[0];
    }

    /// <summary>رمز الفرع لا يتغير بعد الإنشاء لأنه جزء من الأرقام المطبوعة.</summary>
    public async Task<BranchDto> UpdateBranchAsync(Guid branchId, UpdateBranchRequest req, CancellationToken ct)
    {
        // المدير العام لأي فرع؛ من يملك branches.edit لفروعه المسندة إليه فقط، والإيقاف النهائي يتطلب branches.delete
        await EnsureCanManageBranchAsync(branchId, ct, Permissions.BranchesEdit);
        if (req.IsActive is not null && !IsAdmin && !current.Has(Permissions.BranchesDelete))
            throw AppException.Forbidden("إيقاف الفرع وإعادة تفعيله للمدير العام فقط");
        var branch = await db.Branches.Include(b => b.Hours).FirstOrDefaultAsync(b => b.BranchId == branchId, ct) ?? throw AppException.NotFound("الفرع غير موجود");
        new Validator()
            .Check(req.Name is null || (req.Name.Trim().Length is > 0 and <= 100), "name", "اسم الفرع (100 حرف كحد أقصى)")
            .Check(req.City is null || req.City.Length <= 100, "city", "100 حرف كحد أقصى")
            .Check(req.Address is null || req.Address.Length <= 255, "address", "255 حرفاً كحد أقصى")
            .Check(req.Phone is null || req.Phone.Length <= 30, "phone", "30 حرفاً كحد أقصى")
            .Check(req.Latitude is null && req.Longitude is null || ValidLocation(req.Latitude, req.Longitude), "latitude", LocationError)
            .Check(req.WorkingHours is null || req.WorkingHours.Length <= 255, "working_hours", "255 حرفاً كحد أقصى")
            .ThrowIfInvalid();
        var deactivating = req.IsActive == false && branch.IsActive;
        if (deactivating && branch.BranchId == current.BranchId)
            throw new AppException(409, "BRANCH_IN_USE", "لا يمكن إيقاف الفرع الذي تعمل عليه الآن", "انتقل لفرع آخر أولاً");

        var newHours = req.WorkingHoursSchedule is { } schedule ? BranchHours.Parse(branch.BranchId, schedule) : null;
        var before = new { branch = ToDto(branch), working_hours_schedule = BranchHours.Schedule(branch.Hours) };
        if (deactivating)
        {
            // الإيقاف النهائي لا يترك حجوزات معلقة: يُرفض إذا كان فيه مرضى حاضرون، وتُلغى الحجوزات القادمة مع cancel_bookings
            var today = clock.BaghdadToday();
            var sessions = await db.DoctorSessions
                .Where(s => s.BranchId == branchId && s.SessionDate >= today && s.Status != SessionStatuses.Cancelled)
                .ToListAsync(ct);
            await closures.EnsureCanCancelAsync(sessions, req.CancelBookings, "BRANCH", "الفرع", ct);
            await closures.CancelSessionsAsync(sessions, "إيقاف الفرع", ct);
        }
        if (req.Name is not null) branch.Name = req.Name.Trim();
        if (req.City is not null) branch.City = req.City.Trim();
        if (req.Address is not null) branch.Address = req.Address.Trim();
        if (req.Phone is not null) branch.Phone = req.Phone.Trim();
        if (req.Latitude is not null) { branch.Latitude = req.Latitude; branch.Longitude = req.Longitude; }
        if (req.WorkingHours is not null) branch.WorkingHours = req.WorkingHours.Trim().Length == 0 ? null : req.WorkingHours.Trim();
        if (req.IsActive is { } active) branch.IsActive = active;
        if (newHours is not null)
        {
            // تعديل في المكان بنفس المفتاح (الفرع، اليوم، الفترة) بدل حذف وإضافة
            foreach (var old in branch.Hours.ToList())
            {
                var match = newHours.FirstOrDefault(h => h.Weekday == old.Weekday && h.ShiftNo == old.ShiftNo);
                if (match is null) { branch.Hours.Remove(old); db.BranchWorkingHours.Remove(old); }
                else { old.OpensAt = match.OpensAt; old.ClosesAt = match.ClosesAt; }
            }
            foreach (var h in newHours.Where(h => !branch.Hours.Any(o => o.Weekday == h.Weekday && o.ShiftNo == h.ShiftNo)))
                branch.Hours.Add(h);
            if (req.WorkingHours is null && newHours.Count > 0) branch.WorkingHours = BranchHours.Summary(BranchHours.Schedule(newHours));
        }
        audit.Add(deactivating ? "BRANCH_DEACTIVATED" : "BRANCH_UPDATED", "branch", branch.BranchId, before,
            new { branch = ToDto(branch), working_hours_schedule = BranchHours.Schedule(branch.Hours) });
        await db.SaveChangesAsync(ct);
        return (await WithStatusAsync([branch], ct, details: true))[0];
    }

    private const string LocationError = "خط العرض (latitude بين -90 و 90) والطول (longitude بين -180 و 180) يُرسلان معاً";

    private static bool ValidLocation(decimal? lat, decimal? lng) =>
        (lat is null && lng is null) || (lat is >= -90 and <= 90 && lng is >= -180 and <= 180);

    // ------------------------------------------------------------------ العيادات والأقسام

    public async Task<IReadOnlyList<ClinicDto>> ListClinicsAsync(Guid branchId, string? department, bool includeInactive, CancellationToken ct)
    {
        if (!await db.Branches.AnyAsync(b => b.BranchId == branchId && (b.IsActive || IsAdmin), ct))
            throw AppException.NotFound("الفرع غير موجود");
        // عزل الفروع: غير المدير العام يرى أقسام فروعه المسندة إليه فقط
        if (!IsAdmin && branchId != current.BranchId
            && !await db.UserBranches.AnyAsync(ub => ub.UserId == current.UserId && ub.BranchId == branchId, ct))
            throw AppException.NotFound("الفرع غير موجود");
        var showInactive = includeInactive && current.Has(Permissions.ServicesManage);
        var q = db.Clinics.Where(c => c.BranchId == branchId && (c.IsActive || showInactive));
        if (!string.IsNullOrWhiteSpace(department)) q = q.Where(c => c.Department == department);
        var list = await q.OrderBy(c => c.Department).ThenBy(c => c.Name).ToListAsync(ct);
        return await WithClosuresAsync(list, ct);
    }

    public async Task<ClinicDto> CreateClinicAsync(Guid branchId, CreateClinicRequest req, CancellationToken ct)
    {
        await EnsureCanManageBranchAsync(branchId, ct);
        new Validator()
            .Check(!string.IsNullOrWhiteSpace(req.Name) && req.Name.Trim().Length <= 100, "name", "اسم العيادة مطلوب (100 حرف كحد أقصى)")
            .Check(req.Department is not null && Departments.Contains(req.Department), "department", "clinic | lab | radiology | pharmacy")
            .Check(req.Floor is null or >= -5 and <= 100, "floor", "رقم طابق غير صالح")
            .ThrowIfInvalid();
        var name = req.Name!.Trim();
        if (await db.Clinics.AnyAsync(c => c.BranchId == branchId && c.IsActive && c.Name == name, ct))
            throw new AppException(409, "DUPLICATE_CLINIC", "توجد عيادة بنفس الاسم في هذا الفرع");

        var clinic = new Clinic
        {
            ClinicId = UuidV7.New(),
            BranchId = branchId,
            Name = name,
            Department = req.Department!,
            Floor = req.Floor,
            CreatedAt = clock.UtcNow,
        };
        db.Clinics.Add(clinic);
        audit.Add("CLINIC_CREATED", "clinic", clinic.ClinicId, after: ToDto(clinic));
        await db.SaveChangesAsync(ct);
        return ToDto(clinic);
    }

    public async Task<ClinicDto> UpdateClinicAsync(Guid clinicId, UpdateClinicRequest req, CancellationToken ct)
    {
        var clinic = await db.Clinics.FirstOrDefaultAsync(c => c.ClinicId == clinicId, ct) ?? throw AppException.NotFound("العيادة غير موجودة");
        await EnsureCanManageBranchAsync(clinic.BranchId, ct);
        new Validator()
            .Check(req.Name is null || (req.Name.Trim().Length is > 0 and <= 100), "name", "اسم العيادة (100 حرف كحد أقصى)")
            .Check(req.Department is null || Departments.Contains(req.Department), "department", "clinic | lab | radiology | pharmacy")
            .Check(req.Floor is null or >= -5 and <= 100, "floor", "رقم طابق غير صالح")
            .Check(req.FreeReturnDays is null or -1 or >= 0 and <= 90, "free_return_days", "من 0 إلى 90، أو -1 لإعداد الفرع")
            .ThrowIfInvalid();

        var hasDoctors = await db.Doctors.AnyAsync(d => d.ClinicId == clinicId && d.IsActive, ct);
        var hasServices = await db.Services.AnyAsync(s => s.ClinicId == clinicId && s.IsActive, ct);
        if (req.Department is not null && req.Department != clinic.Department && (hasDoctors || hasServices))
            throw new AppException(409, "CLINIC_IN_USE", "لا يمكن تغيير نوع عيادة عليها أطباء أو خدمات",
                "انقل الأطباء وعطّل الخدمات أولاً، أو أنشئ عيادة جديدة");
        if (req.IsActive == false && clinic.IsActive && hasDoctors)
            throw new AppException(409, "CLINIC_HAS_DOCTORS", "لا يمكن إيقاف عيادة فيها أطباء فعالون", "انقل الأطباء أو عطّلهم أولاً");
        if (req.Name is not null && req.Name.Trim() != clinic.Name
            && await db.Clinics.AnyAsync(c => c.BranchId == clinic.BranchId && c.IsActive && c.Name == req.Name.Trim() && c.ClinicId != clinicId, ct))
            throw new AppException(409, "DUPLICATE_CLINIC", "توجد عيادة بنفس الاسم في هذا الفرع");

        var before = ToDto(clinic);
        if (req.Name is not null) clinic.Name = req.Name.Trim();
        if (req.Department is not null) clinic.Department = req.Department;
        if (req.Floor is not null) clinic.Floor = req.Floor;
        if (req.FreeReturnDays is not null) clinic.FreeReturnDays = req.FreeReturnDays == -1 ? null : req.FreeReturnDays;
        var deactivatedServices = 0;
        if (req.IsActive == false && clinic.IsActive && hasServices)
        {
            // خدمات القسم الموقوف (تحاليل، أشعة، كشفيات) لا تبقى قابلة للبيع
            var services = await db.Services.Where(s => s.ClinicId == clinicId && s.IsActive).ToListAsync(ct);
            foreach (var sv in services) sv.IsActive = false;
            deactivatedServices = services.Count;
        }
        if (req.IsActive is { } active) clinic.IsActive = active;
        audit.Add("CLINIC_UPDATED", "clinic", clinic.ClinicId, before,
            deactivatedServices > 0 ? new { clinic = ToDto(clinic), deactivated_services = deactivatedServices } : ToDto(clinic));
        await db.SaveChangesAsync(ct);
        return (await WithClosuresAsync([clinic], ct))[0];
    }

    // ------------------------------------------------------------------ مساعدات

    /// <summary>يضيف حالة اليوم والإغلاق الساري أو القادم لكل عيادة.</summary>
    private async Task<List<ClinicDto>> WithClosuresAsync(List<Clinic> list, CancellationToken ct)
    {
        var map = await closures.CurrentOrNextAsync(list.Select(c => c.ClinicId), ct);
        var today = clock.BaghdadToday();
        var branchClosed = new HashSet<Guid>();
        foreach (var bid in list.Select(c => c.BranchId).Distinct())
            if (await closures.BranchActiveOnAsync(bid, today, ct) is not null) branchClosed.Add(bid);
        var names = map.Count == 0 ? [] : await closures.ReasonNamesAsync(ct);
        return list.Select(c =>
        {
            map.TryGetValue(c.ClinicId, out var closure);
            return ToDto(c) with
            {
                IsOpenToday = c.IsActive && !branchClosed.Contains(c.BranchId) && (closure is null || !closure.Covers(today)),
                Closure = closure is null ? null : closures.ToDto(closure, names),
            };
        }).ToList();
    }

    public async Task<ClinicDto> GetClinicAsync(Guid clinicId, CancellationToken ct)
    {
        var clinic = await db.Clinics.FirstOrDefaultAsync(c => c.ClinicId == clinicId && (c.BranchId == current.BranchId || IsAdmin), ct)
                     ?? throw AppException.NotFound("العيادة غير موجودة");
        return (await WithClosuresAsync([clinic], ct))[0];
    }

    private void EnsureAdmin()
    {
        if (!IsAdmin) throw AppException.Forbidden("إدارة الفروع للمدير العام فقط");
    }

    /// <summary>
    /// المدير العام لكل الفروع، وغيره لفروعه المسندة إليه فقط بشرط امتلاك إحدى الصلاحيات المطلوبة
    /// (الافتراضي services.manage، وهي لمدير الفرع من الأدوار النظامية).
    /// </summary>
    public async Task EnsureCanManageBranchAsync(Guid branchId, CancellationToken ct, params string[] anyOf)
    {
        if (!await db.Branches.AnyAsync(b => b.BranchId == branchId, ct)) throw AppException.NotFound("الفرع غير موجود");
        if (IsAdmin) return;
        var required = anyOf.Length == 0 ? [Permissions.ServicesManage] : anyOf;
        if (required.Any(current.Has)
            && await db.UserBranches.AnyAsync(ub => ub.UserId == current.UserId && ub.BranchId == branchId, ct))
            return;
        throw AppException.Forbidden("إدارة الفرع لمدير هذا الفرع أو المدير العام");
    }

    private static BranchDto ToDto(Branch b) =>
        new(b.BranchId, b.Code, b.Name, b.City, b.Address, b.Phone, b.Latitude, b.Longitude, b.WorkingHours, b.IsActive);
    private static ClinicDto ToDto(Clinic c) => new(c.ClinicId, c.BranchId, c.Name, c.Department, c.Floor, c.IsActive) { FreeReturnDays = c.FreeReturnDays };
}
