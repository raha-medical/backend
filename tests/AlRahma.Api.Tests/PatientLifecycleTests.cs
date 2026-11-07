using System.Text.Json.Nodes;

namespace AlRahma.Api.Tests;

/// <summary>
/// دورة حياة المراجع (dock/docs/review/patient-lifecycle.md): معاينة معلّقة من وجبة أخرى، «تم، أنا قادم» ليست شرطاً،
/// زر «حضر» من الاستقبال، والبوابة التجريبية تقبل أي بطاقة Luhn وأي محفظة صحيحة الشكل.
/// </summary>
[Collection(ApiCollection.Name)]
public class PatientLifecycleTests(TestApp app)
{
    private static string S(JsonNode? n, string k) => n![k]!.GetValue<string>();

    private async Task<(ClinicSetup S, Guid Ticket)> PaidCalledWalkInAsync(ClinicSetup? setup = null)
    {
        var q = new QueueTests(app);
        var s = setup ?? await q.SetupAsync();
        var patient = await q.PatientAsync(s.Reception);
        var ci = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.True(ci.Status == 201, ci.Raw);
        await app.ExecAsync($"""
            UPDATE invoice_items SET status = 'paid' WHERE invoice_id = '{ci.Body!["invoice_id"]}';
            UPDATE invoices SET status = 'paid', paid_amount = net_amount WHERE invoice_id = '{ci.Body!["invoice_id"]}';
            """);
        var ticket = ci.Body!["ticket_id"]!.GetValue<Guid>();
        Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/call")).Status);
        return (s, ticket);
    }

    private async Task<JsonNode> LiveItemAsync(ClinicSetup s, Guid ticket) =>
        (await Api.GetAsync(s.Doctor, $"/api/v1/queue/live?doctor_id={s.DoctorId}")).Body!["items"]!.AsArray().First(i => i!["ticket_id"]!.GetValue<Guid>() == ticket)!;

