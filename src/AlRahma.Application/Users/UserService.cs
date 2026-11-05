using System.Security.Cryptography;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Auth;
using AlRahma.Application.Common;
using AlRahma.Application.Security;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Users;

/// <summary>نتيجة إيقاف موظف: للطبيب عدد الوجبات والحجوزات القادمة التي أُلغيت.</summary>
public record DisableUserResponse(int CancelledSessions, int CancelledBookings, int CreditedBookings);

public class UserService(
    IAppDbContext db,
    IPasswordHasher hasher,
    ICurrentUser current,
    IAuditLogger audit,
    IClock clock,
    Lookups.LookupService lookups,
    Branches.ClosureService closures,
    AccessCache access)
{

    public async Task<PagedResult<UserDto>> ListAsync(UserListQuery q, CancellationToken ct)
    {
        var query = LoadQuery();

        if (!IsAdmin)
        {
            // مدير الفرع يرى موظفي فرعه الحالي فقط
            query = query.Where(u => u.Branches.Any(b => b.BranchId == current.BranchId));
        }

        if (q.BranchId is { } bid) query = query.Where(u => u.Branches.Any(b => b.BranchId == bid));
        if (!string.IsNullOrWhiteSpace(q.Role)) query = query.Where(u => u.Role == q.Role);
        if (q.IsActive is { } active) query = query.Where(u => u.IsActive == active);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = $"%{q.Q.Trim()}%";
            query = query.Where(u => EF.Functions.ILike(u.FullName, term) || EF.Functions.ILike(u.Username, term));
        }

        var paging = new PageQuery(q.Page, q.PageSize);
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(u => u.FullName)
            .Skip((paging.SafePage - 1) * paging.SafePageSize)
            .Take(paging.SafePageSize)
            .ToListAsync(ct);

        var roles = await access.RolesAsync(db, ct);
        return new PagedResult<UserDto>(items.Select(u => ToDto(u, roles)).ToList(), paging.SafePage, paging.SafePageSize, total);
    }

    public async Task<UserDto> GetAsync(Guid userId, CancellationToken ct) =>
        ToDto(await LoadManageableAsync(userId, ct), await access.RolesAsync(db, ct));

    public async Task<UserDto> CreateAsync(CreateUserRequest req, CancellationToken ct)
    {
        var roles = await access.RolesAsync(db, ct);
        var username = req.Username?.Trim().ToLowerInvariant() ?? "";
        new Validator()
            .Check(username.Length >= 3 && username.Length <= 100 && username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'),
                "username", "اسم المستخدم من 3 إلى 100 حرف إنجليزي أو رقم أو _ . -")
            .Check(!string.IsNullOrWhiteSpace(req.FullName), "full_name", "الاسم مطلوب")
            .Check(roles.Find(req.Role) is { IsActive: true } || (roles.Roles.Count == 0 && Domain.Common.Roles.IsValid(req.Role)),
                "role", "الدور غير صالح أو موقوف (GET /roles)")
            .Check(req.BranchIds is { Count: > 0 }, "branch_ids", "يجب تعيين فرع واحد على الأقل")
            .Check(!string.IsNullOrEmpty(req.TemporaryPassword), "temporary_password", "كلمة المرور المؤقتة مطلوبة")
            .ThrowIfInvalid();

        AuthService.EnsureStrongPassword(req.TemporaryPassword!);
        EnsureCanAssignRole(req.Role!, roles);
        var branchIds = req.BranchIds!.Distinct().ToList();
        var clinicIds = (req.ClinicIds ?? []).Distinct().ToList();
        var extras = (req.ExtraPermissions ?? []).Distinct().ToList();
        req = req with { DoctorProfile = await NormalizeProfileAsync(req.DoctorProfile, ct) };
        await ValidateAssignmentsAsync(req.Role!, branchIds, clinicIds, extras, req.DoctorProfile, requireDoctorProfile: true, ct);

        if (await db.Users.AnyAsync(u => u.Username == username, ct))
            throw new AppException(409, "USERNAME_TAKEN", "اسم المستخدم مستخدم مسبقاً");

        var now = clock.UtcNow;
        var user = new User
        {
            UserId = UuidV7.New(),
            Username = username,
            PasswordHash = hasher.Hash(req.TemporaryPassword!),
            FullName = req.FullName!.Trim(),
            PhoneNumber = req.PhoneNumber?.Trim(),
            Role = req.Role!,
            IsActive = true,
            MustChangePassword = true,
            CreatedAt = now,
            UpdatedAt = now,
            Branches = branchIds.Select(b => new UserBranch { BranchId = b }).ToList(),
            Clinics = clinicIds.Select(c => new UserClinic { ClinicId = c }).ToList(),
            ExtraPermissions = extras.Select(p => new UserPermission { Permission = p, GrantedBy = current.UserId, GrantedAt = now }).ToList(),
        };

        if (user.Role == Domain.Common.Roles.Doctor)
            user.Doctor = NewDoctor(user, req.DoctorProfile!, now);

        db.Users.Add(user);
        audit.Add("USER_CREATED", "user", user.UserId,
            after: new { user.Username, user.Role, branch_ids = branchIds, clinic_ids = clinicIds, extra_permissions = extras });
        await db.SaveChangesAsync(ct);
        access.InvalidateUser(user.UserId);
        return ToDto(user, roles);
    }

    public async Task<UserDto> UpdateAsync(Guid userId, UpdateUserRequest req, CancellationToken ct)
    {
        var user = await LoadManageableAsync(userId, ct);
        var roles = await access.RolesAsync(db, ct);
        var before = Snapshot(user);

        var role = req.Role ?? user.Role;
        new Validator()
            .Check(req.FullName is null || !string.IsNullOrWhiteSpace(req.FullName), "full_name", "الاسم لا يكون فارغاً")
            .Check(req.Role is null || req.Role == user.Role || roles.Find(req.Role) is { IsActive: true }
                   || (roles.Roles.Count == 0 && Domain.Common.Roles.IsValid(req.Role)), "role", "الدور غير صالح أو موقوف (GET /roles)")
            .Check(req.BranchIds is null || req.BranchIds.Count > 0, "branch_ids", "يجب تعيين فرع واحد على الأقل")
            .ThrowIfInvalid();

        if (req.Role is not null && req.Role != user.Role)
        {
            EnsureCanAssignRole(req.Role, roles);
            if (user.UserId == current.UserId)
                throw AppException.Forbidden("لا يمكنك تغيير دورك بنفسك");
        }

        var branchIds = req.BranchIds?.Distinct().ToList() ?? user.Branches.Select(b => b.BranchId).ToList();
        var clinicIds = req.ClinicIds?.Distinct().ToList() ?? user.Clinics.Select(c => c.ClinicId).ToList();
        var extras = req.ExtraPermissions?.Distinct().ToList() ?? user.ExtraPermissions.Select(p => p.Permission).ToList();
        var needsDoctorProfile = role == Domain.Common.Roles.Doctor && user.Doctor is null;
        req = req with { DoctorProfile = await NormalizeProfileAsync(req.DoctorProfile, ct) };
        await ValidateAssignmentsAsync(role, branchIds, clinicIds, extras, req.DoctorProfile, needsDoctorProfile, ct);

        var now = clock.UtcNow;
        if (req.FullName is not null) user.FullName = req.FullName.Trim();
        if (req.PhoneNumber is not null) user.PhoneNumber = req.PhoneNumber.Trim();
        user.Role = role;
        user.UpdatedAt = now;

        if (req.BranchIds is not null)
        {
            user.Branches.RemoveAll(b => !branchIds.Contains(b.BranchId));
            foreach (var b in branchIds.Where(b => user.Branches.All(x => x.BranchId != b)))
                user.Branches.Add(new UserBranch { UserId = user.UserId, BranchId = b });
        }
        if (req.ClinicIds is not null)
        {
            user.Clinics.RemoveAll(c => !clinicIds.Contains(c.ClinicId));
            foreach (var c in clinicIds.Where(c => user.Clinics.All(x => x.ClinicId != c)))
                user.Clinics.Add(new UserClinic { UserId = user.UserId, ClinicId = c });
        }
        if (req.ExtraPermissions is not null)
        {
            user.ExtraPermissions.RemoveAll(p => !extras.Contains(p.Permission));
            foreach (var p in extras.Where(p => user.ExtraPermissions.All(x => x.Permission != p)))
                user.ExtraPermissions.Add(new UserPermission { UserId = user.UserId, Permission = p, GrantedBy = current.UserId, GrantedAt = now });
        }

        if (role == Domain.Common.Roles.Doctor)
        {
            if (user.Doctor is null)
                user.Doctor = NewDoctor(user, req.DoctorProfile!, now);
            else if (req.DoctorProfile is { } dp)
            {
                if (!string.IsNullOrWhiteSpace(dp.Specialty)) user.Doctor.Specialty = dp.Specialty.Trim();
                if (dp.JobTitle is not null) user.Doctor.JobTitle = dp.JobTitle.Length == 0 ? null : dp.JobTitle;
                if (dp.ClinicId is { } cid) user.Doctor.ClinicId = cid;
                if (dp.DefaultSlotMinutes is { } slot) user.Doctor.DefaultSlotMinutes = slot;
            }
            user.Doctor.FullName = user.FullName;
            user.Doctor.IsActive = user.IsActive;
        }
        else if (user.Doctor is not null)
        {
            if (user.Doctor.IsActive) await CancelDoctorFutureAsync(user.Doctor.DoctorId, false, "تغيير دور الطبيب", ct);
            user.Doctor.IsActive = false; // يبقى السجل للتاريخ المالي والطبي
        }

        var after = Snapshot(user);
        var action = before.Role != after.Role ? "USER_ROLE_CHANGED" : "USER_UPDATED";
        audit.Add(action, "user", user.UserId, before, after);
        await db.SaveChangesAsync(ct);
        access.InvalidateUser(user.UserId);
        return ToDto(user, roles);
    }

    public async Task<DisableUserResponse> SetActiveAsync(Guid userId, bool active, CancellationToken ct, bool cancelBookings = false)
    {
        if (userId == current.UserId && !active)
            throw AppException.Forbidden("لا يمكنك تعطيل حسابك بنفسك");

        var user = await LoadManageableAsync(userId, ct);
        if (user.IsActive == active) return new DisableUserResponse(0, 0, 0);

        var (sessions, cancelled, credited) = !active && user.Doctor is { IsActive: true } doctor
            ? await CancelDoctorFutureAsync(doctor.DoctorId, cancelBookings, "إيقاف الطبيب", ct)
            : (0, 0, 0);
        var now = clock.UtcNow;
        user.IsActive = active;
        user.UpdatedAt = now;
        if (user.Doctor is not null) user.Doctor.IsActive = active;
        if (!active)
        {
            var tokens = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
            foreach (var t in tokens) t.RevokedAt = now;
        }
        audit.Add(active ? "USER_ENABLED" : "USER_DISABLED", "user", userId,
            after: sessions > 0 ? new { cancelled_sessions = sessions, cancelled_bookings = cancelled } : null);
        await db.SaveChangesAsync(ct);
        access.InvalidateUser(userId);
        return new DisableUserResponse(sessions, cancelled, credited);
    }

    /// <summary>
    /// قبل إيقاف طبيب: وجباته القادمة (من اليوم) لا تبقى معلقة. إذا كان عليها حجوزات يُرفض الإيقاف
    /// إلا مع cancel_bookings = true، فتُلغى الوجبات والحجوزات (المدفوع يتحول رصيداً للمريض).
    /// </summary>
    private async Task<(int Sessions, int Cancelled, int Credited)> CancelDoctorFutureAsync(Guid doctorId, bool cancelBookings, string reason,
        CancellationToken ct)
    {
        var today = clock.BaghdadToday();
        var sessions = await db.DoctorSessions
            .Where(s => s.DoctorId == doctorId && s.SessionDate >= today && s.Status != SessionStatuses.Cancelled)
            .ToListAsync(ct);
        await closures.EnsureCanCancelAsync(sessions, cancelBookings, "DOCTOR", "وجبات الطبيب", ct);
        var (cancelled, credited) = await closures.CancelSessionsAsync(sessions, reason, ct);
        return (sessions.Count, cancelled, credited);
    }

    public async Task<ResetPasswordResponse> ResetPasswordAsync(Guid userId, CancellationToken ct)
    {
        var user = await LoadManageableAsync(userId, ct);
        var temp = GenerateTemporaryPassword();
        var now = clock.UtcNow;

        user.PasswordHash = hasher.Hash(temp);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.UpdatedAt = now;
        var tokens = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in tokens) t.RevokedAt = now;

        audit.Add("USER_PASSWORD_RESET", "user", userId);
        await db.SaveChangesAsync(ct);
        access.InvalidateUser(userId);
        return new ResetPasswordResponse(temp);
    }

    // ---------------------------------------------------------------------

    private bool IsAdmin => current.Role == Domain.Common.Roles.Admin;

    private IQueryable<User> LoadQuery() => db.Users
        .Include(u => u.Branches)
        .Include(u => u.Clinics)
        .Include(u => u.ExtraPermissions)
        .Include(u => u.Doctor);

    /// <summary>
    /// المستخدم الذي يحق للمستخدم الحالي إدارته (المدير العام: الكل، غيره: موظفو فرعه الحالي من غير المدراء،
    /// وموظفو الأدوار المخصصة فقط إذا كانت صلاحيات دورهم ضمن صلاحياته).
    /// </summary>
    private async Task<User> LoadManageableAsync(Guid userId, CancellationToken ct)
    {
        var user = await LoadQuery().FirstOrDefaultAsync(u => u.UserId == userId, ct)
                   ?? throw AppException.NotFound("المستخدم غير موجود");
        if (IsAdmin) return user;

        var sameBranch = user.Branches.Any(b => b.BranchId == current.BranchId);
        var manageableRole = user.Role is not (Domain.Common.Roles.Admin or Domain.Common.Roles.BranchManager)
                             && CanHandleRole(user.Role, await access.RolesAsync(db, ct));
        if (!sameBranch || (!manageableRole && user.UserId != current.UserId))
            throw AppException.NotFound("المستخدم غير موجود");
        return user;
    }

    /// <summary>الأدوار النظامية كما كانت؛ الدور المخصص لا يُسنده غير المدير العام إلا إذا كانت كل صلاحياته ضمن صلاحياته (منع التصعيد).</summary>
    private bool CanHandleRole(string role, RoleSnapshot roles) =>
        Domain.Common.Roles.IsSystem(role) || roles.RolePermissions(role).All(current.Has);

    private void EnsureCanAssignRole(string role, RoleSnapshot roles)
    {
        if (IsAdmin) return;
        if (role is Domain.Common.Roles.Admin or Domain.Common.Roles.BranchManager)
            throw AppException.Forbidden("مدير الفرع لا يستطيع إنشاء مدير أو مدير فرع");
        if (!CanHandleRole(role, roles))
            throw AppException.Forbidden("لا يمكنك إسناد دور يملك صلاحيات لا تملكها");
    }

    private async Task ValidateAssignmentsAsync(
        string role, List<Guid> branchIds, List<Guid> clinicIds, List<string> extras,
        DoctorProfileInput? doctorProfile, bool requireDoctorProfile, CancellationToken ct)
    {
        var v = new Validator();

        if (!IsAdmin)
            v.Check(branchIds.All(b => b == current.BranchId), "branch_ids", "مدير الفرع يعيّن الموظفين على فرعه فقط");

        var foundBranches = await db.Branches.CountAsync(b => branchIds.Contains(b.BranchId) && b.IsActive, ct);
        v.Check(foundBranches == branchIds.Count, "branch_ids", "فرع غير موجود أو معطل");

        if (clinicIds.Count > 0)
        {
            var clinicsOk = await db.Clinics.CountAsync(c => clinicIds.Contains(c.ClinicId) && branchIds.Contains(c.BranchId), ct);
            v.Check(clinicsOk == clinicIds.Count, "clinic_ids", "العيادة غير موجودة أو ليست ضمن فروع الموظف");
        }
        v.Check(role != Domain.Common.Roles.Receptionist || clinicIds.Count > 0, "clinic_ids", "موظف الاستقبال يجب ربطه بعيادة واحدة على الأقل");

        v.Check(extras.All(Permissions.IsKnown), "extra_permissions", "صلاحية غير معروفة (GET /permissions/catalog)");
        v.Check(!extras.Any(Permissions.AdminOnly.Contains), "extra_permissions", "صلاحيات إدارة الأدوار وإنشاء الفروع وإيقافها للمدير العام فقط");
        if (!IsAdmin)
            v.Check(extras.All(p => current.Has(p)), "extra_permissions", "لا يمكنك منح صلاحية لا تملكها");

        if (role == Domain.Common.Roles.Doctor && (requireDoctorProfile || doctorProfile is not null))
        {
            var dp = doctorProfile;
            v.Check(dp is not null, "doctor_profile", "بيانات الطبيب مطلوبة لدور الطبيب");
            if (dp is not null)
            {
                v.Check(!requireDoctorProfile || !string.IsNullOrWhiteSpace(dp.Specialty), "doctor_profile.specialty", "التخصص مطلوب");
                v.Check(!requireDoctorProfile || dp.ClinicId is not null, "doctor_profile.clinic_id", "العيادة مطلوبة");
                if (!string.IsNullOrWhiteSpace(dp.Specialty))
                    v.Check(await lookups.ResolveActiveAsync(LookupCategories.DoctorSpecialty, dp.Specialty, ct) is not null,
                        "doctor_profile.specialty", "اختر الاختصاص من القائمة (GET /lookups/doctor_specialty)");
                if (!string.IsNullOrEmpty(dp.JobTitle))
                    v.Check(await lookups.ResolveActiveAsync(LookupCategories.DoctorJobTitle, dp.JobTitle, ct) is not null,
                        "doctor_profile.job_title", "اختر العنوان الوظيفي من القائمة (GET /lookups/doctor_job_title)");
                v.Check(dp.DefaultSlotMinutes is null or > 0 and <= 240, "doctor_profile.default_slot_minutes", "مدة المعاينة بين 1 و 240 دقيقة");
                if (dp.ClinicId is { } cid)
                {
                    var ok = await db.Clinics.AnyAsync(c => c.ClinicId == cid && c.Department == "clinic" && branchIds.Contains(c.BranchId), ct);
                    v.Check(ok, "doctor_profile.clinic_id", "العيادة غير موجودة أو ليست ضمن فروع الطبيب");
                }
            }
        }

        v.ThrowIfInvalid();
    }

    /// <summary>الاختصاص والعنوان الوظيفي يُقبلان بالرمز أو بالاسم، ويُخزنان بالاسم المعتمد من القائمة.</summary>
    private async Task<DoctorProfileInput?> NormalizeProfileAsync(DoctorProfileInput? dp, CancellationToken ct)
    {
        if (dp is null) return null;
        var spec = await lookups.ResolveActiveAsync(LookupCategories.DoctorSpecialty, dp.Specialty, ct);
        var title = await lookups.ResolveActiveAsync(LookupCategories.DoctorJobTitle, dp.JobTitle, ct);
        return dp with
        {
            Specialty = spec?.NameAr ?? dp.Specialty?.Trim(),
            JobTitle = dp.JobTitle is null ? null : title?.NameAr ?? dp.JobTitle.Trim(),
        };
    }

    private static Doctor NewDoctor(User user, DoctorProfileInput dp, DateTimeOffset now) => new()
    {
        DoctorId = UuidV7.New(),
        UserId = user.UserId,
        ClinicId = dp.ClinicId!.Value,
        FullName = user.FullName,
        Specialty = dp.Specialty!.Trim(),
        JobTitle = string.IsNullOrEmpty(dp.JobTitle) ? null : dp.JobTitle,
        DefaultSlotMinutes = dp.DefaultSlotMinutes ?? 10,
        IsActive = true,
        CreatedAt = now,
    };

    private static UserDto ToDto(User u, RoleSnapshot roles) => new(
        u.UserId,
        u.Username,
        u.FullName,
        u.PhoneNumber,
        u.Role,
        u.IsActive,
        u.MustChangePassword,
        u.Branches.Select(b => b.BranchId).ToList(),
        u.Clinics.Select(c => c.ClinicId).ToList(),
        u.ExtraPermissions.Select(p => p.Permission).Order().ToList(),
        AuthService.EffectivePermissions(u, roles),
        u.Doctor is { } d ? new DoctorProfileDto(d.DoctorId, d.Specialty, d.JobTitle, d.ClinicId, d.DefaultSlotMinutes) : null,
        u.LastLoginAt,
        u.CreatedAt,
        roles.NameAr(u.Role));

    private static UserSnapshot Snapshot(User u) => new(
        u.FullName, u.PhoneNumber, u.Role,
        u.Branches.Select(b => b.BranchId).Order().ToList(),
        u.Clinics.Select(c => c.ClinicId).Order().ToList(),
        u.ExtraPermissions.Select(p => p.Permission).Order().ToList());

    private record UserSnapshot(string FullName, string? PhoneNumber, string Role, List<Guid> BranchIds, List<Guid> ClinicIds, List<string> ExtraPermissions);

    private static string GenerateTemporaryPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        Span<char> chars = stackalloc char[12];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return $"Tmp-{new string(chars)}";
    }
}
