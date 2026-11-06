using System.Text.Json;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlRahma.Application.PatientApp.Gateways;

/// <summary>
/// بيانات الاختبار الثابتة للبوابة التجريبية (موثقة في dock/docs/review/audit-logic.md، قسم «بيانات اختبار البوابة التجريبية»).
/// كل رقم يعطي نتيجة محددة دائماً.
/// </summary>
public static class MockGatewayTestData
{
    public const string ZainCashPin = "1234";
    public const string ZainCashOtp = "1111";       // 4 أرقام كما في زين كاش
    public const string QiExpiry = "12/30";
    public const string QiCvv = "123";
    public const string QiOtp = "123456";
    public const int MaxSecretAttempts = 3;
    public const int MaxOtpAttempts = 3;
    public const int MaxOtpSends = 4;               // الإرسال الأول + 3 إعادات
    public const int OtpLifetimeSeconds = 120;
    public const int OtpResendAfterSeconds = 30;

    /// <summary>المحافظ بصيغة 964، وتُقبل 07 أيضاً.</summary>
    public static readonly Dictionary<string, string> Wallets = new()
    {
        ["9647800000001"] = Outcomes.Success,
        ["9647800000002"] = Outcomes.InsufficientFunds,
        ["9647800000003"] = Outcomes.Timeout,
        ["9647800000004"] = Outcomes.GatewayError,
    };

    public static readonly Dictionary<string, string> Cards = new()
    {
        ["4111111111111111"] = Outcomes.Success,
        ["4111111111111112"] = Outcomes.InsufficientFunds,
        ["4111111111111113"] = Outcomes.Declined,
        ["4111111111111114"] = Outcomes.Timeout,
    };

    public static class Outcomes
    {
        public const string Success = "success";
        public const string InsufficientFunds = "insufficient_funds";
        public const string Declined = "declined";
        public const string Timeout = "timeout";
        public const string GatewayError = "gateway_error";
    }
}

public record MockCredentialsRequest(string? WalletNumber, string? Pin, string? CardNumber, string? Expiry, string? Cvv);
public record MockOtpRequest(string? Otp);
public record MockGatewayPageDto(string Reference, string Provider, string MerchantName, long Amount, string Currency, string Status, string Step,
    string? AccountMasked, string? FailureCode, int SecretAttemptsLeft, int OtpAttemptsLeft, int OtpLength, DateTimeOffset? OtpExpiresAt,
    DateTimeOffset? OtpResendAt, int OtpResendsLeft, DateTimeOffset ExpiresAt, Guid OnlinePaymentId);

/// <summary>
/// تنفيذ IPaymentGateway التجريبي. يحفظ حالة المعاملة في mock_gateway_transactions كأنه خادم البوابة،
/// والنظام لا يعرف النتيجة إلا بالاستعلام (InquireAsync).
/// </summary>
public abstract class MockPaymentGateway(IAppDbContext db, IClock clock, IOptions<OnlinePaymentOptions> options) : IPaymentGateway
{
    public abstract string Provider { get; }
    protected abstract string Prefix { get; }
    public string Mode => "mock";
    public bool IsSandbox => true;

    public Task<GatewayCreateResult> CreateAsync(GatewayCreateRequest req, CancellationToken ct)
    {
        var reference = $"{Prefix}-{Guid.NewGuid():N}"[..(Prefix.Length + 17)].ToUpperInvariant();
        var now = clock.UtcNow;
        db.MockGatewayTransactions.Add(new MockGatewayTransaction
        {
            Reference = reference, Provider = Provider, OnlinePaymentId = req.OnlinePaymentId, Amount = req.Amount, Currency = req.Currency,
            ExpiresAt = req.ExpiresAt, CreatedAt = now, UpdatedAt = now,
        });
        // صفحة البوابة: في التجريبي هي صفحة داخل التطبيق تستدعي /api/v1/mock-gateway/{reference} بتوكن المريض
        return Task.FromResult(new GatewayCreateResult(reference, $"{options.Value.PublicBaseUrl.TrimEnd('/')}/api/v1/mock-gateway/{reference}"));
    }

