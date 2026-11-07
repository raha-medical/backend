using System.Text.Json.Nodes;

namespace AlRahma.Api.Tests;

/// <summary>
/// تنبيه «حان دورك» (feature/patient-alerts): رد المراجع يظهر للاستقبال «أكّد أنه قادم»، أو «لم يرد» بعد المهلة
/// (queue.call_ack_timeout_minutes، 3)، ولا يُتابع الرد لمن ليس له تطبيق.
/// </summary>
[Collection(ApiCollection.Name)]
public class PatientAlertsTests(TestApp app)
{
    private static Guid Id(JsonNode? n, string k) => n![k]!.GetValue<Guid>();
    private static string S(JsonNode? n, string k) => n![k]!.GetValue<string>();
    private static string Phone() => "0779" + Random.Shared.Next(1000000, 9999999);

    private async Task<(ClinicSetup S, Guid Branch)> NewBranchAsync()
    {
        var branch = Guid.NewGuid();
        await app.ExecAsync($"INSERT INTO branches (branch_id, code, name, city) VALUES ('{branch}', 'P{branch.ToString()[..5].ToUpperInvariant()}', 'فرع {branch.ToString()[..4]}', 'بغداد')");
        return (await new QueueTests(app).SetupAsync(branch), branch);
    }

    private async Task<(Guid Patient, string Phone)> PatientAsync(HttpClient reception, string name)
    {
        var phone = Phone();
        var r = await Api.PostAsync(reception, "/api/v1/patients", new { full_name = name, phone_number = phone, gender = "male", approx_age = 40 }, Guid.NewGuid());
        Assert.True(r.Status == 201, r.Raw);
        return (Id(r.Body, "patient_id"), phone);
    }

    private async Task<HttpClient> LoginAsync(string phone)
    {
        var otp = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/request", new { phone_number = phone });
        var ok = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = phone, code = S(otp.Body, "sandbox_code") });
        Assert.True(ok.Status == 200, ok.Raw);
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

    private static async Task<JsonNode> LiveAsync(ClinicSetup s) =>
        (await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}")).Body!;

    private static JsonNode Item(JsonNode live, Guid ticket) => live["items"]!.AsArray().First(i => Id(i, "ticket_id") == ticket)!;

    [SkippableFact]
    public async Task Called_patient_ack_shows_for_reception_and_no_reply_after_the_timeout()
    {
        Skip.If(TestApp.ServerUrl is null);
        var (s, branch) = await NewBranchAsync();
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid())).Status);
        var (withApp, phone) = await PatientAsync(s.Reception, "مراجع بالتطبيق");
        var me = await LoginAsync(phone);
        var (noApp, _) = await PatientAsync(s.Reception, "مراجع بلا تطبيق");
        var t1 = await CheckInPaidAsync(s, withApp);
        var t2 = await CheckInPaidAsync(s, noApp);

        // المهلة قابلة للتغيير لكل فرع (1 إلى 30 دقيقة)
        Assert.Equal(400, (await Api.SendAsync(s.Admin, HttpMethod.Patch, $"/api/v1/branches/{branch}/settings",
            new { values = new Dictionary<string, object> { ["queue.call_ack_timeout_minutes"] = 0 } })).Status);
        var set = await Api.SendAsync(s.Admin, HttpMethod.Patch, $"/api/v1/branches/{branch}/settings",
            new { values = new Dictionary<string, object> { ["queue.call_ack_timeout_minutes"] = 5 } });
        Assert.True(set.Status == 200, set.Raw);

        Assert.Null(Item(await LiveAsync(s), t1)["call_ack_status"]); // لم يُستدعَ بعد
        Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{t1}/call")).Status);
        var live = await LiveAsync(s);
        Assert.Equal(5, live["call_ack_timeout_minutes"]!.GetValue<int>());
        var called = Item(live, t1);
        Assert.Equal("waiting", S(called, "call_ack_status"));
        var deadline = called["call_ack_deadline"]!.GetValue<DateTimeOffset>();
        Assert.Equal(called["called_at"]!.GetValue<DateTimeOffset>().AddMinutes(5), deadline);

        // التطبيق يرى أنه مستدعى ولم يؤكد (لإظهار الشاشة الكاملة بعد العودة من الخلفية)
        var mine = (await Api.GetAsync(me, "/api/v1/me/queue-tickets")).Body!["items"]!.AsArray().Single()!;
        Assert.Equal("called", S(mine, "status"));
        Assert.Null(mine["call_acknowledged_at"]);

        // بعد المهلة بلا رد: «لم يرد»
        await app.ExecAsync($"UPDATE queue_tickets SET called_at = called_at - interval '6 minutes' WHERE ticket_id = '{t1}'");
        Assert.Equal("no_response", S(Item(await LiveAsync(s), t1), "call_ack_status"));

        // «تم، أنا قادم» يُسجَّل في الخادم ويظهر للاستقبال حتى بعد المهلة
        var ack = await Api.PostAsync(me, $"/api/v1/me/queue-tickets/{t1}/acknowledge-call");
        Assert.True(ack.Status == 200, ack.Raw);
        var after = Item(await LiveAsync(s), t1);
        Assert.Equal("acknowledged", S(after, "call_ack_status"));
        Assert.NotNull(after["call_acknowledged_at"]);

        // من ليس له تطبيق: لا متابعة للرد
        Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{t1}/start")).Status);
        Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{t1}/complete", new { })).Status);
        Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{t2}/call")).Status);
        Assert.Null(Item(await LiveAsync(s), t2)["call_ack_status"]);
    }
}
