using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Security;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Auth;

public class AuthService(
    IAppDbContext db,
    IPasswordHasher hasher,
    ITokenService tokens,
    ICurrentUser current,
    IAuditLogger audit,
    IClock clock,
    AccessCache access)
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    public const int MinPasswordLength = 10;

    public async Task<AuthResponse> LoginAsync(LoginRequest req, CancellationToken ct)
    {
        new Validator()
            .Check(!string.IsNullOrWhiteSpace(req.Username), "username", "اسم المستخدم مطلوب")
            .Check(!string.IsNullOrEmpty(req.Password), "password", "كلمة المرور مطلوبة")
            .Check(req.BranchId is not null, "branch_id", "الفرع مطلوب")
            .ThrowIfInvalid();

        var username = req.Username!.Trim().ToLowerInvariant();
        var user = await LoadUserQuery().FirstOrDefaultAsync(u => u.Username == username, ct);
        var now = clock.UtcNow;

        if (user is null)
        {
            // نفس كلفة التحقق لتقليل كشف وجود الحساب عبر التوقيت
            hasher.Verify(req.Password!, DummyHash.Value);
            throw InvalidCredentials();
        }

        if (user.LockedUntil is { } until && until > now)
            throw new AppException(423, "ACCOUNT_LOCKED", "الحساب مقفل مؤقتاً",
                $"أعد المحاولة بعد {Math.Ceiling((until - now).TotalMinutes)} دقيقة");

        if (!hasher.Verify(req.Password!, user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedAttempts)
            {
                user.LockedUntil = now + LockDuration;
                user.FailedLoginCount = 0;
                audit.Add("ACCOUNT_LOCKED", "user", user.UserId);
            }
            await db.SaveChangesAsync(ct);
            throw InvalidCredentials();
        }

        if (!user.IsActive)
            throw new AppException(403, "USER_DISABLED", "الحساب معطل");

        var branchId = req.BranchId!.Value;
        await EnsureBranchAllowedAsync(user, branchId, ct);

        Guid? deviceId = null;
        if (!string.IsNullOrWhiteSpace(req.DeviceId))
            deviceId = await db.Devices.Where(d => d.DeviceCode == req.DeviceId && d.IsActive)
                .Select(d => (Guid?)d.DeviceId).FirstOrDefaultAsync(ct);

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;

        var response = await IssueSessionAsync(user, branchId, deviceId, ct);
        audit.Add("LOGIN", "user", user.UserId, after: new { branch_id = branchId, device = req.DeviceId });
        await db.SaveChangesAsync(ct);
        return response;
    }

    public async Task<AuthResponse> RefreshAsync(RefreshRequest req, CancellationToken ct)
    {
        new Validator().Check(!string.IsNullOrEmpty(req.RefreshToken), "refresh_token", "مطلوب").ThrowIfInvalid();

        var hash = tokens.HashRefreshToken(req.RefreshToken!);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = clock.UtcNow;

        if (stored is null || stored.UserId is null)
            throw InvalidRefresh();

        if (stored.RevokedAt is not null)
        {
            // إعادة استخدام توكن مستبدل = احتمال سرقة: إلغاء كل جلسات المستخدم
            if (stored.ReplacedBy is not null)
            {
                await RevokeAllAsync(stored.UserId.Value, exceptTokenId: null, ct);
                audit.Add("REFRESH_TOKEN_REUSE", "user", stored.UserId);
                await db.SaveChangesAsync(ct);
            }
            throw InvalidRefresh();
        }

        if (stored.ExpiresAt <= now)
            throw InvalidRefresh();

        var user = await LoadUserQuery().FirstAsync(u => u.UserId == stored.UserId, ct);
        if (!user.IsActive)
            throw new AppException(403, "USER_DISABLED", "الحساب معطل");

        var branchId = stored.BranchId ?? user.Branches.Select(b => b.BranchId).FirstOrDefault();
        await EnsureBranchAllowedAsync(user, branchId, ct);

        var (response, newTokenId) = await IssueSessionWithIdAsync(user, branchId, stored.DeviceId, ct);
        stored.RevokedAt = now;
        stored.ReplacedBy = newTokenId;
        await db.SaveChangesAsync(ct);
        return response;
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        if (current.SessionId is not { } sid) return;
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenId == sid && t.UserId == current.UserId, ct);
        if (token is not null && token.RevokedAt is null)
        {
            token.RevokedAt = clock.UtcNow;
            audit.Add("LOGOUT", "user", current.UserId);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<AuthResponse> SwitchBranchAsync(SwitchBranchRequest req, CancellationToken ct)
    {
        new Validator().Check(req.BranchId is not null, "branch_id", "الفرع مطلوب").ThrowIfInvalid();

        var hasOpenShift = await db.ExistsAsync(
            $"SELECT EXISTS (SELECT 1 FROM shifts WHERE user_id = {current.UserId} AND status = 'open') AS \"Value\"", ct);
        if (hasOpenShift)
            throw new AppException(409, "SHIFT_OPEN", "لا يمكن تبديل الفرع مع وجود وردية مفتوحة");

        var user = await LoadUserQuery().FirstAsync(u => u.UserId == current.UserId, ct);
        await EnsureBranchAllowedAsync(user, req.BranchId!.Value, ct);

        Guid? deviceId = null;
        if (current.SessionId is { } sid)
        {
            var old = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenId == sid, ct);
            if (old is not null)
            {
                deviceId = old.DeviceId;
                old.RevokedAt ??= clock.UtcNow;
            }
        }

        var response = await IssueSessionAsync(user, req.BranchId.Value, deviceId, ct);
        audit.Add("BRANCH_SWITCHED", "user", user.UserId,
            before: new { branch_id = current.BranchId }, after: new { branch_id = req.BranchId });
        await db.SaveChangesAsync(ct);
        return response;
    }

    public async Task<AuthUserDto> MeAsync(CancellationToken ct)
    {
        var user = await LoadUserQuery().FirstOrDefaultAsync(u => u.UserId == current.UserId, ct)
                   ?? throw new AppException(401, "TOKEN_EXPIRED", "الجلسة غير صالحة");
        return ToDto(user, current.BranchId, await access.RolesAsync(db, ct));
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest req, CancellationToken ct)
    {
        new Validator()
            .Check(!string.IsNullOrEmpty(req.CurrentPassword), "current_password", "مطلوبة")
            .Check(!string.IsNullOrEmpty(req.NewPassword), "new_password", "مطلوبة")
            .ThrowIfInvalid();

        var user = await db.Users.FirstAsync(u => u.UserId == current.UserId, ct);
        if (!hasher.Verify(req.CurrentPassword!, user.PasswordHash))
            throw new AppException(401, "INVALID_CREDENTIALS", "كلمة المرور الحالية غير صحيحة");

        EnsureStrongPassword(req.NewPassword!);
        if (req.NewPassword == req.CurrentPassword)
            throw new AppException(422, "WEAK_PASSWORD", "كلمة المرور الجديدة يجب أن تختلف عن الحالية");

        user.PasswordHash = hasher.Hash(req.NewPassword!);
        user.MustChangePassword = false;
        user.UpdatedAt = clock.UtcNow;
        await RevokeAllAsync(user.UserId, exceptTokenId: current.SessionId, ct);
        audit.Add("PASSWORD_CHANGED", "user", user.UserId);
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------------

    public static void EnsureStrongPassword(string password)
    {
        if (password.Length < MinPasswordLength)
            throw new AppException(422, "WEAK_PASSWORD", "كلمة المرور ضعيفة",
                $"يجب ألا تقل كلمة المرور عن {MinPasswordLength} أحرف");
    }

    /// <summary>صلاحيات الدور (من جدول role_permissions) + الإضافية للموظف، مع ما تتضمنه الرموز الشاملة.</summary>
    public static IReadOnlyList<string> EffectivePermissions(User user, RoleSnapshot roles) =>
        roles.Effective(user.Role, user.ExtraPermissions.Select(p => p.Permission));

    public static AuthUserDto ToDto(User user, Guid branchId, RoleSnapshot roles)
    {
        var perms = EffectivePermissions(user, roles);
        var role = roles.Find(user.Role);
        var (home, sections) = NavSections.Resolve(user.Role, role?.HomeSection, perms);
        return new AuthUserDto(
            user.UserId,
            user.FullName,
            user.Role,
            branchId,
            user.Branches.Select(b => b.BranchId).ToList(),
            user.Clinics.Select(c => c.ClinicId).ToList(),
            user.Doctor?.DoctorId,
            perms,
            user.MustChangePassword,
            roles.NameAr(user.Role),
            Roles.IsSystem(user.Role),
            home,
            sections);
    }

    private IQueryable<User> LoadUserQuery() => db.Users
        .Include(u => u.Branches)
        .Include(u => u.Clinics)
        .Include(u => u.ExtraPermissions)
        .Include(u => u.Doctor);

    private async Task EnsureBranchAllowedAsync(User user, Guid branchId, CancellationToken ct)
    {
        var branchActive = await db.Branches.AnyAsync(b => b.BranchId == branchId && b.IsActive, ct);
        var assigned = user.Role == Roles.Admin || user.Branches.Any(b => b.BranchId == branchId);
        if (!branchActive || !assigned)
            throw new AppException(403, "BRANCH_NOT_ASSIGNED", "الموظف غير معين على هذا الفرع");
    }

    private async Task<AuthResponse> IssueSessionAsync(User user, Guid branchId, Guid? deviceId, CancellationToken ct) =>
        (await IssueSessionWithIdAsync(user, branchId, deviceId, ct)).Response;

    private async Task<(AuthResponse Response, Guid TokenId)> IssueSessionWithIdAsync(User user, Guid branchId, Guid? deviceId, CancellationToken ct)
    {
        var roles = await access.RolesAsync(db, ct);
        var now = clock.UtcNow;
        var refreshValue = tokens.CreateRefreshTokenValue();
        var tokenId = UuidV7.New();
        db.RefreshTokens.Add(new RefreshToken
        {
            TokenId = tokenId,
            UserId = user.UserId,
            BranchId = branchId,
            DeviceId = deviceId,
            TokenHash = tokens.HashRefreshToken(refreshValue),
            ExpiresAt = now + tokens.RefreshLifetime,
            CreatedAt = now,
        });

        var dto = ToDto(user, branchId, roles);
        var (accessToken, expiresIn) = tokens.CreateAccessToken(user, branchId, dto.Permissions.ToList(), tokenId);
        return (new AuthResponse(accessToken, expiresIn, refreshValue, (int)tokens.RefreshLifetime.TotalSeconds, dto), tokenId);
    }

    private async Task RevokeAllAsync(Guid userId, Guid? exceptTokenId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var active = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.TokenId != exceptTokenId)
            .ToListAsync(ct);
        foreach (var t in active) t.RevokedAt = now;
    }

    private static AppException InvalidCredentials() =>
        new(401, "INVALID_CREDENTIALS", "اسم المستخدم أو كلمة المرور غير صحيحة");

    private static AppException InvalidRefresh() =>
        new(401, "TOKEN_EXPIRED", "انتهت الجلسة، سجّل الدخول من جديد");

    /// <summary>hash ثابت يُستخدم عند عدم وجود المستخدم للحفاظ على زمن استجابة متقارب.</summary>
    public static class DummyHash
    {
        public static string Value { get; set; } = "";
    }
}