    public async Task<GatewayInquiry> InquireAsync(string reference, CancellationToken ct)
    {
        var tx = await db.MockGatewayTransactions.FirstOrDefaultAsync(t => t.Reference == reference, ct)
                 ?? throw new AppException(404, "GATEWAY_TRANSACTION_NOT_FOUND", "المعاملة غير موجودة لدى البوابة");
        if (MockGatewayService.ExpireIfDue(tx, clock.UtcNow)) await db.SaveChangesAsync(ct);
        return new GatewayInquiry(tx.Status, tx.TransactionId, tx.FailureCode, tx.Amount, tx.RefundedAmount,
            JsonSerializer.Serialize(new { provider = Provider, reference, status = tx.Status, transaction_id = tx.TransactionId, failure_code = tx.FailureCode, sandbox = true }));
    }

    public async Task<GatewayRefundResult> RefundAsync(string reference, long amount, CancellationToken ct)
    {
        var tx = await db.MockGatewayTransactions.FirstOrDefaultAsync(t => t.Reference == reference, ct);
        if (tx is null) return new GatewayRefundResult(false, null, "المعاملة غير موجودة");
        if (tx.Status is not (GatewayStatuses.Paid or GatewayStatuses.Refunded)) return new GatewayRefundResult(false, null, "المعاملة غير مدفوعة");
        if (amount <= 0 || tx.RefundedAmount + amount > tx.Amount) return new GatewayRefundResult(false, null, "المبلغ أكبر من المتبقي");
        tx.RefundedAmount += amount;
        if (tx.RefundedAmount >= tx.Amount) tx.Status = GatewayStatuses.Refunded;
        tx.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return new GatewayRefundResult(true, $"RF-{Guid.NewGuid():N}"[..15].ToUpperInvariant(), null);
    }

    public string? VerifyCallback(string body, string? signature)
    {
        var secret = options.Value.Providers.TryGetValue(Provider, out var p) ? p.WebhookSecret : "";
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signature)) return null;
        if (!CryptographicEquals(PaymentGatewayRegistry.Sign(secret, body), signature.Trim().ToLowerInvariant())) return null;
        try { return JsonDocument.Parse(body).RootElement.GetProperty("reference").GetString(); }
        catch { return null; }
    }

    private static bool CryptographicEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}

public sealed class ZainCashMockGateway(IAppDbContext db, IClock clock, IOptions<OnlinePaymentOptions> options) : MockPaymentGateway(db, clock, options)
{
    public override string Provider => "zaincash";
    protected override string Prefix => "ZC";
}

public sealed class QiCardMockGateway(IAppDbContext db, IClock clock, IOptions<OnlinePaymentOptions> options) : MockPaymentGateway(db, clock, options)
{
    public override string Provider => "qicard";
    protected override string Prefix => "QI";
}

/// <summary>
/// صفحة البوابة التجريبية (ما يراه المراجع في صفحة زين كاش أو كي): الخطوات تغيّر حالة المعاملة لدى «البوابة» فقط.
/// كل الخطوات تتطلب دخول المريض صاحب الدفعة. الحجز لا يتأثر حتى يستعلم النظام (POST /online-payments/{id}/verify).
/// </summary>
public class MockGatewayService(IAppDbContext db, IClock clock, PatientPortalService portal, ICurrentUser current)
{
    private async Task<MockGatewayTransaction> LoadAsync(string reference, CancellationToken ct)
    {
        var tx = await db.MockGatewayTransactions.FirstOrDefaultAsync(t => t.Reference == reference, ct)
                 ?? throw new AppException(404, "GATEWAY_TRANSACTION_NOT_FOUND", "المعاملة غير موجودة");
        var patientId = await db.OnlinePayments.AsNoTracking().Where(o => o.OnlinePaymentId == tx.OnlinePaymentId).Select(o => (Guid?)o.PatientId).FirstOrDefaultAsync(ct)
                        ?? throw new AppException(404, "GATEWAY_TRANSACTION_NOT_FOUND", "المعاملة غير موجودة");
        if (!current.IsPatient) throw new AppException(404, "GATEWAY_TRANSACTION_NOT_FOUND", "المعاملة غير موجودة");
        await portal.EnsureLinkedAsync(patientId, ct);
        return tx;
    }

