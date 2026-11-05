using System.Collections.Concurrent;
using AlRahma.Application.Abstractions;
using AlRahma.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Security;

/// <summary>دور كما هو في جدول roles. Stored = الصلاحيات المخزنة في role_permissions (قبل توسيع الرموز الشاملة).</summary>
public record RoleInfo(string Code, string NameAr, string? Description, string? HomeSection, bool IsSystem, bool IsActive,
    IReadOnlyList<string> Stored, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public bool IsAdmin => Code == Roles.Admin;
}

/// <summary>لقطة من جدول الأدوار (تُقرأ مرة وتُحفظ في الذاكرة حتى يتغير أي دور).</summary>
public sealed class RoleSnapshot(IReadOnlyDictionary<string, RoleInfo> roles)
{
    public IReadOnlyDictionary<string, RoleInfo> Roles => roles;

    public RoleInfo? Find(string? code) => code is not null && roles.TryGetValue(code, out var r) ? r : null;

    /// <summary>صلاحيات الدور الفعلية: المدير العام كل الصلاحيات دائماً، وغيره المخزن مع ما تتضمنه الرموز الشاملة.</summary>
    public IReadOnlyList<string> RolePermissions(string role)
    {
        if (role == Domain.Common.Roles.Admin) return Permissions.All.Order(StringComparer.Ordinal).ToList();
        if (Find(role) is { } r) return Permissions.Expand(r.Stored);
        // قاعدة لم يُطبق عليها الترحيل 009 بعد: البذرة الأصلية للأدوار الثمانية
        return Permissions.ByRole.TryGetValue(role, out var seed) ? Permissions.Expand(seed) : [];
    }

    /// <summary>صلاحيات المستخدم = صلاحيات دوره + صلاحياته الإضافية، موسعة ومرتبة.</summary>
    public IReadOnlyList<string> Effective(string role, IEnumerable<string> extras) =>
        role == Domain.Common.Roles.Admin
            ? RolePermissions(role)
            : Permissions.Expand(RolePermissions(role).Concat(extras));

    public string NameAr(string role) => Find(role)?.NameAr ?? Permissions.RoleNamesAr.GetValueOrDefault(role, role);
}

/// <summary>ما يلزم للتحقق من توكن الموظف في كل طلب.</summary>
public record UserAccess(string Role, bool IsActive, IReadOnlySet<Guid> BranchIds, IReadOnlyList<string> Extras);

/// <summary>
/// ذاكرة مؤقتة (singleton) للأدوار وصلاحيات المستخدمين. تُمسح فوراً عند أي تعديل من نفس السيرفر،
/// ولها مدة صلاحية قصيرة (الأدوار 60 ثانية، المستخدم 30 ثانية) لتلتقط تعديلات نسخ السيرفر الأخرى.
/// </summary>
public sealed class AccessCache
{
    public static readonly TimeSpan RoleTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan UserTtl = TimeSpan.FromSeconds(30);

    private RoleSnapshot? _roles;
    private long _rolesLoadedAt;
    private long _rolesVersion;
    private readonly ConcurrentDictionary<Guid, (UserAccess Access, long LoadedAt, long Version)> _users = new();
    private long _usersVersion;

    private static long Now => Environment.TickCount64;

    public async Task<RoleSnapshot> RolesAsync(IAppDbContext db, CancellationToken ct = default)
    {
        var snap = _roles;
        if (snap is not null && Now - Interlocked.Read(ref _rolesLoadedAt) < RoleTtl.TotalMilliseconds) return snap;

        var version = Interlocked.Read(ref _rolesVersion);
        var rows = await db.Roles.AsNoTracking().Include(r => r.Permissions).ToListAsync(ct);
        snap = new RoleSnapshot(rows.ToDictionary(r => r.Code, r => new RoleInfo(r.Code, r.NameAr, r.Description, r.HomeSection, r.IsSystem, r.IsActive,
            r.Permissions.Select(p => p.Permission).Order(StringComparer.Ordinal).ToList(), r.CreatedAt, r.UpdatedAt)));
        // لا تُحفظ لقطة قُرئت قبل تعديل حدث أثناء القراءة
        if (Interlocked.Read(ref _rolesVersion) == version)
        {
            _roles = snap;
            Interlocked.Exchange(ref _rolesLoadedAt, Now);
        }
        return snap;
    }

    public void InvalidateRoles()
    {
        Interlocked.Increment(ref _rolesVersion);
        _roles = null;
    }

    public async Task<UserAccess?> UserAsync(IAppDbContext db, Guid userId, CancellationToken ct = default)
    {
        if (_users.TryGetValue(userId, out var hit) && Now - hit.LoadedAt < UserTtl.TotalMilliseconds
            && hit.Version == Interlocked.Read(ref _usersVersion))
            return hit.Access;

        var version = Interlocked.Read(ref _usersVersion);
        var user = await db.Users.AsNoTracking()
            .Where(u => u.UserId == userId)
            .Select(u => new
            {
                u.Role,
                u.IsActive,
                Branches = u.Branches.Select(b => b.BranchId).ToList(),
                Extras = u.ExtraPermissions.Select(p => p.Permission).ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (user is null)
        {
            _users.TryRemove(userId, out _);
            return null;
        }
        var access = new UserAccess(user.Role, user.IsActive, user.Branches.ToHashSet(), user.Extras);
        if (Interlocked.Read(ref _usersVersion) == version) _users[userId] = (access, Now, version);
        return access;
    }

    public void InvalidateUser(Guid userId) => _users.TryRemove(userId, out _);

    /// <summary>بعد تعديل جماعي (مثل تغيير صلاحيات دور يمس كثيراً من المستخدمين).</summary>
    public void InvalidateAllUsers()
    {
        Interlocked.Increment(ref _usersVersion);
        _users.Clear();
    }
}
