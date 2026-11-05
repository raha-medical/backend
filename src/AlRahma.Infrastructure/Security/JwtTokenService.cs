using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AlRahma.Application.Abstractions;
using AlRahma.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AlRahma.Infrastructure.Security;

public class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "alrahma-api";
    public string Audience { get; set; } = "alrahma-clients";
    /// <summary>مفتاح التوقيع (32 حرفاً على الأقل). يُمرر عبر متغير البيئة Jwt__SigningKey.</summary>
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    /// <summary>14 ساعة = طول الوردية، لتعمل الواجهة أوفلاين حتى نهاية الدوام.</summary>
    public int RefreshTokenHours { get; set; } = 14;
    /// <summary>جمهور توكنات تطبيق المريض (مختلف عن الموظفين، فلا يصل توكن المريض إلى endpoints الموظفين).</summary>
    public string PatientAudience { get; set; } = "alrahma-patients";
    public int PatientRefreshDays { get; set; } = 30;
}

public static class AppClaims
{
    public const string Role = "role";
    public const string BranchId = "branch_id";
    public const string SessionId = "sid";
    public const string Permission = "perm";
    /// <summary>موجود فقط إذا كان على المستخدم تغيير كلمة المرور قبل أي عملية أخرى.</summary>
    public const string MustChangePassword = "mcp";
}

public class JwtTokenService(IOptions<JwtOptions> options, IClock clock) : ITokenService
{
    private readonly JwtOptions _o = options.Value;

    public TimeSpan RefreshLifetime => TimeSpan.FromHours(_o.RefreshTokenHours);

    public (string Token, int ExpiresIn) CreateAccessToken(User user, Guid branchId, IReadOnlyCollection<string> permissions, Guid sessionId)
    {
        var now = clock.UtcNow;
        var expires = now.AddMinutes(_o.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserId.ToString()),
            new(JwtRegisteredClaimNames.Name, user.FullName),
            new(AppClaims.Role, user.Role),
            new(AppClaims.BranchId, branchId.ToString()),
            new(AppClaims.SessionId, sessionId.ToString()),
        };
        claims.AddRange(permissions.Select(p => new Claim(AppClaims.Permission, p)));
        if (user.MustChangePassword)
            claims.Add(new Claim(AppClaims.MustChangePassword, "true"));

        var token = new JwtSecurityToken(
            issuer: _o.Issuer,
            audience: _o.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(SigningKey(_o), SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), _o.AccessTokenMinutes * 60);
    }

    public TimeSpan PatientRefreshLifetime => TimeSpan.FromDays(_o.PatientRefreshDays);

    public (string Token, int ExpiresIn) CreatePatientAccessToken(Guid patientAccountId, string phoneNumber, Guid sessionId)
    {
        var now = clock.UtcNow;
        var token = new JwtSecurityToken(
            issuer: _o.Issuer,
            audience: _o.PatientAudience,
            claims:
            [
                new(JwtRegisteredClaimNames.Sub, patientAccountId.ToString()),
                new(JwtRegisteredClaimNames.Name, phoneNumber),
                new(AppClaims.Role, Domain.Common.Roles.Patient),
                new(AppClaims.SessionId, sessionId.ToString()),
            ],
            notBefore: now.UtcDateTime,
            expires: now.AddMinutes(_o.AccessTokenMinutes).UtcDateTime,
            signingCredentials: new SigningCredentials(SigningKey(_o), SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), _o.AccessTokenMinutes * 60);
    }

    public string CreateRefreshTokenValue() =>
        "rt_" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public string HashRefreshToken(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static SymmetricSecurityKey SigningKey(JwtOptions o)
    {
        if (string.IsNullOrEmpty(o.SigningKey) || o.SigningKey.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.SigningKey));
    }
}