    internal static bool ExpireIfDue(MockGatewayTransaction tx, DateTimeOffset now)
    {
        if (tx.Status != GatewayStatuses.Pending || tx.ExpiresAt > now) return false;
        tx.Status = GatewayStatuses.Failed;
        tx.FailureCode = "TIMEOUT";
        tx.Step = "done";
        tx.UpdatedAt = now;
        return true;
    }

    public async Task<MockGatewayPageDto> GetAsync(string reference, CancellationToken ct)
    {
        var tx = await LoadAsync(reference, ct);
        ExpireIfDue(tx, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return Dto(tx);
    }

    public async Task<MockGatewayPageDto> SubmitCredentialsAsync(string reference, MockCredentialsRequest req, CancellationToken ct)
    {
        var tx = await OpenAsync(reference, "credentials", ct);
        var now = clock.UtcNow;
        string outcome;
        bool secretOk;
        if (tx.Provider == "zaincash")
        {
            var wallet = NormalizeWallet(req.WalletNumber);
            new Validator().Check(wallet is not null, "wallet_number", "رقم المحفظة غير صحيح (مثال 07800000001)")
                .Check(req.Pin is { Length: 4 } && req.Pin.All(char.IsDigit), "pin", "الرمز السري 4 أرقام").ThrowIfInvalid();
            if (!MockGatewayTestData.Wallets.TryGetValue(wallet!, out outcome!))
                throw new AppException(422, "ACCOUNT_NOT_FOUND", "رقم المحفظة غير مسجل في زين كاش");
            if (outcome == MockGatewayTestData.Outcomes.GatewayError)
                throw new AppException(503, "GATEWAY_UNAVAILABLE", "تعذر الاتصال بالبوابة", "أعد المحاولة بعد قليل");
            tx.TestAccount = wallet;
            tx.AccountMasked = wallet![..5] + "****" + wallet[^4..];
            secretOk = req.Pin == MockGatewayTestData.ZainCashPin;
            if (!secretOk) return await WrongSecretAsync(tx, "WRONG_PIN", "الرمز السري غير صحيح", ct);
        }
        else
        {
            var card = new string((req.CardNumber ?? "").Where(char.IsDigit).ToArray());
            new Validator().Check(card.Length == 16, "card_number", "رقم البطاقة 16 رقماً")
                .Check(TryExpiry(req.Expiry, out _), "expiry", "تاريخ الانتهاء بصيغة MM/YY")
                .Check(req.Cvv is { Length: 3 } && req.Cvv.All(char.IsDigit), "cvv", "CVV ثلاثة أرقام").ThrowIfInvalid();
            if (!MockGatewayTestData.Cards.TryGetValue(card, out outcome!))
                throw new AppException(422, "CARD_NOT_FOUND", "البطاقة غير معروفة لدى كي كارد");
            tx.TestAccount = card;
            tx.AccountMasked = card[..4] + " **** **** " + card[^4..];
            TryExpiry(req.Expiry, out var exp);
            if (exp < new DateOnly(now.Year, now.Month, 1))
                return await FinishAsync(tx, GatewayStatuses.Failed, "CARD_EXPIRED", ct);
            secretOk = req.Expiry!.Trim() == MockGatewayTestData.QiExpiry && req.Cvv == MockGatewayTestData.QiCvv;
            if (!secretOk) return await WrongSecretAsync(tx, "WRONG_CARD_DATA", "تاريخ الانتهاء أو CVV غير صحيح", ct);
        }

        switch (outcome)
        {
            case MockGatewayTestData.Outcomes.InsufficientFunds: return await FinishAsync(tx, GatewayStatuses.Failed, "INSUFFICIENT_FUNDS", ct);
            case MockGatewayTestData.Outcomes.Declined: return await FinishAsync(tx, GatewayStatuses.Failed, "CARD_DECLINED", ct);
        }
        tx.Step = "otp";
        tx.OtpSends = 1;
        tx.OtpAttempts = 0;
        tx.OtpExpiresAt = now.AddSeconds(MockGatewayTestData.OtpLifetimeSeconds);
        tx.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return Dto(tx);
    }

    public async Task<MockGatewayPageDto> VerifyOtpAsync(string reference, MockOtpRequest req, CancellationToken ct)
    {
        var tx = await OpenAsync(reference, "otp", ct);
        var now = clock.UtcNow;
        var expected = tx.Provider == "zaincash" ? MockGatewayTestData.ZainCashOtp : MockGatewayTestData.QiOtp;
        new Validator().Check(req.Otp is not null && req.Otp.Length == expected.Length && req.Otp.All(char.IsDigit), "otp", $"رمز التحقق {expected.Length} أرقام").ThrowIfInvalid();
        if (tx.OtpExpiresAt <= now) throw new AppException(422, "OTP_EXPIRED", "انتهت صلاحية رمز التحقق", "اطلب رمزاً جديداً");
        if (req.Otp != expected)
        {
            tx.OtpAttempts++;
            tx.UpdatedAt = now;
            if (tx.OtpAttempts >= MockGatewayTestData.MaxOtpAttempts) return await FinishAsync(tx, GatewayStatuses.Failed, "OTP_ATTEMPTS_EXCEEDED", ct);
            await db.SaveChangesAsync(ct);
            throw new AppException(422, "WRONG_OTP", "رمز التحقق غير صحيح", $"المحاولات المتبقية {MockGatewayTestData.MaxOtpAttempts - tx.OtpAttempts}")
                { Extensions = new Dictionary<string, object?> { ["attempts_left"] = MockGatewayTestData.MaxOtpAttempts - tx.OtpAttempts } };
        }
        var outcome = tx.Provider == "zaincash" ? MockGatewayTestData.Wallets[tx.TestAccount!] : MockGatewayTestData.Cards[tx.TestAccount!];
        if (outcome == MockGatewayTestData.Outcomes.Timeout)
        {
            // البوابة لا ترد: المعاملة تبقى pending حتى تنتهي مهلتها، ثم يعطي الاستعلام failed/TIMEOUT
            tx.Step = "processing";
            tx.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            throw new AppException(504, "GATEWAY_TIMEOUT", "انتهت مهلة الاتصال بالبوابة", "لم يُخصم أي مبلغ حتى الآن. تحقق من حالة الدفع لاحقاً");
        }
        tx.TransactionId = $"{(tx.Provider == "zaincash" ? "ZCT" : "QIT")}{now:yyMMddHHmmss}{Random.Shared.Next(1000, 9999)}";
        tx.PaidAt = now;
        return await FinishAsync(tx, GatewayStatuses.Paid, null, ct);
    }

    public async Task<MockGatewayPageDto> ResendOtpAsync(string reference, CancellationToken ct)
    {
        var tx = await OpenAsync(reference, "otp", ct);
        var now = clock.UtcNow;
        if (tx.OtpSends >= MockGatewayTestData.MaxOtpSends) throw new AppException(429, "OTP_RESEND_LIMIT", "تجاوزت عدد مرات إعادة الإرسال");
        var lastSent = (tx.OtpExpiresAt ?? now).AddSeconds(-MockGatewayTestData.OtpLifetimeSeconds);
        if (now < lastSent.AddSeconds(MockGatewayTestData.OtpResendAfterSeconds))
            throw new AppException(429, "OTP_RESEND_TOO_SOON", "انتظر قبل طلب رمز جديد");
        tx.OtpSends++;
        tx.OtpAttempts = 0;
        tx.OtpExpiresAt = now.AddSeconds(MockGatewayTestData.OtpLifetimeSeconds);
        tx.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return Dto(tx);
    }

    public async Task<MockGatewayPageDto> CancelAsync(string reference, CancellationToken ct)
    {
        var tx = await LoadAsync(reference, ct);
        ExpireIfDue(tx, clock.UtcNow);
        if (tx.Status != GatewayStatuses.Pending) { await db.SaveChangesAsync(ct); return Dto(tx); }
        return await FinishAsync(tx, GatewayStatuses.Cancelled, "CANCELLED_BY_USER", ct);
    }

    private async Task<MockGatewayTransaction> OpenAsync(string reference, string step, CancellationToken ct)
    {
        var tx = await LoadAsync(reference, ct);
        ExpireIfDue(tx, clock.UtcNow);
        if (tx.Status != GatewayStatuses.Pending)
        {
            await db.SaveChangesAsync(ct);
            throw new AppException(409, tx.FailureCode == "TIMEOUT" ? "TRANSACTION_EXPIRED" : "TRANSACTION_CLOSED", "المعاملة منتهية",
                $"الحالة: {tx.Status}") { Extensions = new Dictionary<string, object?> { ["status"] = tx.Status } };
        }
        if (tx.Step != step) throw new AppException(409, "WRONG_STEP", "خطوة غير متوقعة", $"الخطوة الحالية: {tx.Step}");
        return tx;
    }

    private async Task<MockGatewayPageDto> WrongSecretAsync(MockGatewayTransaction tx, string code, string title, CancellationToken ct)
    {
        tx.SecretAttempts++;
        tx.UpdatedAt = clock.UtcNow;
        if (tx.SecretAttempts >= MockGatewayTestData.MaxSecretAttempts) return await FinishAsync(tx, GatewayStatuses.Failed, code + "_LIMIT", ct);
        await db.SaveChangesAsync(ct);
        throw new AppException(422, code, title, $"المحاولات المتبقية {MockGatewayTestData.MaxSecretAttempts - tx.SecretAttempts}")
            { Extensions = new Dictionary<string, object?> { ["attempts_left"] = MockGatewayTestData.MaxSecretAttempts - tx.SecretAttempts } };
    }

    private async Task<MockGatewayPageDto> FinishAsync(MockGatewayTransaction tx, string status, string? failure, CancellationToken ct)
    {
        tx.Status = status;
        tx.FailureCode = failure;
        tx.Step = "done";
        tx.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return Dto(tx);
    }

    private static string? NormalizeWallet(string? raw)
    {
        var d = new string((raw ?? "").Where(char.IsDigit).ToArray());
        if (d.StartsWith("07") && d.Length == 11) d = "964" + d[1..];
        return d.Length == 13 && d.StartsWith("9647") ? d : null;
    }

    private static bool TryExpiry(string? raw, out DateOnly month)
    {
        month = default;
        var parts = (raw ?? "").Trim().Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var m) || !int.TryParse(parts[1], out var y) || m is < 1 or > 12 || parts[1].Length != 2) return false;
        month = new DateOnly(2000 + y, m, 1);
        return true;
    }

    private static MockGatewayPageDto Dto(MockGatewayTransaction t)
    {
        var lastSent = t.OtpExpiresAt?.AddSeconds(-MockGatewayTestData.OtpLifetimeSeconds);
        return new MockGatewayPageDto(t.Reference, t.Provider, "مجمع الرحمة الطبي", t.Amount, t.Currency, t.Status, t.Step, t.AccountMasked, t.FailureCode,
            Math.Max(0, MockGatewayTestData.MaxSecretAttempts - t.SecretAttempts), Math.Max(0, MockGatewayTestData.MaxOtpAttempts - t.OtpAttempts),
            t.Provider == "zaincash" ? MockGatewayTestData.ZainCashOtp.Length : MockGatewayTestData.QiOtp.Length, t.OtpExpiresAt,
            lastSent?.AddSeconds(MockGatewayTestData.OtpResendAfterSeconds), Math.Max(0, MockGatewayTestData.MaxOtpSends - t.OtpSends), t.ExpiresAt, t.OnlinePaymentId);
    }
}
