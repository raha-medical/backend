using System.Security.Cryptography;
using System.Text;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using Microsoft.Extensions.Options;

namespace AlRahma.Application.Queue;

/// <summary>إعدادات رمز QR تثبيت الحضور على شاشة الانتظار (CheckIn__*).</summary>
public class CheckInOptions
{
    public const string Section = "CheckIn";
    /// <summary>مفتاح HMAC. فارغ = يُشتق من Jwt:SigningKey (يُضبط في Infrastructure).</summary>
    public string SigningKey { get; set; } = "";
    /// <summary>مدة نافذة الرمز بالثواني (يتجدد الرمز كل نافذة، ويُقبل الحالي والسابق). 15 - 300.</summary>
    public int WindowSeconds { get; set; } = 45;
}

/// <summary>الرمز الذي تعرضه الشاشة. qr_payload هو نص الـ QR، والتطبيق يرسل token أو qr_payload كما هو.</summary>
public record CheckInTokenDto(bool Enabled, string? Token, string? QrPayload, DateTimeOffset? IssuedAt, DateTimeOffset? ExpiresAt,
    int RefreshAfterSeconds, int WindowSeconds, Guid ClinicId, string ClinicName, string Scope);

/// <summary>
/// رمز تثبيت الحضور الدوار: HMAC-SHA256 على (معرف الشاشة + رقم النافذة الزمنية). الشاشة تجلبه بمفتاحها وتعرضه QR،
/// والمريض يمسحه من تطبيقه داخل المجمع. يتغير كل نافذة (45 ثانية افتراضياً) ويُقبل الحالي والسابق فقط،
/// فصورة الرمز المرسلة لشخص خارج المجمع تنتهي خلال دقيقة ونصف على الأكثر. الشاشة تحدد العيادة والفرع.
/// الصيغة: ci1.{device_id بلا شرطات}.{window}.{التوقيع base64url 16 بايت}
/// </summary>
public class CheckInTokenService(IOptions<CheckInOptions> options, IClock clock)
{
    public const string Prefix = "ci1";
    public const string QrScheme = "alrahma://checkin?t=";

    private int Window => Math.Clamp(options.Value.WindowSeconds, 15, 300);

    private byte[] Key => SHA256.HashData(Encoding.UTF8.GetBytes("alrahma-checkin:" + options.Value.SigningKey));

    private long WindowOf(DateTimeOffset at) => at.ToUnixTimeSeconds() / Window;

    public (string Token, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, int RefreshAfterSeconds) Issue(Guid deviceId, DateTimeOffset? at = null)
    {
        var now = at ?? clock.UtcNow;
        var w = WindowOf(now);
        var start = DateTimeOffset.FromUnixTimeSeconds(w * Window);
        // يُقبل حتى نهاية النافذة التالية
        var expires = start.AddSeconds(2 * Window);
        var refresh = (int)Math.Max(1, (start.AddSeconds(Window) - now).TotalSeconds);
        return ($"{Prefix}.{deviceId:N}.{w}.{Sign(deviceId, w)}", start, expires, refresh);
    }

    public int WindowSeconds => Window;

    /// <summary>يعيد معرف الشاشة، أو 422 CHECKIN_TOKEN_INVALID / CHECKIN_TOKEN_EXPIRED.</summary>
    public Guid Validate(string? raw)
    {
        var token = Normalize(raw);
        var parts = token?.Split('.') ?? [];
        if (parts.Length != 4 || parts[0] != Prefix || !Guid.TryParseExact(parts[1], "N", out var deviceId)
            || !long.TryParse(parts[2], out var w))
            throw Invalid();
        var expected = Encoding.ASCII.GetBytes(Sign(deviceId, w));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(parts[3]))) throw Invalid();
        var current = WindowOf(clock.UtcNow);
        // الحالي والسابق (والتالي تسامحاً مع فرق بسيط في الساعة بين الخوادم)
        if (w < current - 1)
            throw new AppException(422, "CHECKIN_TOKEN_EXPIRED", "انتهت صلاحية الرمز", "امسح الرمز الظاهر على شاشة الانتظار الآن");
        if (w > current + 1) throw Invalid();
        return deviceId;
    }

    /// <summary>يقبل الرمز وحده أو نص الـ QR كاملاً (alrahma://checkin?t=...).</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        var i = t.IndexOf("t=", StringComparison.Ordinal);
        if (!t.StartsWith(Prefix + ".", StringComparison.Ordinal) && i >= 0) t = Uri.UnescapeDataString(t[(i + 2)..].Split('&')[0]);
        return t;
    }

    private string Sign(Guid deviceId, long window)
    {
        var mac = HMACSHA256.HashData(Key, Encoding.ASCII.GetBytes($"{Prefix}|{deviceId:N}|{window}"));
        return Convert.ToBase64String(mac, 0, 16).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static AppException Invalid() =>
        new(422, "CHECKIN_TOKEN_INVALID", "رمز تثبيت الحضور غير صالح", "امسح الرمز الظاهر على شاشة الانتظار داخل المجمع");
}
