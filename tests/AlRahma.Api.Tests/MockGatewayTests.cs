using System.Text;

namespace AlRahma.Api.Tests;

/// <summary>رحلة البوابة التجريبية في الاختبارات: بيانات الاختبار الثابتة (audit-logic.md، «بيانات اختبار البوابة التجريبية»).</summary>
public static class MockPay
{
    public const string Wallet = "07800000001", InsufficientWallet = "07800000002", TimeoutWallet = "07800000003", ErrorWallet = "07800000004";
    public const string Pin = "1234";
    public const string Card = "4111111111111111", InsufficientCard = "4111111111111112", DeclinedCard = "4111111111111113";

    /// <summary>الرمز العشوائي من رسالة الـ SMS التجريبية في رد البوابة (sandbox_otp).</summary>
    public static string Otp(ApiResult page) => page.Body!["sandbox_otp"]!.GetValue<string>();
    /// <summary>رمز خاطئ مضمون: نفس الطول بأول رقم مختلف.</summary>
    public static string WrongOtp(ApiResult page) { var o = Otp(page); return (char)('0' + (o[0] - '0' + 1) % 10) + o[1..]; }

    public static async Task<string> ReferenceAsync(HttpClient me, Guid opId) =>
        (await Api.GetAsync(me, $"/api/v1/online-payments/{opId}")).Body!["provider_reference"]!.GetValue<string>();

    /// <summary>زين كاش ناجح كاملاً (محفظة + PIN ثم OTP) ثم الاستعلام. يعيد رد /verify.</summary>
    public static async Task<ApiResult> SucceedAsync(HttpClient me, Guid opId)
    {
        var reference = await ReferenceAsync(me, opId);
        var c = await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/credentials", new { wallet_number = Wallet, pin = Pin });
        if (c.Status != 200) throw new InvalidOperationException("credentials: " + c.Raw);
        var o = await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/otp", new { otp = Otp(c) });
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
        var (me, patient, booking, _) = await BookingWithClinicAsync();
        return (me, patient, booking);
    }

