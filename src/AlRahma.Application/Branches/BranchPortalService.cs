using AlRahma.Application.Abstractions;
using AlRahma.Application.Bookings;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Branches;

// ------------------------------------------------------------------ نظرة عامة (الإدارة)

public record OverviewDoctorDto(Guid DoctorId, string FullName, string Specialty, string? JobTitle, bool HasScheduleToday);
public record OverviewClinicDto(Guid ClinicId, string Name, string Department, string DepartmentAr, short? Floor, bool IsActive, bool IsOpenToday,
    ClosureDto? Closure, int ActiveServices, IReadOnlyList<OverviewDoctorDto> Doctors);
public record DepartmentCountDto(string Department, string DepartmentAr, int Total, int Active, int OpenToday);
public record StaffCountDto(string Role, string RoleAr, int Count);
public record BranchTodayDto(DateOnly Date, int Sessions, int OpenSessions, int Booked, int InQueue, int Completed, int NoShows);
public record BranchOverviewDto(BranchDto Branch, IReadOnlyList<DepartmentCountDto> Departments, IReadOnlyList<OverviewClinicDto> Clinics,
    IReadOnlyList<StaffCountDto> Staff, int ActiveDoctors, BranchTodayDto Today, IReadOnlyList<string> SetupWarnings);

// ------------------------------------------------------------------ تطبيق المريض

/// <summary>روابط فتح تطبيق الخرائط بمسار إلى الفرع. المسافة تُحسب في جهاز المريض، وموقعه لا يُرسل للسيرفر.</summary>
public record BranchDirectionsDto(string GoogleMapsUrl, string AppleMapsUrl, string WazeUrl, string GeoUri)
{
    public static BranchDirectionsDto? For(decimal? latitude, decimal? longitude)
    {
        if (latitude is not { } lat || longitude is not { } lng) return null;
        var at = FormattableString.Invariant($"{lat:0.######},{lng:0.######}");
        return new BranchDirectionsDto($"https://www.google.com/maps/dir/?api=1&destination={at}", $"https://maps.apple.com/?daddr={at}",
            $"https://waze.com/ul?ll={at}&navigate=yes", $"geo:{at}?q={at}");
    }
}
public record PublicBranchDto(Guid BranchId, string Code, string Name, string? City, string? Address, string? Phone,
    decimal? Latitude, decimal? Longitude, string? WorkingHours, string Status, string StatusAr, string? StatusReasonAr, DateOnly? ClosedUntil,
    IReadOnlyList<string> Departments, int DoctorsCount,
    IReadOnlyList<WorkingDayDto>? WorkingHoursSchedule = null, IReadOnlyList<WorkingShiftDto>? TodayHours = null, bool? OpenNow = null)
{
    public BranchDirectionsDto? Directions => BranchDirectionsDto.For(Latitude, Longitude);
}
public record PublicClinicDto(Guid ClinicId, string Name, short? Floor, bool IsOpenToday, string? ClosureLabel, DateOnly? ClosedUntil);
public record PublicDepartmentDto(string Department, string DepartmentAr, IReadOnlyList<PublicClinicDto> Clinics);
public record PublicSpecialtyDto(string Code, string NameAr, int DoctorsCount);
public record ScheduleConflictDto(Guid SessionId, Guid DoctorId, string DoctorName, string ClinicName, DateOnly Date, string WeekdayAr,
    string Start, string End, int ActiveBookings);
public record ScheduleConflictsDto(bool HasSchedule, int TotalBookings, IReadOnlyList<ScheduleConflictDto> Items);
public record PublicBranchHomeDto(PublicBranchDto Branch, IReadOnlyList<PublicDepartmentDto> Departments,
    IReadOnlyList<PublicSpecialtyDto> Specialties, IReadOnlyList<PublicDoctorDto> Doctors);

