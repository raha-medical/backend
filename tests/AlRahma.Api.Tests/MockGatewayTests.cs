using System.Text;

namespace AlRahma.Api.Tests;

/// <summary>رحلة البوابة التجريبية في الاختبارات: بيانات الاختبار الثابتة (audit-logic.md، «بيانات اختبار البوابة التجريبية»).</summary>
public static class MockPay
{
    public const string Wallet = "07800000001", InsufficientWallet = "07800000002", TimeoutWallet = "07800000003", ErrorWallet = "07800000004";
    public const string Pin = "1234", Otp = "1111";
    public const string Card = "4111111111111111", InsufficientCard = "4111111111111112", DeclinedCard = "4111111111111113";

    public static async Task<string> ReferenceAsync(HttpClient me, Guid opId) =>
        (await Api.GetAsync(me, $"/api/v1/online-payments/{opId}")).Body!["provider_reference"]!.GetValue<string>();

    /// <summary>زين كاش ناجح كاملاً (محفظة + PIN ثم OTP) ثم الاستعلام. يعيد رد /verify.</summary>
    public static async Task<ApiResult> SucceedAsync(HttpClient me, Guid opId)
    {
        var reference = await ReferenceAsync(me, opId);
        var c = await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/credentials", new { wallet_number = Wallet, pin = Pin });
        if (c.Status != 200) throw new InvalidOperationException("credentials: " + c.Raw);
        var o = await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/otp", new { otp = Otp });
        if (o.Status != 200) throw new InvalidOperationException("otp: " + o.Raw);
        return await Api.PostAsync(me, $"/api/v1/online-payments/{opId}/verify");
    }
}

/// <summary>الحساب اليومي في الاختبارات: كل وصل يُطبع قبل الإغلاق (POST /receipts/{id}/printed)، كما تفعل الواجهة بعد نجاح الطابعة.</summary>
public static class DailyAccount
{
    public static async Task PrintAllAsync(HttpClient cashier)
    {
        var cur = await Api.GetAsync(cashier, "/api/v1/shifts/current");
        foreach (var r in cur.Body!["summary"]!["unprinted_receipts"]!.AsArray())
            await Api.PostAsync(cashier, $"/api/v1/receipts/{r!["receipt_id"]!.GetValue<Guid>()}/printed");
    }
}

[Collection(ApiCollection.Name)]
public class MockGatewayTests(TestApp app)
{
    private static Guid Id(System.Text.Json.Nodes.JsonNode? n, string k) => n![k]!.GetValue<Guid>();
    private static string S(System.Text.Json.Nodes.JsonNode? n, string k) => n![k]!.GetValue<string>();