    [SkippableFact]
    public async Task Stale_in_room_exam_from_another_session_is_shown_and_can_be_closed()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (s, stale) = await PaidCalledWalkInAsync();
        Assert.Equal("in_room", S((await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{stale}/start")).Body, "status"));
        // المعاينة بقيت «في الغرفة» في وجبة أخرى (مثل وجبة أمس لم تُغلق)
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)).AddDays(1);
        var other = (await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={tomorrow:yyyy-MM-dd}&to={tomorrow:yyyy-MM-dd}")).Body!["items"]![0]!["session_id"]!.GetValue<Guid>();
        await app.ExecAsync($"UPDATE queue_tickets SET session_id = '{other}', ticket_number = 'Z-999' WHERE ticket_id = '{stale}'");

        var (_, next) = await PaidCalledWalkInAsync(s);
        var live = (await Api.GetAsync(s.Doctor, $"/api/v1/queue/live?doctor_id={s.DoctorId}")).Body!;
        Assert.DoesNotContain(live["items"]!.AsArray(), i => S(i, "status") == "in_room"); // الشاشة كانت تقول «لا يوجد مريض في الغرفة»
        var open = live["open_exams_elsewhere"]!.AsArray();
        Assert.Single(open);
        Assert.Equal(stale, open[0]!["ticket_id"]!.GetValue<Guid>());

        var busy = await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{next}/start");
        Assert.Equal(409, busy.Status);
        Assert.Equal("DOCTOR_BUSY", busy.Code);
        Assert.Equal(stale, busy.Body!["ticket_id"]!.GetValue<Guid>());
        Assert.Equal("Z-999", S(busy.Body, "ticket_number"));
        Assert.True(busy.Body!["other_session"]!.GetValue<bool>());

        Assert.Equal("completed", S((await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{stale}/complete", new { call_next = false, expect_return = false })).Body, "status"));
        Assert.Empty((await Api.GetAsync(s.Doctor, $"/api/v1/queue/live?doctor_id={s.DoctorId}")).Body!["open_exams_elsewhere"]!.AsArray());
        Assert.Equal("in_room", S((await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{next}/start")).Body, "status"));
    }

    [SkippableFact]
    public async Task Called_patient_enters_without_acknowledgement_and_reception_can_mark_present()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        // بلا تطبيق: لا «لم يرد» ولا مهلة رد
        var (s, ticket) = await PaidCalledWalkInAsync();
        var item = await LiveItemAsync(s, ticket);
        Assert.False(item["has_app"]!.GetValue<bool>());
        Assert.Null(item["call_ack_status"]);
        Assert.Null(item["call_ack_deadline"]);

        // «حضر» من الاستقبال (مكرر = نفس النتيجة)
        var present = await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/present");
        Assert.True(present.Status == 200, present.Raw);
        Assert.Equal("staff", S(present.Body, "call_ack_by"));
        Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/present")).Status);
        item = await LiveItemAsync(s, ticket);
        Assert.Equal("staff", S(item, "call_ack_by"));
        Assert.NotNull(item["call_acknowledged_at"]);
        Assert.Equal("staff", await app.ScalarAsync<string>($"SELECT call_ack_by FROM queue_tickets WHERE ticket_id = '{ticket}'"));
        Assert.Equal("in_room", S((await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{ticket}/start")).Body, "status"));
        // «حضر» لغير المستدعى مرفوض
        Assert.Equal("INVALID_TICKET_STATE", (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/present")).Code);
        Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{ticket}/complete", new { expect_return = false })).Status);

        // «تم، أنا قادم» ليست شرطاً: مستدعى لم يؤكد أحد حضوره يدخل مباشرة
        var (_, silent) = await PaidCalledWalkInAsync(s);
        Assert.Null((await LiveItemAsync(s, silent))["call_acknowledged_at"]);
        Assert.Equal("in_room", S((await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{silent}/start")).Body, "status"));
    }

    [SkippableFact]
    public async Task Sandbox_gateway_accepts_any_luhn_card_and_any_well_formed_wallet()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var gw = new MockGatewayTests(app);
        var (me, patient, booking) = await gw.BookingForTestsAsync();

        // بطاقة غير صحيحة (Luhn) تُرفض، وبطاقة صحيحة غير موجودة في الجدول تنجح بأي تاريخ غير منتهٍ وأي CVV
        var (opBad, bad) = await MockGatewayTests.CreateForTestsAsync(me, patient, booking, "qicard");
        Assert.Equal("CARD_NOT_FOUND", (await Api.PostAsync(me, $"/api/v1/mock-gateway/{bad}/credentials", new { card_number = "5555555555554445", expiry = "11/29", cvv = "456" })).Code);
        var expired = await Api.PostAsync(me, $"/api/v1/mock-gateway/{bad}/credentials", new { card_number = "5555555555554444", expiry = "01/20", cvv = "456" });
        Assert.Equal("CARD_EXPIRED", S(expired.Body, "failure_code"));
        Assert.Equal("failed", S((await Api.PostAsync(me, $"/api/v1/online-payments/{opBad}/verify")).Body, "status"));

        var (op, reference) = await MockGatewayTests.CreateForTestsAsync(me, patient, booking, "qicard");
        var c = await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/credentials", new { card_number = "5555 5555 5555 4444", expiry = "11/29", cvv = "456" });
        Assert.True(c.Status == 200, c.Raw);
        Assert.Equal("otp", S(c.Body, "step"));
        Assert.Equal("paid", S((await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/otp", new { otp = MockGatewayTestDataOtp.Qi })).Body, "status"));
        Assert.Equal("succeeded", S((await Api.PostAsync(me, $"/api/v1/online-payments/{op}/verify")).Body, "status"));

        // أي محفظة صحيحة الشكل تنجح، و07809999999 تبقى «غير مسجلة»
        var (me2, patient2, booking2) = await gw.BookingForTestsAsync();
        var (op2, ref2) = await MockGatewayTests.CreateForTestsAsync(me2, patient2, booking2, "zaincash");
        Assert.Equal("ACCOUNT_NOT_FOUND", (await Api.PostAsync(me2, $"/api/v1/mock-gateway/{ref2}/credentials", new { wallet_number = "07809999999", pin = MockPay.Pin })).Code);
        Assert.Equal(200, (await Api.PostAsync(me2, $"/api/v1/mock-gateway/{ref2}/credentials", new { wallet_number = "07712345678", pin = MockPay.Pin })).Status);
        Assert.Equal("paid", S((await Api.PostAsync(me2, $"/api/v1/mock-gateway/{ref2}/otp", new { otp = MockPay.Otp })).Body, "status"));
        Assert.Equal("succeeded", S((await Api.PostAsync(me2, $"/api/v1/online-payments/{op2}/verify")).Body, "status"));
    }
}

internal static class MockGatewayTestDataOtp
{
    public const string Qi = "123456";
}