/// <summary>
/// ما يوجد داخل الفرع: شاشة "نظرة عامة" للمدير، والواجهة الرئيسية للفرع في تطبيق المريض.
/// </summary>
public class BranchPortalService(IAppDbContext db, IClock clock, BranchService branches, ClosureService closures,
    BookingService bookings)
{
    public static readonly IReadOnlyDictionary<string, string> DepartmentsAr = new Dictionary<string, string>
    {
        ["clinic"] = "العيادات",
        ["lab"] = "المختبر",
        ["radiology"] = "الأشعة والسونار",
        ["pharmacy"] = "الصيدلية",
    };

    // ------------------------------------------------------------------ نظرة عامة

    /// <summary>كل ما في الفرع: حالته، أقسامه وعياداته وأطباؤها، عدد موظفيه حسب الدور، وأرقام اليوم، وتنبيهات الإعداد الناقص.</summary>
    public async Task<BranchOverviewDto> OverviewAsync(Guid branchId, CancellationToken ct)
    {
        await branches.EnsureCanManageBranchAsync(branchId, ct, Permissions.BranchesView, Permissions.ServicesManage);
        var branchEntity = await db.Branches.AsNoTracking().FirstAsync(b => b.BranchId == branchId, ct);
        var branch = (await branches.WithStatusAsync([branchEntity], ct, details: true))[0];
        var today = clock.BaghdadToday();

        var clinicDtos = await branches.ListClinicsAsync(branchId, null, includeInactive: true, ct);
        var doctors = await db.Doctors.AsNoTracking().Where(d => d.IsActive && d.Clinic!.BranchId == branchId)
            .OrderBy(d => d.FullName).ToListAsync(ct);
        var weekday = (short)today.DayOfWeek;
        var scheduledToday = (await db.DoctorScheduleTemplates.AsNoTracking()
            .Where(t => t.BranchId == branchId && t.IsActive && t.Weekday == weekday).Select(t => t.DoctorId).ToListAsync(ct)).ToHashSet();
        var serviceCounts = await db.Services.AsNoTracking().Where(s => s.BranchId == branchId && s.IsActive && s.ClinicId != null)
            .GroupBy(s => s.ClinicId!.Value).Select(g => new { ClinicId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.ClinicId, x => x.Count, ct);

        var clinics = clinicDtos.Select(c => new OverviewClinicDto(c.ClinicId, c.Name, c.Department, DepartmentsAr[c.Department], c.Floor,
            c.IsActive, c.IsOpenToday, c.Closure, serviceCounts.GetValueOrDefault(c.ClinicId),
            doctors.Where(d => d.ClinicId == c.ClinicId)
                .Select(d => new OverviewDoctorDto(d.DoctorId, d.FullName, d.Specialty, d.JobTitle, scheduledToday.Contains(d.DoctorId))).ToList()))
            .ToList();

        var departments = BranchService.Departments.Select(dep =>
        {
            var inDep = clinicDtos.Where(c => c.Department == dep).ToList();
            return new DepartmentCountDto(dep, DepartmentsAr[dep], inDep.Count, inDep.Count(c => c.IsActive), inDep.Count(c => c.IsOpenToday));
        }).ToList();

        var staff = (await db.Users.AsNoTracking().Where(u => u.IsActive && u.Branches.Any(b => b.BranchId == branchId))
                .GroupBy(u => u.Role).Select(g => new { Role = g.Key, Count = g.Count() }).ToListAsync(ct))
            .Select(x => new StaffCountDto(x.Role, Permissions.RoleNamesAr.GetValueOrDefault(x.Role, x.Role), x.Count))
            .OrderBy(x => x.Role).ToList();

        var sessions = await db.DoctorSessions.AsNoTracking().Where(s => s.BranchId == branchId && s.SessionDate == today)
            .Select(s => new { s.SessionId, s.Status }).ToListAsync(ct);
        var sessionIds = sessions.Select(s => s.SessionId).ToList();
        var booked = await db.Bookings.CountAsync(b => sessionIds.Contains(b.SessionId) && b.Status == BookingStatuses.Booked, ct);
        var tickets = await db.QueueTickets.AsNoTracking().Where(t => sessionIds.Contains(t.SessionId))
            .GroupBy(t => t.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Status, x => x.Count, ct);
        var inQueue = new[] { TicketStatuses.Arrived, TicketStatuses.Called, TicketStatuses.InRoom }.Sum(s => tickets.GetValueOrDefault(s));
        var todayDto = new BranchTodayDto(today, sessions.Count, sessions.Count(s => s.Status != SessionStatuses.Cancelled), booked, inQueue,
            tickets.GetValueOrDefault(TicketStatuses.Completed), tickets.GetValueOrDefault(TicketStatuses.NoShow));

        return new BranchOverviewDto(branch, departments, clinics, staff, doctors.Count, todayDto,
            SetupWarnings(clinics, staff, doctors, scheduledToday.Count > 0 || sessions.Count > 0));
    }

    /// <summary>ما ينقص الفرع ليعمل: يساعد عند افتتاح فرع جديد.</summary>
    private static List<string> SetupWarnings(List<OverviewClinicDto> clinics, List<StaffCountDto> staff, List<Doctor> doctors, bool anySchedule)
    {
        var w = new List<string>();
        var active = clinics.Where(c => c.IsActive).ToList();
        if (active.All(c => c.Department != "clinic")) w.Add("لا توجد عيادة أطباء في الفرع");
        if (doctors.Count == 0) w.Add("لا يوجد أطباء في الفرع");
        else if (!anySchedule) w.Add("لم يُحدد جدول دوام لأي طبيب");
        foreach (var c in active.Where(c => c.ActiveServices == 0))
            w.Add(c.Department == "clinic" ? $"لم تُحدد كشفية لعيادة {c.Name}" : $"لا توجد خدمات وأسعار في {c.Name}");
        foreach (var c in active.Where(c => c.Department == "clinic" && c.Doctors.Count == 0))
            w.Add($"لا يوجد طبيب في {c.Name}");
        if (staff.All(s => s.Role != Roles.Receptionist)) w.Add("لا يوجد موظف استقبال في الفرع");
        if (staff.All(s => s.Role != Roles.BranchManager)) w.Add("لم يُعيَّن مدير للفرع");
        if (active.Any(c => c.Department == "lab") && staff.All(s => s.Role != Roles.LabTech)) w.Add("يوجد مختبر بلا فني مختبر");
        if (active.Any(c => c.Department == "radiology") && staff.All(s => s.Role != Roles.Radiologist)) w.Add("يوجد قسم أشعة بلا أخصائي أشعة");
        if (active.Any(c => c.Department == "pharmacy") && staff.All(s => s.Role != Roles.Pharmacist)) w.Add("توجد صيدلية بلا صيدلي");
        return w;
    }

    // ------------------------------------------------------------------ تطبيق المريض (بدون تسجيل دخول)

    /// <summary>حجوزات قائمة في أيام أغلقها جدول دوام الفرع: لا تُلغى تلقائياً، وتُعرض ليتصرف الفرع معها.</summary>
    public async Task<ScheduleConflictsDto> ScheduleConflictsAsync(Guid branchId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        await branches.EnsureCanManageBranchAsync(branchId, ct, Permissions.BranchesView, Permissions.ServicesManage);
        var start = from ?? clock.BaghdadToday();
        var end = to ?? start.AddDays(60);
        if (end < start || end.DayNumber - start.DayNumber > 366)
            throw AppException.Validation(new Dictionary<string, string[]> { ["to"] = ["الفترة 366 يوماً كحد أقصى"] });
        var open = await db.BranchWorkingHours.AsNoTracking().Where(h => h.BranchId == branchId).Select(h => h.Weekday).Distinct().ToListAsync(ct);
        if (open.Count == 0) return new ScheduleConflictsDto(false, 0, []);
        var rows = await (
            from s in db.DoctorSessions.AsNoTracking()
            join d in db.Doctors.AsNoTracking() on s.DoctorId equals d.DoctorId
            join c in db.Clinics.AsNoTracking() on s.ClinicId equals c.ClinicId
            where s.BranchId == branchId && s.SessionDate >= start && s.SessionDate <= end && s.Status != SessionStatuses.Cancelled
            let active = db.Bookings.Count(b => b.SessionId == s.SessionId && b.Status == BookingStatuses.Booked)
            where active > 0
            orderby s.SessionDate, s.StartTime
            select new { s, Doctor = d.FullName, Clinic = c.Name, Active = active }).ToListAsync(ct);
        var items = rows.Where(x => !open.Contains((short)x.s.SessionDate.DayOfWeek))
            .Select(x => new ScheduleConflictDto(x.s.SessionId, x.s.DoctorId, x.Doctor, x.Clinic, x.s.SessionDate,
                BranchHours.Week.First(w => w.Value == (short)x.s.SessionDate.DayOfWeek).NameAr, x.s.StartTime.ToString("HH:mm"),
                x.s.EffectiveEndTime.ToString("HH:mm"), x.Active)).ToList();
        return new ScheduleConflictsDto(true, items.Sum(i => i.ActiveBookings), items);
    }

    /// <summary>كل الفروع الفعالة مع حالتها اليوم، ليختار المريض فرعه.</summary>
    public async Task<IReadOnlyList<PublicBranchDto>> PublicListAsync(string? city, CancellationToken ct)
    {
        var q = db.Branches.AsNoTracking().Where(b => b.IsActive);
        if (!string.IsNullOrWhiteSpace(city)) q = q.Where(b => b.City == city.Trim());
        var list = await q.OrderBy(b => b.Code).ToListAsync(ct);
        return await ToPublicAsync(list, ct);
    }

    /// <summary>الواجهة الرئيسية للفرع: معلوماته وحالته، أقسامه وعياداته، الاختصاصات، والأطباء مع أسعار الكشفية.</summary>
    public async Task<PublicBranchHomeDto> PublicHomeAsync(Guid branchId, CancellationToken ct)
    {
        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.BranchId == branchId && b.IsActive, ct)
                     ?? throw new AppException(404, "BRANCH_NOT_FOUND", "الفرع غير موجود");
        var info = (await ToPublicAsync([branch], ct))[0];

        var clinics = await db.Clinics.AsNoTracking().Where(c => c.BranchId == branchId && c.IsActive)
            .OrderBy(c => c.Name).ToListAsync(ct);
        var closureMap = await closures.CurrentOrNextAsync(clinics.Select(c => c.ClinicId), ct);
        var names = await closures.ReasonNamesAsync(ct);
        var today = clock.BaghdadToday();
        var branchClosedToday = info.Status == "closed";
        var departments = BranchService.Departments
            .Select(dep => new PublicDepartmentDto(dep, DepartmentsAr[dep], clinics.Where(c => c.Department == dep).Select(c =>
            {
                closureMap.TryGetValue(c.ClinicId, out var cl);
                var closedToday = cl is not null && cl.Covers(today);
                return new PublicClinicDto(c.ClinicId, c.Name, c.Floor, !branchClosedToday && !closedToday,
                    closedToday ? ClosureService.Label(cl!, names) : null, closedToday ? cl!.EndsOn : null);
            }).ToList()))
            .Where(d => d.Clinics.Count > 0)
            .ToList();

        var doctors = await bookings.PublicDoctorsAsync(branchId, null, null, ct);
        var specialtyCodes = await db.LookupValues.AsNoTracking().Where(l => l.Category == LookupCategories.DoctorSpecialty)
            .ToDictionaryAsync(l => l.NameAr, l => l.Code, ct);
        var specialties = doctors.GroupBy(d => d.Specialty)
            .Select(g => new PublicSpecialtyDto(specialtyCodes.GetValueOrDefault(g.Key, g.Key), g.Key, g.Count()))
            .OrderBy(s => s.NameAr).ToList();

        return new PublicBranchHomeDto(info, departments, specialties, doctors);
    }

    private async Task<List<PublicBranchDto>> ToPublicAsync(List<Branch> list, CancellationToken ct)
    {
        var withStatus = await branches.WithStatusAsync(list, ct);
        var ids = list.Select(b => b.BranchId).ToList();
        var departments = await db.Clinics.AsNoTracking().Where(c => ids.Contains(c.BranchId) && c.IsActive)
            .Select(c => new { c.BranchId, c.Department }).Distinct().ToListAsync(ct);
        var doctorCounts = await db.Doctors.AsNoTracking().Where(d => d.IsActive && d.Clinic!.IsActive && ids.Contains(d.Clinic.BranchId))
            .GroupBy(d => d.Clinic!.BranchId).Select(g => new { BranchId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.BranchId, x => x.Count, ct);
        return withStatus.Select(b => new PublicBranchDto(b.BranchId, b.Code, b.Name, b.City, b.Address, b.Phone, b.Latitude, b.Longitude,
                b.WorkingHours, b.Status, b.StatusAr, b.StatusReasonAr, b.ClosedUntil,
                BranchService.Departments.Where(d => departments.Any(x => x.BranchId == b.BranchId && x.Department == d)).ToList(),
                doctorCounts.GetValueOrDefault(b.BranchId),
                b.WorkingHoursSchedule, b.TodayHours,
                b.WorkingHoursSchedule is null ? null : b.LiveStatus == "open"))
            .ToList();
    }
}
