using System.Text.Json.Nodes;

namespace AlRahma.Api.Tests;

/// <summary>
/// إصلاحات الإشعارات (fix/notifications): «اقترب دورك» يوم الموعد فقط، نص إلغاء الطبيب حسب الدفع، إشعار نجاح الدفع وفشله،
/// «تأخر الطبيب»، ودور «المتابعة والإشعارات» من الترحيل 027.
/// </summary>
[Collection(ApiCollection.Name)]
public class NotificationFixesTests(TestApp app)
{
    private static Guid Id(JsonNode? n, string k) => n![k]!.GetValue<Guid>();
    private static string S(JsonNode? n, string k) => n![k]!.GetValue<string>();
    private static string Phone() => "0779" + Random.Shared.Next(1000000, 9999999);
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    private async Task<(Guid Patient, HttpClient Me)> AppPatientAsync(HttpClient reception, string name)
    {
        var phone = Phone();
        var r = await Api.PostAsync(reception, "/api/v1/patients", new { full_name = name, phone_number = phone, gender = "male", approx_age = 40 }, Guid.NewGuid());
        Assert.True(r.Status == 201, r.Raw);
        var otp = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/request", new { phone_number = phone });
        var ok = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = phone, code = S(otp.Body, "sandbox_code") });
        Assert.True(ok.Status == 200, ok.Raw);
        return (Id(r.Body, "patient_id"), app.Client(S(ok.Body, "access_token")));
    }

    private static async Task<Guid> SessionAsync(ClinicSetup s, DateOnly day) =>
        Id((await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}")).Body!["items"]![0], "session_id");

    private static async Task<Guid> BookAsync(HttpClient me, Guid patient, Guid session)
    {
        var b = await Api.PostAsync(me, "/api/v1/bookings", new { patient_id = patient, session_id = session, visit_kind = "auto" }, Guid.NewGuid());
        Assert.True(b.Status == 201, b.Raw);
        return Id(b.Body, "booking_id");
    }

    private static async Task<List<JsonNode>> NotificationsAsync(HttpClient me) =>
        (await Api.GetAsync(me, "/api/v1/me/notifications?page_size=50")).Body!["items"]!.AsArray().Select(n => n!).ToList();

    [SkippableFact]
    public async Task Turn_soon_is_not_sent_for_a_booking_on_a_later_day()
    {
        Skip.If(TestApp.ServerUrl is null);
        var s = await new QueueTests(app).SetupAsync();
        var (patient, me) = await AppPatientAsync(s.Reception, "مراجع حجز مسبق");
        await BookAsync(me, patient, await SessionAsync(s, Today.AddDays(2)));
        var all = await NotificationsAsync(me);
        Assert.Contains(all, n => S(n, "type") == "booking.confirmed");
        Assert.DoesNotContain(all, n => S(n, "type") == "queue.turn_soon");
    }

    [SkippableFact]
    public async Task Doctor_cancel_text_lists_three_options_for_paid_and_only_rebooking_for_unpaid()
    {
        Skip.If(TestApp.ServerUrl is null);
        var s = await new QueueTests(app).SetupAsync();
        var day = Today.AddDays(3);
        var session = await SessionAsync(s, day);
        var (paidPatient, paidMe) = await AppPatientAsync(s.Reception, "مراجع دافع");
        var (freePatient, freeMe) = await AppPatientAsync(s.Reception, "مراجع غير دافع");
        var paidBooking = await BookAsync(paidMe, paidPatient, session);
        var freeBooking = await BookAsync(freeMe, freePatient, session);
        var op = await Api.PostAsync(paidMe, "/api/v1/online-payments", new { purpose = "booking_full", patient_id = paidPatient, booking_id = paidBooking, provider = "zaincash" }, Guid.NewGuid());
        Assert.True(op.Status == 201, op.Raw);
        Assert.Equal("succeeded", S((await MockPay.SucceedAsync(paidMe, Id(op.Body, "online_payment_id"))).Body, "status"));

        var cancel = await Api.PostAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions/{day:yyyy-MM-dd}/cancel", new { reason = "سفر الطبيب" });
        Assert.True(cancel.Status == 200, cancel.Raw);

        var paid = (await NotificationsAsync(paidMe)).First(n => S(n, "type") == "booking.cancelled");
        var text = S(paid, "message");
        Assert.Contains("إعادة الحجز", text);
        Assert.Contains("رصيداً في محفظتك", text);
        Assert.Contains("استرجاعه إلى وسيلة الدفع", text);
        Assert.Contains("د.ع", text);
        Assert.Equal("booking_cancel_options", S(paid["link"], "type"));
        Assert.Equal(paidBooking, Id(paid["link"], "id"));

        var free = (await NotificationsAsync(freeMe)).First(n => S(n, "type") == "booking.cancelled");
        var freeText = S(free, "message");
        Assert.Contains("إعادة الحجز", freeText);
        Assert.DoesNotContain("د.ع", freeText);
        Assert.DoesNotContain("رصيد", freeText);
        Assert.DoesNotContain("المبلغ", freeText);
        Assert.Equal("booking", S(free["link"], "type"));
        Assert.Equal(freeBooking, Id(free["link"], "id"));
    }

    [SkippableFact]
    public async Task Online_payment_success_and_failure_are_saved_notifications_for_the_patient()
    {
        Skip.If(TestApp.ServerUrl is null);
        var s = await new QueueTests(app).SetupAsync();
        var session = await SessionAsync(s, Today.AddDays(1));
        var (patient, me) = await AppPatientAsync(s.Reception, "مراجع الدفع");
        var booking = await BookAsync(me, patient, session);

        var bad = await Api.PostAsync(me, "/api/v1/online-payments", new { purpose = "booking_full", patient_id = patient, booking_id = booking, provider = "zaincash" }, Guid.NewGuid());
        var badId = Id(bad.Body, "online_payment_id");
        var reference = await MockPay.ReferenceAsync(me, badId);
        await Api.PostAsync(me, $"/api/v1/mock-gateway/{reference}/credentials", new { wallet_number = MockPay.InsufficientWallet, pin = MockPay.Pin });
        Assert.Equal("failed", S((await Api.PostAsync(me, $"/api/v1/online-payments/{badId}/verify")).Body, "status"));
        Assert.Equal("failed", S((await Api.PostAsync(me, $"/api/v1/online-payments/{badId}/verify")).Body, "status")); // مرة ثانية: لا إشعار مكرر

        var good = await Api.PostAsync(me, "/api/v1/online-payments", new { purpose = "booking_full", patient_id = patient, booking_id = booking, provider = "zaincash" }, Guid.NewGuid());
        Assert.Equal("succeeded", S((await MockPay.SucceedAsync(me, Id(good.Body, "online_payment_id"))).Body, "status"));

        var all = await NotificationsAsync(me);
        var failed = Assert.Single(all, n => S(n, "type") == "online_payment.failed");
        Assert.Contains("الرصيد غير كافٍ", S(failed, "message"));
        Assert.Equal(booking, Id(failed["link"], "id"));
        var ok = Assert.Single(all, n => S(n, "type") == "online_payment.succeeded");
        Assert.Contains("زين كاش", S(ok, "message"));
    }

    [SkippableFact]
    public async Task Reception_announces_doctor_delay_and_booked_patients_are_notified()
    {
        Skip.If(TestApp.ServerUrl is null);
        var s = await new QueueTests(app).SetupAsync();
        var session = await SessionAsync(s, Today);
        var (patient, me) = await AppPatientAsync(s.Reception, "مراجع التأخير");
        await BookAsync(me, patient, session);

        Assert.Equal(400, (await Api.PostAsync(s.Reception, $"/api/v1/queue/sessions/{session}/delay", new { minutes = 500 })).Status);
        var later = await SessionAsync(s, Today.AddDays(1));
        Assert.Equal("DELAY_TODAY_ONLY", (await Api.PostAsync(s.Reception, $"/api/v1/queue/sessions/{later}/delay", new { minutes = 30 })).Code);

        var r = await Api.PostAsync(s.Reception, $"/api/v1/queue/sessions/{session}/delay", new { minutes = 30, reason = "عملية طارئة" });
        Assert.True(r.Status == 200, r.Raw);
        Assert.Equal(30, r.Body!["delay_minutes"]!.GetValue<int>());
        Assert.Equal(1, r.Body!["notified_patients"]!.GetValue<int>());
        var n = Assert.Single(await NotificationsAsync(me), x => S(x, "type") == "booking.doctor_delayed");
        Assert.Contains("يتأخر نحو 30 دقيقة", S(n, "message"));
        Assert.Contains("عملية طارئة", S(n, "message"));

        var live = await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}");
        Assert.Equal(30, live.Body!["doctor_delay_minutes"]!.GetValue<int>());

        Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/queue/sessions/{session}/doctor-arrived")).Status);
        Assert.Equal("DOCTOR_ALREADY_ARRIVED", (await Api.PostAsync(s.Reception, $"/api/v1/queue/sessions/{session}/delay", new { minutes = 10 })).Code);
    }

    [SkippableFact]
    public async Task Followup_role_comes_from_the_schema_with_support_and_branch_notifications()
    {
        Skip.If(TestApp.ServerUrl is null);
        Assert.Equal(1, await app.ScalarAsync<long>("SELECT count(*) FROM roles WHERE code = 'followup' AND is_active"));
        Assert.Equal(2, await app.ScalarAsync<long>(
            "SELECT count(*) FROM role_permissions WHERE role_code = 'followup' AND permission IN ('support.manage', 'notifications.branch_view')"));
        var (_, token) = await app.CreateStaffAsync("followup");
        var me = app.Client(token);
        Assert.Equal(200, (await Api.GetAsync(me, "/api/v1/support-requests")).Status);
        Assert.Equal(200, (await Api.GetAsync(me, "/api/v1/branch/notifications")).Status);
        Assert.Equal(403, (await Api.GetAsync(me, "/api/v1/users")).Status);
    }
}
