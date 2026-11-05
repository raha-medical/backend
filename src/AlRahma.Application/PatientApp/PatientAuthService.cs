using System.Security.Cryptography;
using System.Text;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Notifications;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlRahma.Application.PatientApp;

public record OtpRequest(string? PhoneNumber);
public record OtpRequestResponse(int OtpExpiresIn, int ResendAfter, string? SandboxCode);
public record OtpVerifyRequest(string? PhoneNumber, string? Code);
public record PatientRefreshRequest(string? RefreshToken);
public record PatientAccountDto(Guid PatientAccountId, string PhoneNumber);
public record PatientAuthResponse(string AccessToken, int ExpiresIn, string RefreshToken, int RefreshExpiresIn, PatientAccountDto Account,
    IReadOnlyList<MyPatientDto> Patients);

/// <summary>
/// دخول تطبيق المريض برقم الهاتف ورمز OTP (بلا كلمة مرور). الحساب يُنشأ عند أول دخول، وتُربط به تلقائياً
/// كل ملفات المرضى بنفس الرقم. الوضع التجريبي: الرمز ثابت ويعود في الرد ولا تُرسل رسالة.
/// </summary>
public class PatientAuthService(IAppDbContext db, ITokenService tokens, ICurrentUser current, IAuditLogger audit, IClock clock,
    IOptions<OtpOptions> otpOptions, NotificationService notifications, PatientPortalService portal)
{
    public const int OtpLifetimeSeconds = 300;
    public const int ResendAfterSeconds = 60;
    public const int MaxRequestsPer10Minutes = 3;
    public const int MaxAttempts = 5;

    private OtpOptions Otp => otpOptions.Value;

    public async Task<OtpRequestResponse> RequestOtpAsync(OtpRequest req, CancellationToken ct)
    {
        var phone = PhoneNumbers.Normalize(req.PhoneNumber);
        new Validator().Check(phone is not null, "phone_number", "رقم الهاتف غير صالح").ThrowIfInvalid();
        var now = clock.UtcNow;
        var recent = await db.OtpCodes.AsNoTracking().Where(o => o.PhoneNumber == phone && o.CreatedAt > now.AddMinutes(-10))
            .OrderBy(o => o.CreatedAt).Select(o => o.CreatedAt).ToListAsync(ct);
        if (recent.Count >= MaxRequestsPer10Minutes)
        {
            var retry = (int)Math.Ceiling((recent[0].AddMinutes(10) - now).TotalSeconds);
            throw new AppException(429, "RATE_LIMITED", "طلبات كثيرة لنفس الرقم", "أعد المحاولة لاحقاً")
                { Extensions = new Dictionary<string, object?> { ["retry_after"] = Math.Max(1, retry) } };
        }
        // رمز عشوائي لكل طلب دائماً (حتى في الوضع التجريبي)؛ في sandbox يُسجَّل في «الرسائل التجريبية» بدل الإرسال الفعلي
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        // الرمز الجديد يُبطل ما قبله
        foreach (var old in await db.OtpCodes.Where(o => o.PhoneNumber == phone && o.ConsumedAt == null).ToListAsync(ct))
            old.ConsumedAt = now;
        db.OtpCodes.Add(new OtpCode
        {
            OtpId = UuidV7.New(), PhoneNumber = phone!, CodeHash = Hash(phone!, code), ExpiresAt = now.AddSeconds(OtpLifetimeSeconds), CreatedAt = now,
        });
        notifications.QueueSms(phone!, null, $"رمز الدخول إلى تطبيق مجمع الرحمة: {code}");
        await db.SaveChangesAsync(ct);
        return new OtpRequestResponse(OtpLifetimeSeconds, ResendAfterSeconds, Otp.IsSandbox && Otp.ExposeCodeInResponse ? code : null);
    }

    public async Task<PatientAuthResponse> VerifyAsync(OtpVerifyRequest req, CancellationToken ct)
    {
        var phone = PhoneNumbers.Normalize(req.PhoneNumber);
        new Validator()
            .Check(phone is not null, "phone_number", "رقم الهاتف غير صالح")
            .Check(!string.IsNullOrWhiteSpace(req.Code), "code", "الرمز مطلوب")
            .ThrowIfInvalid();
        var now = clock.UtcNow;
        var otp = await db.OtpCodes.Where(o => o.PhoneNumber == phone && o.ConsumedAt == null).OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);
        if (otp is null || otp.ExpiresAt <= now)
            throw new AppException(401, "OTP_EXPIRED", "انتهت صلاحية الرمز", "اطلب رمزاً جديداً");
        if (otp.Attempts >= MaxAttempts)
            throw new AppException(429, "OTP_TOO_MANY_ATTEMPTS", "محاولات خاطئة كثيرة", "اطلب رمزاً جديداً");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(otp.CodeHash), Encoding.UTF8.GetBytes(Hash(phone!, req.Code!.Trim()))))
        {
            otp.Attempts++;
            if (otp.Attempts >= MaxAttempts) otp.ConsumedAt = now;
            await db.SaveChangesAsync(ct);
            if (otp.Attempts >= MaxAttempts)
                throw new AppException(429, "OTP_TOO_MANY_ATTEMPTS", "محاولات خاطئة كثيرة", "اطلب رمزاً جديداً");
            throw new AppException(401, "INVALID_OTP", "الرمز غير صحيح")
                { Extensions = new Dictionary<string, object?> { ["attempts_left"] = MaxAttempts - otp.Attempts } };
        }
        otp.ConsumedAt = now;

        var account = await db.PatientAccounts.FirstOrDefaultAsync(a => a.PhoneNumber == phone, ct);
        if (account is null)
        {
            account = new PatientAccount { PatientAccountId = UuidV7.New(), PhoneNumber = phone!, CreatedAt = now };
            db.PatientAccounts.Add(account);
        }
        account.LastLoginAt = now;
        // ربط تلقائي لملفات المرضى بنفس الرقم (غير المدموجة) التي لم تُربط بعد
        var linked = await db.PatientAccountLinks.Where(l => l.PatientAccountId == account.PatientAccountId).Select(l => l.PatientId).ToListAsync(ct);
        var toLink = await db.Patients.Where(p => p.PhoneNumber == phone && p.MergedIntoPatientId == null && !linked.Contains(p.PatientId))
            .Select(p => p.PatientId).ToListAsync(ct);
        foreach (var pid in toLink)
            db.PatientAccountLinks.Add(new PatientAccountLink { PatientAccountId = account.PatientAccountId, PatientId = pid, Relation = PatientRelations.Self });
        var response = Issue(account, out _);
        await db.SaveChangesAsync(ct);
        return response with { Patients = await portal.MyPatientsAsync(account.PatientAccountId, ct) };
    }

    public async Task<PatientAuthResponse> RefreshAsync(PatientRefreshRequest req, CancellationToken ct)
    {
        new Validator().Check(!string.IsNullOrEmpty(req.RefreshToken), "refresh_token", "مطلوب").ThrowIfInvalid();
        var hash = tokens.HashRefreshToken(req.RefreshToken!);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = clock.UtcNow;
        if (stored?.PatientAccountId is not { } accountId) throw Invalid();
        if (stored.RevokedAt is not null)
        {
            if (stored.ReplacedBy is not null)
            {
                // إعادة استخدام توكن مستبدل: إبطال كل جلسات الحساب
                foreach (var t in await db.RefreshTokens.Where(t => t.PatientAccountId == accountId && t.RevokedAt == null).ToListAsync(ct))
                    t.RevokedAt = now;
                audit.Add("PATIENT_REFRESH_TOKEN_REUSE", "patient_account", accountId);
                await db.SaveChangesAsync(ct);
            }
            throw Invalid();
        }
        if (stored.ExpiresAt <= now) throw Invalid();
        var account = await db.PatientAccounts.FirstAsync(a => a.PatientAccountId == accountId, ct);
        var response = Issue(account, out var newId);
        stored.RevokedAt = now;
        stored.ReplacedBy = newId;
        await db.SaveChangesAsync(ct);
        return response with { Patients = await portal.MyPatientsAsync(account.PatientAccountId, ct) };
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        if (current.SessionId is not { } sid || current.PatientAccountId is not { } accountId) return;
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenId == sid && t.PatientAccountId == accountId, ct);
        if (token is null || token.RevokedAt is not null) return;
        token.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private PatientAuthResponse Issue(PatientAccount account, out Guid tokenId)
    {
        var now = clock.UtcNow;
        var refresh = tokens.CreateRefreshTokenValue();
        tokenId = UuidV7.New();
        db.RefreshTokens.Add(new RefreshToken
        {
            TokenId = tokenId, PatientAccountId = account.PatientAccountId, TokenHash = tokens.HashRefreshToken(refresh),
            ExpiresAt = now + tokens.PatientRefreshLifetime, CreatedAt = now,
        });
        var (access, expiresIn) = tokens.CreatePatientAccessToken(account.PatientAccountId, account.PhoneNumber, tokenId);
        return new PatientAuthResponse(access, expiresIn, refresh, (int)tokens.PatientRefreshLifetime.TotalSeconds,
            new PatientAccountDto(account.PatientAccountId, account.PhoneNumber), []);
    }

    private string Hash(string phone, string code) => tokens.HashRefreshToken($"otp:{phone}:{code}");

    private static AppException Invalid() => new(401, "UNAUTHORIZED", "انتهت الجلسة، سجّل الدخول من جديد");
}