    private async Task<(HttpClient Me, Guid Patient, Guid Booking, ClinicSetup S)> BookingWithClinicAsync()
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
        return (me, patient, Id(booking.Body, "booking_id"), s);
    }

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
        Assert.Equal(400, (await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/credentials", new { wallet_number = "123", pin = "1234" })).Status);
        var wrong = await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/credentials", new { wallet_number = MockPay.Wallet, pin = "9999" });
        Assert.Equal("WRONG_PIN", wrong.Code);
        var cred = await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/credentials", new { wallet_number = MockPay.Wallet, pin = MockPay.Pin });
        Assert.True(cred.Status == 200, cred.Raw);
        Assert.Equal("otp", S(cred.Body, "step"));
        Assert.Equal(4, cred.Body!["otp_length"]!.GetValue<int>());
        // الرمز عشوائي ويصل كرسالة SMS تجريبية فيها الرمز والمبلغ
        var code = MockPay.Otp(cred);
        Assert.Matches("^[0-9]{4}$", code);
        Assert.Contains(code, S(cred.Body, "sandbox_sms"));
        Assert.NotNull(cred.Body!["otp_sent_at"]);
        Assert.Equal(code, MockPay.Otp(await Api.GetAsync(me, $"/api/v1/mock-gateway/{reference}")));
        Assert.Equal("WRONG_OTP", (await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/otp", new { otp = MockPay.WrongOtp(cred) })).Code);
        var paid = await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/otp", new { otp = code });
        Assert.Equal("paid", S(paid.Body, "status"));
        Assert.Null(paid.Body!["sandbox_otp"]); // بعد انتهاء المعاملة لا يبقى الرمز
        Assert.True(await app.ScalarAsync<bool>($"SELECT otp_code IS NULL FROM mock_gateway_transactions WHERE reference = '{reference}'"));

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
        var c3 = await Api.PostAsync(me, $"/api/v1/mock-gateway/{r3}/credentials", new { wallet_number = MockPay.Wallet, pin = MockPay.Pin });
        await app.ExecAsync($"UPDATE mock_gateway_transactions SET otp_expires_at = now() - interval '1 second' WHERE reference = '{r3}'");
        Assert.Equal("OTP_EXPIRED", (await Api.PostAsync(me, $"/api/v1/mock-gateway/{r3}/otp", new { otp = MockPay.Otp(c3) })).Code);
        var resent = await Api.PostAsync(me, $"/api/v1/mock-gateway/{r3}/otp/resend");
        Assert.Equal(200, resent.Status);
        Assert.Equal("OTP_RESEND_TOO_SOON", (await Api.PostAsync(me, $"/api/v1/mock-gateway/{r3}/otp/resend")).Code);
        for (var i = 0; i < 2; i++) Assert.Equal("WRONG_OTP", (await Api.PostAsync(me, $"/api/v1/mock-gateway/{r3}/otp", new { otp = MockPay.WrongOtp(resent) })).Code);
        Assert.Equal("OTP_ATTEMPTS_EXCEEDED", S((await Api.PostAsync(me, $"/api/v1/mock-gateway/{r3}/otp", new { otp = MockPay.WrongOtp(resent) })).Body, "failure_code"));
        Assert.Equal("failed", S((await Api.PostAsync(me, $"/api/v1/online-payments/{op3}/verify")).Body, "status"));

        // المراجع ألغى
        var (op4, r4) = await CreateAsync(me, patient, booking, "qicard");
        Assert.Equal("cancelled", S((await Api.PostAsync(me, $"/api/v1/mock-gateway/{r4}/cancel")).Body, "status"));
        Assert.Equal("cancelled", S((await Api.PostAsync(me, $"/api/v1/online-payments/{op4}/verify")).Body, "status"));

        // انتهاء المهلة: البوابة لا ترد، المعاملة معلقة حتى المهلة ثم TIMEOUT
        var (op5, r5) = await CreateAsync(me, patient, booking, "zaincash");
        var c5 = await Api.PostAsync(me, $"/api/v1/mock-gateway/{r5}/credentials", new { wallet_number = MockPay.TimeoutWallet, pin = MockPay.Pin });
        Assert.Equal("GATEWAY_TIMEOUT", (await Api.PostAsync(me, $"/api/v1/mock-gateway/{r5}/otp", new { otp = MockPay.Otp(c5) })).Code);
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
        Assert.Matches("^[0-9]{6}$", MockPay.Otp(c));
        Assert.Equal("paid", S((await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/otp", new { otp = MockPay.Otp(c) })).Body, "status"));
        Assert.Equal("succeeded", S((await Api.PostAsync(me, $"/api/v1/online-payments/{op}/verify")).Body, "status"));
        Assert.Equal("paid", await BookingPaymentAsync(me, booking));
    }

    /// <summary>طابور الاستقبال لهذا الحجز قبل تثبيت الحضور.</summary>
    private static async Task<System.Text.Json.Nodes.JsonNode> LiveItemAsync(ClinicSetup s, Guid booking)
    {
        var live = await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}");
        Assert.True(live.Status == 200, live.Raw);
        return live.Body!["items"]!.AsArray().First(i => i!["booking_id"]?.GetValue<Guid?>() == booking)!;
    }

    [SkippableFact]
    public async Task Reception_queue_shows_online_full_payment_and_deposit_before_check_in()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        // دفع كامل من التطبيق: الاستقبال يرى «مدفوع» وإلكتروني قبل الحضور (لا فاتورة بعد)
        var (me, patient, booking, s) = await BookingWithClinicAsync();
        var before = await LiveItemAsync(s, booking);
        Assert.Equal("not_billed", S(before, "payment_status"));
        Assert.False(before["paid_online"]!.GetValue<bool>());
        var (op, _) = await CreateAsync(me, patient, booking, "zaincash");
        Assert.Equal("succeeded", S((await MockPay.SucceedAsync(me, op)).Body, "status"));
        var paid = await LiveItemAsync(s, booking);
        Assert.Equal("paid", S(paid, "payment_status"));
        Assert.True(paid["paid_online"]!.GetValue<bool>());
        Assert.Equal(0, paid["deposit_amount"]!.GetValue<long>());

        // عربون: «عربون» بمبلغه، ويبقى كذلك بعد الحضور حتى يُحصّل المتبقي
        var (me2, patient2, booking2, s2) = await BookingWithClinicAsync();
        var dep = await Api.PostAsync(me2, "/api/v1/online-payments", new { purpose = "booking_deposit", patient_id = patient2, booking_id = booking2, provider = "qicard" }, Guid.NewGuid());
        Assert.True(dep.Status == 201, dep.Raw);
        var depId = Id(dep.Body, "online_payment_id");
        var amount = dep.Body!["amount"]!.GetValue<long>();
        var c = await Api.PostAsync(me2, $"/api/v1/mock-gateway/{S(dep.Body, "provider_reference")}/credentials", new { card_number = MockPay.Card, expiry = "12/30", cvv = "123" });
        Assert.Equal("paid", S((await Api.PostAsync(me2, $"/api/v1/mock-gateway/{S(dep.Body, "provider_reference")}/otp", new { otp = MockPay.Otp(c) })).Body, "status"));
        Assert.Equal("succeeded", S((await Api.PostAsync(me2, $"/api/v1/online-payments/{depId}/verify")).Body, "status"));
        Assert.Equal("deposit_paid", await BookingPaymentAsync(me2, booking2));
        var partial = await LiveItemAsync(s2, booking2);
        Assert.Equal("partial", S(partial, "payment_status"));
        Assert.Equal(amount, partial["deposit_amount"]!.GetValue<long>());
        Assert.True(partial["paid_online"]!.GetValue<bool>());
        var checkIn = await Api.PostAsync(s2.Reception, "/api/v1/queue/check-in", new { booking_id = booking2 }, Guid.NewGuid());
        Assert.True(checkIn.Status is 200 or 201, checkIn.Raw);
        Assert.Equal("partial", S(await LiveItemAsync(s2, booking2), "payment_status"));
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