    /// <summary>مريض بتطبيق وحجز اليوم غير مدفوع.</summary>
    private async Task<(HttpClient Me, Guid Patient, Guid Booking)> BookingAsync()
    {
        var s = await new QueueTests(app).SetupAsync();
        var phone = "0775" + Random.Shared.Next(1000000, 9999999);
        var p = await Api.PostAsync(s.Reception, "/api/v1/patients", new { full_name = "مريض بوابة", phone_number = phone, gender = "female", approx_age = 30 }, Guid.NewGuid());
        var patient = Id(p.Body, "patient_id");
        var otp = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/request", new { phone_number = phone });
        var login = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = phone, code = S(otp.Body, "sandbox_code") });
        var me = app.Client(S(login.Body, "access_token"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var sessions = await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        var booking = await Api.PostAsync(me, "/api/v1/bookings", new { patient_id = patient, session_id = Id(sessions.Body!["items"]![0], "session_id"), visit_kind = "auto" }, Guid.NewGuid());
        Assert.True(booking.Status == 201, booking.Raw);
        return (me, patient, Id(booking.Body, "booking_id"));
    }

    internal Task<(HttpClient Me, Guid Patient, Guid Booking)> BookingForTestsAsync() => BookingAsync();
    internal static Task<(Guid Op, string Ref)> CreateForTestsAsync(HttpClient me, Guid patient, Guid booking, string provider) => CreateAsync(me, patient, booking, provider);

    private static async Task<(Guid Op, string Ref)> CreateAsync(HttpClient me, Guid patient, Guid booking, string provider)
    {
        var op = await Api.PostAsync(me, "/api/v1/online-payments", new { purpose = "booking_full", patient_id = patient, booking_id = booking, provider }, Guid.NewGuid());
        Assert.True(op.Status == 201, op.Raw);
        Assert.Equal("pending", S(op.Body, "status"));
        return (Id(op.Body, "online_payment_id"), S(op.Body, "provider_reference"));
    }

    private static async Task<string> BookingPaymentAsync(HttpClient me, Guid booking) =>
        S((await Api.GetAsync(me, $"/api/v1/bookings/{booking}")).Body, "payment_status");

    [SkippableFact]
    public async Task ZainCash_wallet_pin_otp_paid_only_after_inquiry()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (me, patient, booking) = await BookingAsync();
        var providers = await Api.GetAsync(me, "/api/v1/online-payments/providers");
        Assert.Equal(new[] { "zaincash", "qicard" }, providers.Body!.AsArray().Select(x => S(x, "code")));
        var (op, reference) = await CreateAsync(me, patient, booking, "zaincash");
        Assert.StartsWith("ZC-", reference);

        // أمان: لا شيء بلا تسجيل دخول
        Assert.Equal(401, (await Api.PostAsync(app.Client(), $"/api/v1/mock-gateway/{reference}/credentials", new { wallet_number = MockPay.Wallet, pin = MockPay.Pin })).Status);
        Assert.Equal(401, (await Api.PostAsync(app.Client(), $"/api/v1/online-payments/{op}/verify")).Status);
        Assert.Equal(401, (await Api.PostAsync(app.Client(), "/api/v1/webhooks/payments/zaincash", new { reference })).Status);

        var page = await Api.GetAsync(me, $"/api/v1/mock-gateway/{reference}");
        Assert.Equal("credentials", S(page.Body, "step"));
        Assert.Equal("ACCOUNT_NOT_FOUND", (await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/credentials", new { wallet_number = "07809999999", pin = "1234" })).Code);
        var wrong = await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/credentials", new { wallet_number = MockPay.Wallet, pin = "9999" });
        Assert.Equal("WRONG_PIN", wrong.Code);
        var cred = await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/credentials", new { wallet_number = MockPay.Wallet, pin = MockPay.Pin });
        Assert.True(cred.Status == 200, cred.Raw);
        Assert.Equal("otp", S(cred.Body, "step"));
        Assert.Equal(4, cred.Body!["otp_length"]!.GetValue<int>());
        Assert.Equal("WRONG_OTP", (await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/otp", new { otp = "2222" })).Code);
        var paid = await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/otp", new { otp = MockPay.Otp });
        Assert.Equal("paid", S(paid.Body, "status"));

        // البوابة دفعت لكن النظام لم يستعلم بعد: الحجز غير مدفوع
        Assert.Equal("unpaid", await BookingPaymentAsync(me, booking));
        var verify = await Api.PostAsync(me, $"/api/v1/online-payments/{op}/verify");
        Assert.True(verify.Status == 200, verify.Raw);
        Assert.Equal("succeeded", S(verify.Body, "status"));
        Assert.StartsWith("B1-O-", S(verify.Body!["receipt"], "receipt_number"));
        Assert.Null(verify.Body!["is_sandbox"]); // العلامة للإدارة فقط
        Assert.Equal("paid", await BookingPaymentAsync(me, booking));
        Assert.True(await app.ScalarAsync<bool>($"SELECT is_sandbox FROM online_payments WHERE online_payment_id = '{op}'"));
    }

    [SkippableFact]
    public async Task Deterministic_failures_insufficient_pin_limit_otp_limit_cancel_timeout()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (me, patient, booking) = await BookingAsync();

        // رصيد غير كافٍ
        var (op1, r1) = await CreateAsync(me, patient, booking, "zaincash");
        var f1 = await Api.PostAsync(me, $"/api/v1/mock-gateway/{r1}/credentials", new { wallet_number = MockPay.InsufficientWallet, pin = MockPay.Pin });
        Assert.Equal("INSUFFICIENT_FUNDS", S(f1.Body, "failure_code"));
        var v1 = await Api.PostAsync(me, $"/api/v1/online-payments/{op1}/verify");
        Assert.Equal("failed", S(v1.Body, "status"));
        Assert.Equal("INSUFFICIENT_FUNDS", S(v1.Body, "failure_code"));

        // PIN خاطئ 3 مرات
        var (op2, r2) = await CreateAsync(me, patient, booking, "zaincash");
        for (var i = 0; i < 2; i++) Assert.Equal("WRONG_PIN", (await Api.PostAsync(me, $"/api/v1/mock-gateway/{r2}/credentials", new { wallet_number = MockPay.Wallet, pin = "0000" })).Code);
        Assert.Equal("WRONG_PIN_LIMIT", S((await Api.PostAsync(me, $"/api/v1/mock-gateway/{r2}/credentials", new { wallet_number = MockPay.Wallet, pin = "0000" })).Body, "failure_code"));
        Assert.Equal("failed", S((await Api.PostAsync(me, $"/api/v1/online-payments/{op2}/verify")).Body, "status"));

        // OTP منتهٍ ثم إعادة الإرسال، ثم OTP خاطئ 3 مرات
        var (op3, r3) = await CreateAsync(me, patient, booking, "zaincash");
        await Api.PostAsync(me, $"/api/v1/mock-gateway/{r3}/credentials", new { wallet_number = MockPay.Wallet, pin = MockPay.Pin });
        await app.ExecAsync($"UPDATE mock_gateway_transactions SET otp_expires_at = now() - interval '1 second' WHERE reference = '{r3}'");
        Assert.Equal("OTP_EXPIRED", (await Api.PostAsync(me, $"/api/v1/mock-gateway/{r3}/otp", new { otp = MockPay.Otp })).Code);
        Assert.Equal(200, (await Api.PostAsync(me, $"/api/v1/mock-gateway/{r3}/otp/resend")).Status);
        Assert.Equal("OTP_RESEND_TOO_SOON", (await Api.PostAsync(me, $"/api/v1/mock-gateway/{r3}/otp/resend")).Code);
        for (var i = 0; i < 2; i++) Assert.Equal("WRONG_OTP", (await Api.PostAsync(me, $"/api/v1/mock-gateway/{r3}/otp", new { otp = "9999" })).Code);
        Assert.Equal("OTP_ATTEMPTS_EXCEEDED", S((await Api.PostAsync(me, $"/api/v1/mock-gateway/{r3}/otp", new { otp = "9999" })).Body, "failure_code"));
        Assert.Equal("failed", S((await Api.PostAsync(me, $"/api/v1/online-payments/{op3}/verify")).Body, "status"));

        // المراجع ألغى
        var (op4, r4) = await CreateAsync(me, patient, booking, "qicard");
        Assert.Equal("cancelled", S((await Api.PostAsync(me, $"/api/v1/mock-gateway/{r4}/cancel")).Body, "status"));
        Assert.Equal("cancelled", S((await Api.PostAsync(me, $"/api/v1/online-payments/{op4}/verify")).Body, "status"));

        // انتهاء المهلة: البوابة لا ترد، المعاملة معلقة حتى المهلة ثم TIMEOUT
        var (op5, r5) = await CreateAsync(me, patient, booking, "zaincash");
        await Api.PostAsync(me, $"/api/v1/mock-gateway/{r5}/credentials", new { wallet_number = MockPay.TimeoutWallet, pin = MockPay.Pin });
        Assert.Equal("GATEWAY_TIMEOUT", (await Api.PostAsync(me, $"/api/v1/mock-gateway/{r5}/otp", new { otp = MockPay.Otp })).Code);
        Assert.Equal("pending", S((await Api.PostAsync(me, $"/api/v1/online-payments/{op5}/verify")).Body, "status"));
        await app.ExecAsync($"UPDATE mock_gateway_transactions SET expires_at = now() - interval '1 second' WHERE reference = '{r5}'; UPDATE online_payments SET expires_at = now() - interval '1 second' WHERE online_payment_id = '{op5}'");
        var v5 = await Api.PostAsync(me, $"/api/v1/online-payments/{op5}/verify");
        Assert.Equal("expired", S(v5.Body, "status"));
        Assert.Equal("TIMEOUT", S(v5.Body, "failure_code"));

        // انقطاع البوابة: لا تغيير، يمكن إعادة المحاولة
        var (_, r6) = await CreateAsync(me, patient, booking, "zaincash");
        Assert.Equal(503, (await Api.PostAsync(me, $"/api/v1/mock-gateway/{r6}/credentials", new { wallet_number = MockPay.ErrorWallet, pin = MockPay.Pin })).Status);
        Assert.Equal("unpaid", await BookingPaymentAsync(me, booking));
    }

    [SkippableFact]
    public async Task QiCard_card_expiry_cvv_otp_and_declined_and_other_patient_hidden()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (me, patient, booking) = await BookingAsync();
        var (opD, rD) = await CreateAsync(me, patient, booking, "qicard");
        Assert.StartsWith("QI-", rD);
        Assert.Equal("CARD_DECLINED", S((await Api.PostAsync(me, $"/api/v1/mock-gateway/{rD}/credentials", new { card_number = MockPay.DeclinedCard, expiry = "12/30", cvv = "123" })).Body, "failure_code"));
        Assert.Equal("failed", S((await Api.PostAsync(me, $"/api/v1/online-payments/{opD}/verify")).Body, "status"));

        var (op, reference) = await CreateAsync(me, patient, booking, "qicard");
        // مريض آخر لا يرى معاملة غيره
        var (other, _, _) = await BookingAsync();
        Assert.Equal(404, (await Api.GetAsync(other, $"/api/v1/mock-gateway/{reference}")).Status);

        Assert.Equal("WRONG_CARD_DATA", (await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/credentials", new { card_number = MockPay.Card, expiry = "12/30", cvv = "999" })).Code);
        var c = await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/credentials", new { card_number = "4111 1111 1111 1111", expiry = "12/30", cvv = "123" });
        Assert.True(c.Status == 200, c.Raw);
        Assert.Equal(6, c.Body!["otp_length"]!.GetValue<int>());
        Assert.Equal("paid", S((await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/otp", new { otp = "123456" })).Body, "status"));
        Assert.Equal("succeeded", S((await Api.PostAsync(me, $"/api/v1/online-payments/{op}/verify")).Body, "status"));
        Assert.Equal("paid", await BookingPaymentAsync(me, booking));
    }

    [SkippableFact]
    public async Task Webhook_needs_valid_signature_and_still_inquires()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        const string secret = "test-webhook-secret";
        using var factory = app.Factory.WithWebHostBuilder(b => b.UseSetting("Payments:Providers:zaincash:WebhookSecret", secret));
        var (me0, patient, booking) = await BookingAsync();
        var me = factory.CreateClient();
        me.DefaultRequestHeaders.Authorization = me0.DefaultRequestHeaders.Authorization;
        var (op, reference) = await CreateAsync(me, patient, booking, "zaincash");
        var body = $"{{\"reference\":\"{reference}\",\"status\":\"paid\"}}";

        async Task<int> Post(string? sig)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments/zaincash") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (sig is not null) req.Headers.Add("X-Signature", sig);
            return (int)(await factory.CreateClient().SendAsync(req)).StatusCode;
        }
        Assert.Equal(401, await Post(null));
        Assert.Equal(401, await Post("deadbeef"));
        // توقيع صالح لكن الإشعار يدّعي الدفع والبوابة تقول pending: لا يُدفع شيء
        Assert.Equal(200, await Post(AlRahma.Application.PatientApp.Gateways.PaymentGatewayRegistry.Sign(secret, body)));
        Assert.Equal("pending", S((await Api.GetAsync(me, $"/api/v1/online-payments/{op}")).Body, "status"));
        Assert.Equal("unpaid", await BookingPaymentAsync(me, booking));
    }

    [SkippableFact]
    public async Task Paid_online_booking_final_no_show_becomes_wallet_credit_without_ban()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (me, patient, booking) = await BookingAsync();
        var (op, _) = await CreateAsync(me, patient, booking, "zaincash");
        Assert.Equal("succeeded", S((await MockPay.SucceedAsync(me, op)).Body, "status"));
        var amount = await app.ScalarAsync<long>($"SELECT amount FROM online_payments WHERE online_payment_id = '{op}'");
        var ticket = await app.ScalarAsync<Guid>($"SELECT ticket_id FROM queue_tickets WHERE booking_id = '{booking}'");
        var staff = app.Client(await app.AdminTokenAsync());
        var noShow = await Api.PostAsync(staff, $"/api/v1/queue/tickets/{ticket}/no-show", new { mode = "final" });
        Assert.True(noShow.Status == 200, noShow.Raw);
        Assert.Equal(amount, await app.ScalarAsync<long>($"SELECT COALESCE(sum(amount),0)::bigint FROM patient_credit_ledger WHERE patient_id = '{patient}' AND booking_id = '{booking}'"));
        Assert.False(await app.ScalarAsync<bool>($"SELECT counted_as_no_show FROM bookings WHERE booking_id = '{booking}'"));
    }
}
