using System.Text.RegularExpressions;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Security;

public record PermissionActionDto(string Code, string NameAr);
public record PermissionItemDto(string Code, string Action, string NameAr, string? Description, IReadOnlyList<string> Implies, bool AdminOnly, bool Legacy);
public record PermissionSectionDto(string Code, string NameAr, IReadOnlyList<PermissionItemDto> Permissions);
public record PermissionCatalogDto(IReadOnlyList<PermissionActionDto> Actions, IReadOnlyList<PermissionSectionDto> Sections,
    IReadOnlyList<string> NavSections);

/// <summary>
/// role = code (للتوافق مع شكل GET /roles القديم). permissions = المخزن للدور، effective_permissions = بعد توسيع الرموز الشاملة.
/// is_locked: صلاحيات المدير العام ثابتة (كلها). assignable: هل يستطيع المستخدم الحالي إسناد الدور لموظف.
/// </summary>
public record RoleDto(string Code, string Role, string NameAr, string? Description, bool IsSystem, bool IsActive, bool IsLocked,
    string? HomeSection, IReadOnlyList<string> Sections, IReadOnlyList<string> Permissions, IReadOnlyList<string> EffectivePermissions,
    int UserCount, int ActiveUserCount, bool Assignable, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record CreateRoleRequest(string? Code, string? NameAr, string? Description, string? HomeSection, List<string>? Permissions);
/// <summary>PATCH: الحقول غير المرسلة لا تتغير. permissions تستبدل القائمة كاملة. home_section = "" يمسحها.</summary>
public record UpdateRoleRequest(string? NameAr, string? Description, string? HomeSection, List<string>? Permissions, bool? IsActive);

/// <summary>
/// الأدوار والصلاحيات من شاشة الإدارة. الإدارة (إضافة، تعديل، حذف) للمدير العام فقط، والعرض لمن يملك roles.view أو users.view.
/// الأدوار الثمانية (is_system): يمكن تعديل اسمها ووصفها وصلاحياتها، عدا صلاحيات المدير العام (ثابتة: كل الصلاحيات)،
/// ولا تُحذف ولا تُوقف ولا يتغير رمزها.
/// </summary>
public partial class RoleService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, AccessCache access)
{
    [GeneratedRegex("^[a-z][a-z0-9_]{2,29}$")]
    private static partial Regex CodePattern();

    public static PermissionCatalogDto Catalog() => new(
        Permissions.Actions.Select(a => new PermissionActionDto(a.Code, a.NameAr)).ToList(),
        Permissions.Catalog.Select(s => new PermissionSectionDto(s.Code, s.NameAr, s.Permissions.Select(p => new PermissionItemDto(
            p.Code, p.Action, p.NameAr, p.Description,
            Permissions.Implies.TryGetValue(p.Code, out var implied) ? implied : [],
            Permissions.AdminOnly.Contains(p.Code), p.Legacy)).ToList())).ToList(),
        NavSections.All);

