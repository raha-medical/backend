using System.Text.Json.Nodes;
using Xunit;

namespace AlRahma.Api.Tests;

/// <summary>إشعارات الطابور: اقترب دورك، تنبيه الاستدعاء المستمر و"أنا قادم"، حفظ وإنهاء مع استدعاء التالي، ودعوة الحضور المبكر.</summary>
[Collection(ApiCollection.Name)]
public class Part3Tests(TestApp app)
{
    private static Guid Id(JsonNode? n, string key) => n![key]!.GetValue<Guid>();
    private static string S(JsonNode? n, string key) => n![key]!.GetValue<string>();
    private static string Phone() => "0773" + Random.Shared.Next(1000000, 9999999);

    private async Task<(Guid Patient, string Phone)> PatientAsync(HttpClient reception, string name)
    {
        var phone = Phone();
        var r = await Api.PostAsync(reception, "/api/v1/patients", new { full_name = name, phone_number = phone, gender = "male", approx_age = 40 }, Guid.NewGuid());
        Assert.Equal(201, r.Status);
        return (Id(r.Body, "patient_id"), phone);
    }

    private async Task<HttpClient> LoginAsync(string phone)
    {
        var otp = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/request", new { phone_number = phone });
        Assert.Equal(202, otp.Status);
        var ok = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = phone, code = S(otp.Body, "sandbox_code") });
        Assert.Equal(200, ok.Status);
        return app.Client(S(ok.Body, "access_token"));
    }

    private async Task<Guid> CheckInPaidAsync(ClinicSetup s, Guid patient)
    {
        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.True(checkIn.Status == 201, checkIn.Raw);
        var ticket = Id(checkIn.Body, "ticket_id");
        var item = await app.ScalarAsync<Guid>($"SELECT invoice_item_id FROM invoice_items WHERE ticket_id = '{ticket}'");
        var paid = await Api.PostAsync(s.Reception, "/api/v1/payments",
            new { patient_id = patient, invoice_item_ids = new[] { item }, tenders = new[] { new { method = "cash", amount = 25000 } } }, Guid.NewGuid());
        Assert.True(paid.Status == 201, paid.Raw);
        return ticket;
    }

    [SkippableFact]
    public async Task Walk_in_patient_gets_turn_soon_then_continuous_call_alert_and_doctor_finishes_with_call_next()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid())).Status);
        var (first, _) = await PatientAsync(s.Reception, "المريض الأول");
        var (second, phone) = await PatientAsync(s.Reception, "مريض بلا حجز");
        var me = await LoginAsync(phone);
        var token = "fcm_" + Guid.NewGuid();
        Assert.Equal(201, (await Api.PostAsync(me, "/api/v1/me/devices", new { push_token = token, platform = "android", app = "patient" })).Status);

        var t1 = await CheckInPaidAsync(s, first);
        var t2 = await CheckInPaidAsync(s, second);

        // اقترب دورك: مرة واحدة (أمامه مريض واحد)
        Assert.Equal(1, await app.ScalarAsync<long>($"SELECT count(*) FROM notifications n JOIN patient_account_links l ON l.patient_account_id = n.patient_account_id WHERE l.patient_id = '{second}' AND n.type = 'queue.turn_soon'"));
        var mine = await Api.GetAsync(me, "/api/v1/me/queue-tickets");
        Assert.True(mine.Status == 200, mine.Raw);
        var my = mine.Body!["items"]!.AsArray().Single();
        Assert.Equal(t2, Id(my, "ticket_id"));
        Assert.Equal("walk_in", S(my, "source"));
        Assert.Equal(1, my["patients_ahead"]!.GetValue<int>());

        // لم يُستدع بعد
        Assert.Equal("TICKET_NOT_CALLED", (await Api.PostAsync(me, $"/api/v1/me/queue-tickets/{t2}/acknowledge-call")).Code);
        Assert.Equal(404, (await Api.PostAsync(me, $"/api/v1/me/queue-tickets/{t1}/acknowledge-call")).Status);

        // الطبيب: استدعاء الأول وبدء المعاينة ثم "حفظ وإنهاء" مع استدعاء التالي
        Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{t1}/call")).Status);
        Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{t1}/start")).Status);
        Assert.Equal(400, (await Api.PostAsync(s.Doctor, "/api/v1/consultations", new { ticket_id = t1, diagnosis = "x", call_next = true }, Guid.NewGuid())).Status);
        var consult = await Api.PostAsync(s.Doctor, "/api/v1/consultations",
            new { ticket_id = t1, diagnosis = "زكام", finish_visit = true, call_next = true }, Guid.NewGuid());
        Assert.True(consult.Status == 201, consult.Raw);
        Assert.Equal("completed", S(consult.Body!["queue"], "status"));
        Assert.Equal(t2, Id(consult.Body!["queue"]!["next_ticket"], "ticket_id"));
        Assert.Equal("called", S(consult.Body!["queue"]!["next_ticket"], "status"));

        // تنبيه الاستدعاء: Push عالي الأولوية بصوت مستمر يطلب التأكيد
        var data = await app.ScalarAsync<string>($"SELECT data::text FROM outbound_messages WHERE recipient = '{token}' AND data->>'type' = 'queue.called'");
        var push = JsonNode.Parse(data)!;
        Assert.Equal("queue_alarm", S(push, "sound"));
        Assert.Equal("high", S(push, "priority"));
        Assert.True(push["require_ack"]!.GetValue<bool>());
        Assert.Equal("queue_ticket", S(push, "link_type"));
        Assert.Equal(t2, Id(push, "link_id"));
        Assert.Equal(1, await app.ScalarAsync<short>($"SELECT call_alerts_sent FROM queue_tickets WHERE ticket_id = '{t2}'"));

        // "أنا قادم": يظهر عند الطبيب والاستقبال، وإعادة الضغط لا تغير الوقت
        var ack = await Api.PostAsync(me, $"/api/v1/me/queue-tickets/{t2}/acknowledge-call");
        Assert.True(ack.Status == 200, ack.Raw);
        var again = await Api.PostAsync(me, $"/api/v1/me/queue-tickets/{t2}/acknowledge-call");
        Assert.Equal(S(ack.Body, "call_acknowledged_at"), S(again.Body, "call_acknowledged_at"));
        var live = await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}");
        var item = live.Body!["items"]!.AsArray().Single(i => Id(i, "ticket_id") == t2);
        Assert.NotNull(item["call_acknowledged_at"]);
        Assert.Equal(1, item["call_alerts_sent"]!.GetValue<int>());

        // إعادة الاستدعاء تبدأ تنبيهاً جديداً
        Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{t2}/call")).Status);
        Assert.Equal(0, await app.ScalarAsync<long>($"SELECT count(*) FROM queue_tickets WHERE ticket_id = '{t2}' AND call_acknowledged_at IS NOT NULL"));
    }

    [SkippableFact]
    public async Task Reception_sends_open_early_arrival_invite()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var sessions = await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        var session = Id(sessions.Body!["items"]![0], "session_id");

        Assert.Equal(400, (await Api.PostAsync(s.Reception, $"/api/v1/queue/sessions/{session}/fast-forward-offers",
            new { gap_minutes = 10, expires_minutes = 30 }, Guid.NewGuid())).Status);
        Assert.Equal(400, (await Api.PostAsync(s.Reception, $"/api/v1/queue/sessions/{session}/fast-forward-offers",
            new { mode = "invite", max_recipients = 31 }, Guid.NewGuid())).Status);
        var invite = await Api.PostAsync(s.Reception, $"/api/v1/queue/sessions/{session}/fast-forward-offers",
            new { mode = "invite", from_time = DateTimeOffset.UtcNow, expires_minutes = 45, max_recipients = 30 }, Guid.NewGuid());
        Assert.True(invite.Status == 201, invite.Raw);
        Assert.Equal("invite", S(invite.Body, "mode"));
        // الدعوة لا تمنع دعوة ثانية ولا عرض فجوة
        Assert.Equal(201, (await Api.PostAsync(s.Reception, $"/api/v1/queue/sessions/{session}/fast-forward-offers",
            new { mode = "invite" }, Guid.NewGuid())).Status);
        Assert.Equal(201, (await Api.PostAsync(s.Reception, $"/api/v1/queue/sessions/{session}/fast-forward-offers",
            new { gap_minutes = 15 }, Guid.NewGuid())).Status);
    }
}
