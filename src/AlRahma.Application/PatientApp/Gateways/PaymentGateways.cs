using System.Security.Cryptography;
using System.Text;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Entities;
using Microsoft.Extensions.Options;

namespace AlRahma.Application.PatientApp.Gateways;

/// <summary>
/// طبقة الدفع الموحدة (التدقيق 3.5-ب): كل بوابة (زين كاش، كي كارد، تجريبية أو حقيقية) تنفّذ هذه الواجهة.
/// الحجز لا يُعتبر مدفوعاً إلا بنتيجة <see cref="InquireAsync"/> (مصدر الحقيقة)، لا من صفحة العودة ولا من الإشعار.
/// اختيار التنفيذ بالإعداد: Payments__Providers__{zaincash|qicard}__Mode = mock | live | off.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>zaincash | qicard (نفس قيم عمود online_payments.provider).</summary>
    string Provider { get; }
    /// <summary>mock | live.</summary>
    string Mode { get; }
    bool IsSandbox { get; }
    Task<GatewayCreateResult> CreateAsync(GatewayCreateRequest req, CancellationToken ct);
    Task<GatewayInquiry> InquireAsync(string reference, CancellationToken ct);
    Task<GatewayRefundResult> RefundAsync(string reference, long amount, CancellationToken ct);
    /// <summary>يتحقق من توقيع إشعار البوابة (Webhook) ويعيد مرجع المعاملة، أو null إن كان التوقيع غير صالح.</summary>
    string? VerifyCallback(string body, string? signature);
}

/// <summary>حالات المعاملة لدى البوابة (القسم 3.5): قيد الانتظار، مدفوعة، فاشلة، ملغاة، مسترجعة.</summary>
public static class GatewayStatuses
{
    public const string Pending = "pending";
    public const string Paid = "paid";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Refunded = "refunded";
}

public record GatewayCreateRequest(Guid OnlinePaymentId, long Amount, string Currency, string Description, DateTimeOffset ExpiresAt);
public record GatewayCreateResult(string Reference, string RedirectUrl);
public record GatewayInquiry(string Status, string? TransactionId, string? FailureCode, long Amount, long RefundedAmount, string Raw);
public record GatewayRefundResult(bool Ok, string? RefundId, string? Error);

public class PaymentProviderOptions
{
    /// <summary>mock (تجريبي، الافتراضي) | live (البوابة الحقيقية) | off.</summary>
    public string Mode { get; set; } = "mock";
    /// <summary>سر توقيع الإشعارات (HMAC-SHA256). فارغ = كل إشعار مرفوض.</summary>
    public string WebhookSecret { get; set; } = "";
}

public record PaymentProviderDto(string Code, string NameAr, string Mode);

/// <summary>يختار تنفيذ البوابة حسب الإعداد. التجريبي (mock) معطل حين Payments:SandboxAllowed=false (الإنتاج).</summary>
public class PaymentGatewayRegistry(IEnumerable<IPaymentGateway> gateways, IOptions<OnlinePaymentOptions> options)
{
    public static readonly string[] Known = ["zaincash", "qicard"];
    private static readonly Dictionary<string, string> Names = new() { ["zaincash"] = "زين كاش", ["qicard"] = "كي كارد" };

    public PaymentProviderOptions OptionsOf(string provider) =>
        options.Value.Providers.TryGetValue(provider, out var p) ? p : new PaymentProviderOptions();

    public IPaymentGateway? Find(string? provider)
    {
        if (provider is null || !Known.Contains(provider)) return null;
        var mode = OptionsOf(provider).Mode.Trim().ToLowerInvariant();
        if (mode == "off") return null;
        if (mode == "mock" && !options.Value.SandboxAllowed) return null;
        return gateways.FirstOrDefault(g => g.Provider == provider && g.Mode == mode);
    }

    public IReadOnlyList<PaymentProviderDto> Available() =>
        Known.Select(Find).Where(g => g is not null).Select(g => new PaymentProviderDto(g!.Provider, Names[g.Provider], g.Mode)).ToList();

    /// <summary>
    /// الاسترجاع عبر البوابة (3.5-ح، 5.1-ج). الدفعات القديمة بلا مرجع (البوابة العامة قبل 019) تُسجَّل فقط كما كانت.
    /// </summary>
    public async Task RefundAsync(OnlinePayment op, long amount, CancellationToken ct)
    {
        if (op.ProviderReference is null) return;
        var gateway = Find(op.Provider) ?? throw new AppException(422, "GATEWAY_UNAVAILABLE", "بوابة الدفع غير متاحة للاسترجاع");
        var r = await gateway.RefundAsync(op.ProviderReference, amount, ct);
        if (!r.Ok) throw new AppException(502, "GATEWAY_REFUND_FAILED", "رفضت البوابة الاسترجاع", r.Error);
    }

    public static string Sign(string secret, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}