    public async Task<IReadOnlyList<RoleDto>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var roles = await access.RolesAsync(db, ct);
        var counts = await CountsAsync(null, ct);
        return roles.Roles.Values
            .Where(r => r.IsActive || includeInactive)
            .OrderBy(r => r.IsSystem ? 0 : 1).ThenBy(r => SystemOrder(r.Code)).ThenBy(r => r.NameAr)
            .Select(r => ToDto(r, roles, counts))
            .ToList();
    }

    public async Task<RoleDto> GetAsync(string code, CancellationToken ct)
    {
        var roles = await access.RolesAsync(db, ct);
        var role = roles.Find(code) ?? throw NotFound();
        return ToDto(role, roles, await CountsAsync(code, ct));
    }

    public async Task<RoleDto> CreateAsync(CreateRoleRequest req, CancellationToken ct)
    {
        EnsureAdmin();
        var code = req.Code?.Trim().ToLowerInvariant();
        var name = req.NameAr?.Trim();
        var perms = Normalize(req.Permissions);
        new Validator()
            .Check(code is not null && CodePattern().IsMatch(code) && code != Roles.Patient, "code",
                "رمز الدور: حرف إنجليزي صغير ثم حروف أو أرقام أو _ (3 إلى 30)، مثل senior_reception")
            .Check(!string.IsNullOrWhiteSpace(name) && name.Length <= 100, "name_ar", "اسم الدور مطلوب (100 حرف كحد أقصى)")
            .Check(req.Description is null || req.Description.Length <= 500, "description", "500 حرف كحد أقصى")
            .Check(HomeOk(req.HomeSection), "home_section", string.Join(" | ", NavSections.All))
            .Check(perms.All(Permissions.IsKnown), "permissions", "صلاحية غير معروفة (GET /permissions/catalog)")
            .Check(!perms.Any(Permissions.AdminOnly.Contains), "permissions", "صلاحيات إدارة الأدوار وإنشاء الفروع وإيقافها للمدير العام فقط")
            .ThrowIfInvalid();
        if (await db.Roles.AnyAsync(r => r.Code == code, ct))
            throw new AppException(409, "DUPLICATE_ROLE", "رمز الدور مستخدم");
        if (await db.Roles.AnyAsync(r => r.NameAr == name, ct))
            throw new AppException(409, "DUPLICATE_ROLE", "يوجد دور بنفس الاسم");

        var now = clock.UtcNow;
        var role = new Role
        {
            Code = code!,
            NameAr = name!,
            Description = Clean(req.Description),
            HomeSection = Clean(req.HomeSection),
            IsSystem = false,
            IsActive = true,
            CreatedAt = now,
            CreatedBy = current.StaffUserId,
            UpdatedAt = now,
            UpdatedBy = current.StaffUserId,
            Permissions = perms.Select(p => new RolePermission { RoleCode = code!, Permission = p, GrantedBy = current.StaffUserId, GrantedAt = now }).ToList(),
        };
        db.Roles.Add(role);
        audit.Add("ROLE_CREATED", "role", null, after: new { code, name_ar = name, permissions = perms, home_section = role.HomeSection });
        await db.SaveChangesAsync(ct);
        access.InvalidateRoles();
        return await GetAsync(role.Code, ct);
    }

    public async Task<RoleDto> UpdateAsync(string code, UpdateRoleRequest req, CancellationToken ct)
    {
        EnsureAdmin();
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Code == code, ct) ?? throw NotFound();
        var perms = req.Permissions is null ? null : Normalize(req.Permissions);
        var name = req.NameAr?.Trim();
        new Validator()
            .Check(req.NameAr is null || (name!.Length is > 0 and <= 100), "name_ar", "اسم الدور (100 حرف كحد أقصى)")
            .Check(req.Description is null || req.Description.Length <= 500, "description", "500 حرف كحد أقصى")
            .Check(string.IsNullOrEmpty(req.HomeSection) || HomeOk(req.HomeSection), "home_section", string.Join(" | ", NavSections.All))
            .Check(perms is null || perms.All(Permissions.IsKnown), "permissions", "صلاحية غير معروفة (GET /permissions/catalog)")
            .Check(perms is null || role.Code == Roles.Admin || !perms.Any(Permissions.AdminOnly.Contains), "permissions",
                "صلاحيات إدارة الأدوار وإنشاء الفروع وإيقافها للمدير العام فقط")
            .ThrowIfInvalid();

        if (role.Code == Roles.Admin && perms is not null)
            throw new AppException(409, "ROLE_LOCKED", "صلاحيات المدير العام ثابتة", "المدير العام يملك كل الصلاحيات دائماً");
        if (req.IsActive == false && role.IsActive)
        {
            if (role.IsSystem) throw new AppException(409, "ROLE_IS_SYSTEM", "أدوار النظام لا تُوقف ولا تُحذف");
            var users = await db.Users.CountAsync(u => u.Role == code, ct);
            if (users > 0) throw InUse(users);
        }
        if (name is not null && name != role.NameAr && await db.Roles.AnyAsync(r => r.NameAr == name && r.Code != code, ct))
            throw new AppException(409, "DUPLICATE_ROLE", "يوجد دور بنفس الاسم");

        var before = new { name_ar = role.NameAr, role.Description, home_section = role.HomeSection, is_active = role.IsActive,
            permissions = role.Permissions.Select(p => p.Permission).Order(StringComparer.Ordinal).ToList() };
        var now = clock.UtcNow;
        if (name is not null) role.NameAr = name;
        if (req.Description is not null) role.Description = Clean(req.Description);
        if (req.HomeSection is not null) role.HomeSection = Clean(req.HomeSection);
        if (req.IsActive is { } active) role.IsActive = active;
        if (perms is not null)
        {
            role.Permissions.RemoveAll(p => !perms.Contains(p.Permission));
            foreach (var p in perms.Where(p => role.Permissions.All(x => x.Permission != p)))
                role.Permissions.Add(new RolePermission { RoleCode = role.Code, Permission = p, GrantedBy = current.StaffUserId, GrantedAt = now });
        }
        role.UpdatedAt = now;
        role.UpdatedBy = current.StaffUserId;
        var after = new { name_ar = role.NameAr, role.Description, home_section = role.HomeSection, is_active = role.IsActive,
            permissions = role.Permissions.Select(p => p.Permission).Order(StringComparer.Ordinal).ToList() };
        audit.Add(perms is not null && !before.permissions.SequenceEqual(after.permissions) ? "ROLE_PERMISSIONS_CHANGED" : "ROLE_UPDATED",
            "role", null, new { code, before }, new { code, after });
        await db.SaveChangesAsync(ct);
        access.InvalidateRoles();
        return await GetAsync(code, ct);
    }

    public async Task DeleteAsync(string code, CancellationToken ct)
    {
        EnsureAdmin();
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Code == code, ct) ?? throw NotFound();
        if (role.IsSystem) throw new AppException(409, "ROLE_IS_SYSTEM", "أدوار النظام لا تُوقف ولا تُحذف");
        var users = await db.Users.CountAsync(u => u.Role == code, ct);
        if (users > 0) throw InUse(users);
        audit.Add("ROLE_DELETED", "role", null, before: new
        {
            code, name_ar = role.NameAr, permissions = role.Permissions.Select(p => p.Permission).Order(StringComparer.Ordinal).ToList(),
        });
        db.RolePermissions.RemoveRange(role.Permissions);
        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct);
        access.InvalidateRoles();
    }

    // ------------------------------------------------------------------

    private void EnsureAdmin()
    {
        if (current.Role != Roles.Admin) throw AppException.Forbidden("إدارة الأدوار للمدير العام فقط");
    }

    private static AppException NotFound() => new(404, "ROLE_NOT_FOUND", "الدور غير موجود");

    private static AppException InUse(int users) =>
        new(409, "ROLE_IN_USE", "الدور مسند لموظفين", $"انقل الموظفين ({users}) لدور آخر أولاً")
        {
            Extensions = new Dictionary<string, object?> { ["users_count"] = users },
        };

    private static bool HomeOk(string? home) => home is null || NavSections.All.Contains(home.Trim());

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static List<string> Normalize(List<string>? perms) =>
        (perms ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct().Order(StringComparer.Ordinal).ToList();

    private static int SystemOrder(string code)
    {
        var i = ((IList<string>)Roles.All).IndexOf(code);
        return i < 0 ? int.MaxValue : i;
    }

    private async Task<Dictionary<string, (int Total, int Active)>> CountsAsync(string? only, CancellationToken ct)
    {
        var q = db.Users.AsNoTracking();
        if (only is not null) q = q.Where(u => u.Role == only);
        // مدير الفرع يرى أعداد موظفي فرعه الحالي فقط، والمدير العام كل الفروع
        if (current.Role != Roles.Admin) q = q.Where(u => u.Branches.Any(b => b.BranchId == current.BranchId));
        return (await q.GroupBy(u => u.Role).Select(g => new { g.Key, Total = g.Count(), Active = g.Count(u => u.IsActive) }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => (x.Total, x.Active));
    }

    private RoleDto ToDto(RoleInfo r, RoleSnapshot roles, Dictionary<string, (int Total, int Active)> counts)
    {
        var effective = roles.RolePermissions(r.Code);
        var stored = r.IsAdmin ? effective : r.Stored;
        var (home, sections) = NavSections.Resolve(r.Code, r.HomeSection, effective);
        var (total, active) = counts.GetValueOrDefault(r.Code);
        var assignable = r.IsActive && (current.Role == Roles.Admin
                         || (r.Code is not (Roles.Admin or Roles.BranchManager) && (r.IsSystem || effective.All(current.Has))));
        return new RoleDto(r.Code, r.Code, r.NameAr, r.Description, r.IsSystem, r.IsActive, r.IsAdmin, home, sections, stored, effective,
            total, active, assignable, r.CreatedAt, r.UpdatedAt);
    }
}
