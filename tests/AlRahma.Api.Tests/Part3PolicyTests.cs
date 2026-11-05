using System.Text.Json.Nodes;
using AlRahma.Application.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlRahma.Api.Tests;

/// <summary>
/// الجزء الثالث: من يستدعي التالي (queue.call_next_by)، الاستدعاء التلقائي، إشعارات الإرجاع والرصيد،
/// نص التذكير، تكرار تنبيه الاستدعاء، وقبول دعوة الحضور المبكر.
/// </summary>
[Collection(ApiCollection.Name)]
public class Part3PolicyTests(TestApp app)
{
    private static Guid Id(JsonNode? n, string key) => n![key]!.GetValue<Guid>();
    private static string S(JsonNode? n, string key) => n![key]!.GetValue<string>();
    private static string Phone() => "0775" + Random.Shared.Next(1000000, 9999999);

    private async Task<(Guid Patient, HttpClient Me)> AppPatientAsync(HttpClient reception, string name)
    {
        var phone = Phone();
        var r = await Api.PostAsync(reception, "/api/v1/patients", new { full_name = name, phone_number = phone, gender = "male", approx_age = 35 }, Guid.NewGuid());
        Assert.Equal(201, r.Status);
        var otp = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/request", new { phone_number = phone });
        Assert.Equal(202, otp.Status);
        var ok = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = phone, code = S(otp.Body, "sandbox_code") });
        Assert.Equal(200, ok.Status);
        return (Id(r.Body, "patient_id"), app.Client(S(ok.Body, "access_token")));
    }

    private async Task<Guid> PlainPatientAsync(HttpClient reception, string name) =>
        Id((await Api.PostAsync(reception, "/api/v1/patients", new { full_name = name, phone_number = Phone() }, Guid.NewGuid())).Body, "patient_id");

    private async Task<Guid> CheckInPaidAsync(ClinicSetup s, Guid patient)
    {
        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.True(checkIn.Status == 201, checkIn.Raw);
        await app.ExecAsync($"""
            UPDATE invoice_items SET status = 'paid' WHERE invoice_id = '{checkIn.Body!["invoice_id"]}';
            UPDATE invoices SET status = 'paid', paid_amount = net_amount WHERE invoice_id = '{checkIn.Body!["invoice_id"]}';
            """);
        return Id(checkIn.Body, "ticket_id");
    }

    private Task<long> NotificationsAsync(Guid patient, string type) => app.ScalarAsync<long>($"""
        SELECT count(*) FROM notifications n JOIN patient_account_links l ON l.patient_account_id = n.patient_account_id
         WHERE l.patient_id = '{patient}' AND n.type = '{type}'
        """);

    /// <summary>يضبط سياسة الاستدعاء للفرع ثم يعيدها للعامة بعد الاختبار حتى لا تتأثر الاختبارات الأخرى.</summary>
    private async Task WithPolicyAsync(ClinicSetup s, object values, Func<Task> body)
    {
        var url = $"/api/v1/branches/{app.Branch1}/settings";
        var set = await Api.SendAsync(s.Admin, HttpMethod.Patch, url, new { values });
        Assert.True(set.Status == 200, set.Raw);
        try { await body(); }
        finally
        {
            await Api.SendAsync(s.Admin, HttpMethod.Patch, url,
                new { values = new Dictionary<string, object?> { ["queue.call_next_by"] = null, ["queue.auto_call_next"] = null } });
        }
    }

    private static object Policy(string by, int auto = 0) =>
        new Dictionary<string, object> { ["queue.call_next_by"] = by, ["queue.auto_call_next"] = auto };

    private async Task<JsonNode> LiveAsync(ClinicSetup s) => (await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}")).Body!;

    private async Task<T> JobAsync<T>(Func<ScheduledJobs, Task<T>> run)
    {
        using var scope = app.Server.Services.CreateScope();
        return await run(scope.ServiceProvider.GetRequiredService<ScheduledJobs>());
    }

    [SkippableFact]
    public async Task Call_next_by_setting_is_listed_with_options_and_validated()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        var settings = await Api.GetAsync(s.Admin, $"/api/v1/branches/{app.Branch1}/settings");
        var row = settings.Body!["items"]!.AsArray().Single(r => S(r, "key") == "queue.call_next_by");
        Assert.Equal("both", S(row, "value_text"));
        Assert.Equal(3, row["value"]!.GetValue<long>());
        Assert.Equal(3, row["options"]!.AsArray().Count);
        Assert.Equal(400, (await Api.SendAsync(s.Admin, HttpMethod.Patch, $"/api/v1/branches/{app.Branch1}/settings",
            new { values = new Dictionary<string, object> { ["queue.call_next_by"] = "nurse" } })).Status);
        // الإعدادات الرقمية تعمل كما كانت
        Assert.Equal(400, (await Api.SendAsync(s.Admin, HttpMethod.Patch, $"/api/v1/branches/{app.Branch1}/settings",
            new { values = new Dictionary<string, object> { ["queue.turn_soon_ahead"] = 99 } })).Status);

        var policy = (await LiveAsync(s))["call_policy"]!;
        Assert.Equal("both", S(policy, "call_next_by"));
        Assert.False(policy["auto_call_next"]!.GetValue<bool>());
    }

    [SkippableFact]
    public async Task Doctor_only_policy_blocks_reception_from_calling_and_finishing()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        var t1 = await CheckInPaidAsync(s, await PlainPatientAsync(s.Reception, "أول"));
        var t2 = await CheckInPaidAsync(s, await PlainPatientAsync(s.Reception, "ثاني"));
        await WithPolicyAsync(s, Policy("doctor"), async () =>
        {
            Assert.Equal("doctor", S((await LiveAsync(s))["call_policy"], "call_next_by"));
            var denied = await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{t1}/call");
            Assert.Equal(403, denied.Status);
            Assert.Equal("CALL_NOT_ALLOWED_FOR_ROLE", denied.Code);
            Assert.Equal("CALL_NOT_ALLOWED_FOR_ROLE", (await Api.PostAsync(s.Reception, $"/api/v1/queue/call-next?doctor_id={s.DoctorId}")).Code);

            var called = await Api.PostAsync(s.Doctor, $"/api/v1/queue/call-next?doctor_id={s.DoctorId}");
            Assert.True(called.Status == 200, called.Raw);
            Assert.Equal(t1, Id(called.Body, "ticket_id"));
            Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{t1}/start")).Status);
            Assert.Equal("CALL_NOT_ALLOWED_FOR_ROLE", (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{t1}/complete", new { call_next = true })).Code);

            var done = await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{t1}/complete", new { call_next = true });
            Assert.True(done.Status == 200, done.Raw);
            Assert.Equal(t2, Id(done.Body!["next_ticket"], "ticket_id"));
            // المدير مسموح له دائماً (إعادة استدعاء)
            Assert.Equal(200, (await Api.PostAsync(s.Admin, $"/api/v1/queue/tickets/{t2}/call")).Status);
        });
    }

    [SkippableFact]
    public async Task Reception_only_policy_lets_reception_finish_and_call_next_for_a_doctor_without_the_system()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        var t1 = await CheckInPaidAsync(s, await PlainPatientAsync(s.Reception, "أول"));
        var t2 = await CheckInPaidAsync(s, await PlainPatientAsync(s.Reception, "ثاني"));
        await WithPolicyAsync(s, Policy("reception"), async () =>
        {
            Assert.Equal("CALL_NOT_ALLOWED_FOR_ROLE", (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{t1}/call")).Code);
            Assert.Equal("CALL_NOT_ALLOWED_FOR_ROLE", (await Api.PostAsync(s.Doctor, $"/api/v1/queue/call-next?doctor_id={s.DoctorId}")).Code);
            Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{t1}/call")).Status);

            // الطبيب يحفظ التشخيص لكن لا يستطيع طلب استدعاء التالي، ولا تُحفظ المعاينة عند الرفض
            Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{t1}/start")).Status);
            var consult = await Api.PostAsync(s.Doctor, "/api/v1/consultations",
                new { ticket_id = t1, diagnosis = "صداع", finish_visit = true, call_next = true }, Guid.NewGuid());
            Assert.Equal("CALL_NOT_ALLOWED_FOR_ROLE", consult.Code);
            Assert.Equal(0, await app.ScalarAsync<long>($"SELECT count(*) FROM consultations WHERE ticket_id = '{t1}'"));
            Assert.Equal("CALL_NOT_ALLOWED_FOR_ROLE", (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{t1}/complete", new { call_next = true })).Code);

            // زر الاستقبال "إنهاء واستدعاء التالي"
            var done = await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{t1}/complete", new { call_next = true });
            Assert.True(done.Status == 200, done.Raw);
            Assert.Equal("completed", S(done.Body, "status"));
            Assert.Equal(t2, Id(done.Body!["next_ticket"], "ticket_id"));
            // طبيب لا يستخدم النظام: الاستقبال ينهي المريض المستدعى مباشرة
            var direct = await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{t2}/complete");
            Assert.True(direct.Status == 200, direct.Raw);
            Assert.Equal("completed", S(direct.Body, "status"));
            Assert.NotNull(direct.Body!["started_at"]);
        });
    }

    [SkippableFact]
    public async Task Both_policy_lets_doctor_and_reception_call()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        var t1 = await CheckInPaidAsync(s, await PlainPatientAsync(s.Reception, "أول"));
        var t2 = await CheckInPaidAsync(s, await PlainPatientAsync(s.Reception, "ثاني"));
        await WithPolicyAsync(s, Policy("both"), async () =>
        {
            Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{t1}/call")).Status);
            Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{t1}/call")).Status);
            Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{t1}/start")).Status);
            var done = await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{t1}/complete", new { call_next = true });
            Assert.True(done.Status == 200, done.Raw);
            Assert.Equal(t2, Id(done.Body!["next_ticket"], "ticket_id"));
        });
    }

    [SkippableFact]
    public async Task Auto_call_next_after_save_and_finish_works_independently_and_tolerates_empty_queue()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        var t1 = await CheckInPaidAsync(s, await PlainPatientAsync(s.Reception, "أول"));
        var t2 = await CheckInPaidAsync(s, await PlainPatientAsync(s.Reception, "ثاني"));
        // التلقائي يعمل حتى لو كان الاستدعاء اليدوي للاستقبال فقط
        await WithPolicyAsync(s, Policy("reception", auto: 1), async () =>
        {
            Assert.True((await LiveAsync(s))["call_policy"]!["auto_call_next"]!.GetValue<bool>());
            Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{t1}/call")).Status);
            Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{t1}/start")).Status);
            var consult = await Api.PostAsync(s.Doctor, "/api/v1/consultations", new { ticket_id = t1, diagnosis = "زكام", finish_visit = true }, Guid.NewGuid());
            Assert.True(consult.Status == 201, consult.Raw);
            Assert.Equal(t2, Id(consult.Body!["queue"]!["next_ticket"], "ticket_id"));
            Assert.Equal("called", S(consult.Body!["queue"]!["next_ticket"], "status"));

            // آخر مريض: لا يوجد تالٍ ولا خطأ
            Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{t2}/start")).Status);
            var last = await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{t2}/complete");
            Assert.True(last.Status == 200, last.Raw);
            Assert.Equal("completed", S(last.Body, "status"));
            Assert.Null(last.Body!["next_ticket"]);
        });
    }

    [SkippableFact]
    public async Task Patient_is_notified_on_cash_refund_and_on_credit()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid())).Status);
        var (patient, _) = await AppPatientAsync(s.Reception, "مريض استرجاع");
        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        var ticket = Id(checkIn.Body, "ticket_id");
        var item = await app.ScalarAsync<Guid>($"SELECT invoice_item_id FROM invoice_items WHERE ticket_id = '{ticket}'");
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/payments",
            new { patient_id = patient, invoice_item_ids = new[] { item }, tenders = new[] { new { method = "cash", amount = 25000 } } }, Guid.NewGuid())).Status);

        Assert.Equal(201, (await Api.PostAsync(s.Reception, $"/api/v1/invoice-items/{item}/refunds",
            new { amount = 5000, method = "cash", reason = "خصم" }, Guid.NewGuid())).Status);
        Assert.Equal(1, await NotificationsAsync(patient, "payment.refunded"));
        Assert.Equal(201, (await Api.PostAsync(s.Reception, $"/api/v1/invoice-items/{item}/refunds",
            new { amount = 5000, method = "patient_credit", reason = "رصيد" }, Guid.NewGuid())).Status);
        Assert.Equal(1, await NotificationsAsync(patient, "credit.added"));
        Assert.Contains("5,000", await app.ScalarAsync<string>($"""
            SELECT n.message FROM notifications n JOIN patient_account_links l ON l.patient_account_id = n.patient_account_id
             WHERE l.patient_id = '{patient}' AND n.type = 'credit.added'
            """));
    }

    [SkippableFact]
    public async Task Booking_reminder_asks_patient_to_arrive_early_and_is_sent_once()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        var (patient, _) = await AppPatientAsync(s.Reception, "مريض تذكير");
        var session = await TodaySessionAsync(s);
        var booking = await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = patient, session_id = session }, Guid.NewGuid());
        Assert.True(booking.Status == 201, booking.Raw);
        await app.ExecAsync($"UPDATE bookings SET estimated_time = now() + interval '30 minutes' WHERE booking_id = '{Id(booking.Body, "booking_id")}'");

        await JobAsync(j => j.SendBookingRemindersAsync(CancellationToken.None));
        await JobAsync(j => j.SendBookingRemindersAsync(CancellationToken.None));
        Assert.Equal(1, await NotificationsAsync(patient, "booking.reminder"));
        var text = await app.ScalarAsync<string>($"""
            SELECT n.message FROM notifications n JOIN patient_account_links l ON l.patient_account_id = n.patient_account_id
             WHERE l.patient_id = '{patient}' AND n.type = 'booking.reminder'
            """);
        Assert.Contains("يُرجى الحضور قبل موعدك بـ 20 دقيقة", text);
    }

    [SkippableFact]
    public async Task Call_alert_repeats_every_minute_up_to_the_limit_and_stops_after_I_am_coming()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        var (patient, me) = await AppPatientAsync(s.Reception, "مريض غير منتبه");
        var ticket = await CheckInPaidAsync(s, patient);
        Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/call")).Status);

        // قبل مرور دقيقة لا يتكرر
        await JobAsync(j => j.RepeatCallAlertsAsync(CancellationToken.None));
        Assert.Equal(0, await NotificationsAsync(patient, "queue.call_repeat"));
        for (var i = 0; i < 5; i++)
        {
            await app.ExecAsync($"UPDATE queue_tickets SET last_call_alert_at = now() - interval '2 minutes' WHERE ticket_id = '{ticket}'");
            await JobAsync(j => j.RepeatCallAlertsAsync(CancellationToken.None));
        }
        // الأول عند الاستدعاء + 3 تكرارات (الافتراضي) ثم يتوقف
        Assert.Equal(3, await NotificationsAsync(patient, "queue.call_repeat"));
        Assert.Equal(4, await app.ScalarAsync<short>($"SELECT call_alerts_sent FROM queue_tickets WHERE ticket_id = '{ticket}'"));

        // إعادة الاستدعاء ثم "أنا قادم": يتوقف التكرار
        Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/call")).Status);
        Assert.Equal(200, (await Api.PostAsync(me, $"/api/v1/me/queue-tickets/{ticket}/acknowledge-call")).Status);
        await app.ExecAsync($"UPDATE queue_tickets SET last_call_alert_at = now() - interval '2 minutes' WHERE ticket_id = '{ticket}'");
        await JobAsync(j => j.RepeatCallAlertsAsync(CancellationToken.None));
        Assert.Equal(3, await NotificationsAsync(patient, "queue.call_repeat"));
        Assert.Equal(1, await app.ScalarAsync<short>($"SELECT call_alerts_sent FROM queue_tickets WHERE ticket_id = '{ticket}'"));
    }

    [SkippableFact]
    public async Task Patient_accepts_early_invite_and_reception_sees_on_the_way()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await new QueueTests(app).SetupAsync();
        var (patient, me) = await AppPatientAsync(s.Reception, "مريض مدعو");
        var session = await TodaySessionAsync(s);
        var booking = await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = patient, session_id = session }, Guid.NewGuid());
        Assert.True(booking.Status == 201, booking.Raw);
        var bookingId = Id(booking.Body, "booking_id");

        var invite = await Api.PostAsync(s.Reception, $"/api/v1/queue/sessions/{session}/fast-forward-offers", new { mode = "invite", expires_minutes = 30 }, Guid.NewGuid());
        Assert.True(invite.Status == 201, invite.Raw);
        var offer = Id(invite.Body, "offer_id");
        Assert.Equal(1, invite.Body!["sent_to"]!.GetValue<int>());

        var answer = await Api.PostAsync(me, $"/api/v1/fast-forward-offers/{offer}/respond", new { booking_id = bookingId, answer = "come_now", eta_minutes = 15 });
        Assert.True(answer.Status == 200, answer.Raw);
        Assert.True(answer.Body!["accepted"]!.GetValue<bool>());

        var item = (await LiveAsync(s))["items"]!.AsArray().Single(i => S(i, "booking_id") == bookingId.ToString());
        Assert.NotNull(item["on_the_way_at"]);
        Assert.Equal(15, item["on_the_way_eta_minutes"]!.GetValue<int>());
        Assert.Equal("waiting", S(item, "status"));
    }

    private async Task<Guid> TodaySessionAsync(ClinicSetup s)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var sessions = await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        return Id(sessions.Body!["items"]![0], "session_id");
    }
}
