using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using AlRahma.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AlRahma.Infrastructure.Security;

public class DrawerOptions
{
    public const string Section = "Drawer";
    /// <summary>
    /// مفتاح ES256 الخاص (PEM). يُمرر عبر Drawer__PrivateKeyPem وله الأولوية.
    /// </summary>
    public string PrivateKeyPem { get; set; } = "";
    /// <summary>
    /// بدون PrivateKeyPem: يُقرأ المفتاح من هذا الملف، وإن لم يوجد يُولد مرة واحدة ويُحفظ فيه،
    /// فيبقى المفتاح العام ثابتاً بين مرات التشغيل (الوكيل يحفظه). فارغ = مفتاح مؤقت (اختبارات فقط).
    /// </summary>
    public string KeyFile { get; set; } = "App_Data/drawer-es256.pem";
    public int LifetimeSeconds { get; set; } = 60;
    /// <summary>من Hardware:CashDrawerEnabled (افتراضياً false: الدرج موقوف بالكامل).</summary>
    public bool CashDrawerEnabled { get; set; }
}

public sealed class DrawerTokenIssuer : IDrawerTokenIssuer, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly DrawerOptions _o;
    private readonly IClock _clock;

    public DrawerTokenIssuer(IOptions<DrawerOptions> options, IClock clock, ILogger<DrawerTokenIssuer> log)
    {
        _o = options.Value;
        _clock = clock;
        if (!string.IsNullOrWhiteSpace(_o.PrivateKeyPem))
            _key.ImportFromPem(_o.PrivateKeyPem);
        else if (!string.IsNullOrWhiteSpace(_o.KeyFile))
            LoadOrCreate(Path.GetFullPath(_o.KeyFile), log);
        else
            log.LogWarning("Drawer key: no PrivateKeyPem or KeyFile; using an ephemeral key (changes on every restart).");
        PublicKeyPem = _key.ExportSubjectPublicKeyInfoPem();
        // معرّف قصير للمفتاح العام: الوكيل يقارنه ليعرف أن المفتاح تغيّر فيجلبه من جديد
        PublicKeyId = Convert.ToHexString(SHA256.HashData(_key.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();
    }

    private void LoadOrCreate(string path, ILogger log)
    {
        if (File.Exists(path))
        {
            _key.ImportFromPem(File.ReadAllText(path));
            log.LogInformation("Drawer key loaded from {Path}", path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, _key.ExportPkcs8PrivateKeyPem());
        log.LogWarning("Drawer key generated and saved to {Path}. Back it up; agents trust its public key.", path);
    }

    public bool Enabled => _o.CashDrawerEnabled;
    public string PublicKeyPem { get; }
    public string PublicKeyId { get; }

    public string Issue(string receiptNumber, Guid shiftId, Guid? deviceId)
    {
        var now = _clock.UtcNow.UtcDateTime;
        var claims = new List<Claim>
        {
            new("drawer", "open"),
            new("receipt_number", receiptNumber),
            new("shift_id", shiftId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (deviceId is { } d) claims.Add(new Claim("device_id", d.ToString()));
        var token = new JwtSecurityToken(
            issuer: "alrahma-api",
            audience: "alrahma-agent",
            claims: claims,
            notBefore: now,
            expires: now.AddSeconds(_o.LifetimeSeconds),
            signingCredentials: new SigningCredentials(new ECDsaSecurityKey(_key), SecurityAlgorithms.EcdsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string Sign(string payload) =>
        Convert.ToBase64String(_key.SignData(System.Text.Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));

    public void Dispose() => _key.Dispose();
}
