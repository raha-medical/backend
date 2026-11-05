using Xunit;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class ClosureTests(TestApp app)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    [SkippableFact]
    public async Task Temporary_closure_cancels_bookings_and_reopening_restores_sessions()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var patient = await q.PatientAsync(s.Reception);
        var tomorrow = Today.AddDays(1);
        var sessions = await Api.GetAsync(s.Admin, $"/api/v1/doctors/{s.DoctorId}/sessions?from={tomorrow:yyyy-MM-dd}&to={tomorrow:yyyy-MM-dd}");
        var session = sessions.Body!["items"]![0]!["session_id"]!.GetValue<Guid>();
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = patient, session_id = session }, Guid.NewGuid())).Status);

        var url = $"/api/v1/clinics/{s.ClinicId}/closures";
        var body = new { reason = "maintenance", note = "صيانة التكييف", starts_on = $"{tomorrow:yyyy-MM-dd}", ends_on = $"{tomorrow.AddDays(2):yyyy-MM-dd}" };

        // التحقق: سبب من القائمة، وملاحظة لسبب "أخرى"، وصلاحية الإدارة
        Assert.Equal(400, (await Api.PostAsync(s.Admin, url, new { reason = "vacation" }, Guid.NewGuid())).Status);
        Assert.Equal(400, (await Api.PostAsync(s.Admin, url, new { reason = "other" }, Guid.NewGuid())).Status);
        Assert.Equal(403, (await Api.PostAsync(s.Reception, url, body, Guid.NewGuid())).Status);

        var blocked = await Api.PostAsync(s.Admin, url, body, Guid.NewGuid());
        Assert.Equal("CLINIC_HAS_BOOKINGS", blocked.Code);
        Assert.Equal(1, blocked.Body!["bookings_count"]!.GetValue<int>());

        var closed = await Api.PostAsync(s.Admin, url, new { body.reason, body.note, body.starts_on, body.ends_on, cancel_bookings = true }, Guid.NewGuid());
        Assert.Equal(201, closed.Status);
        Assert.Equal(1, closed.Body!["cancelled_bookings"]!.GetValue<int>());
        Assert.Equal("صيانة", closed.Body!["closure"]!["reason_ar"]!.GetValue<string>());
        Assert.Equal("scheduled", closed.Body!["closure"]!["status"]!.GetValue<string>());
        var closureId = closed.Body!["closure"]!["closure_id"]!.GetValue<Guid>();
        Assert.Equal("cancelled", await app.ScalarAsync<string>($"SELECT status FROM doctor_sessions WHERE session_id = '{session}'"));
        Assert.Equal("CLOSURE_OVERLAP", (await Api.PostAsync(s.Admin, url, new { reason = "travel", starts_on = $"{tomorrow:yyyy-MM-dd}" }, Guid.NewGuid())).Code);

        // العيادة تبقى مفتوحة اليوم وتظهر معها بيانات الإغلاق القادم
        var clinic = await Api.GetAsync(s.Reception, $"/api/v1/clinics/{s.ClinicId}");
        Assert.True(clinic.Body!["is_open_today"]!.GetValue<bool>());
        Assert.Equal(closureId, clinic.Body!["closure"]!["closure_id"]!.GetValue<Guid>());

        var reopened = await Api.PostAsync(s.Admin, $"{url}/{closureId}/end");
        Assert.Equal(200, reopened.Status);
        Assert.Equal("cancelled", reopened.Body!["status"]!.GetValue<string>());
        Assert.Equal("open", await app.ScalarAsync<string>($"SELECT status FROM doctor_sessions WHERE session_id = '{session}'"));
        Assert.Equal("CLOSURE_ENDED", (await Api.PostAsync(s.Admin, $"{url}/{closureId}/end")).Code);

        // إغلاق من اليوم بلا تاريخ نهاية: الاستقبال يُرفض برسالة السبب
        var now = await Api.PostAsync(s.Admin, url, new { reason = "breakdown", note = "عطل في جهاز السونار" }, Guid.NewGuid());
        Assert.Equal(201, now.Status);
        Assert.Equal("active", now.Body!["closure"]!["status"]!.GetValue<string>());
        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.True(checkIn.Code is "CLINIC_CLOSED" or "NO_ACTIVE_SESSION", checkIn.Raw);
        Assert.False((await Api.GetAsync(s.Reception, $"/api/v1/clinics/{s.ClinicId}")).Body!["is_open_today"]!.GetValue<bool>());
        var history = await Api.GetAsync(s.Reception, url);
        Assert.Equal(2, history.Body!["items"]!.AsArray().Count);
    }

    [SkippableFact]
    public async Task Lab_closure_uses_reasons_added_by_admin()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var suffix = Guid.NewGuid().ToString()[..6];
        var lab = (await Api.PostAsync(admin, $"/api/v1/branches/{app.Branch1}/clinics", new { name = $"مختبر {suffix}", department = "lab" }, Guid.NewGuid()))
            .Body!["clinic_id"]!.GetValue<Guid>();

        var reason = await Api.PostAsync(admin, "/api/v1/lookups/closure_reason", new { name_ar = $"انقطاع الكهرباء {suffix}" }, Guid.NewGuid());
        Assert.Equal(201, reason.Status);
        var code = reason.Body!["code"]!.GetValue<string>();
        var closed = await Api.PostAsync(admin, $"/api/v1/clinics/{lab}/closures", new { reason = code, starts_on = $"{Today.AddDays(5):yyyy-MM-dd}" }, Guid.NewGuid());
        Assert.Equal(201, closed.Status);
        Assert.Equal($"انقطاع الكهرباء {suffix}", closed.Body!["closure"]!["reason_ar"]!.GetValue<string>());
        Assert.Null(closed.Body!["closure"]!["ends_on"]);

        // سبب موقوف لا يُختار، والسجلات القديمة تبقى باسمه
        await Api.PatchAsync(admin, $"/api/v1/lookups/closure_reason/{reason.Body!["lookup_id"]}", new { is_active = false });
        Assert.Equal(400, (await Api.PostAsync(admin, $"/api/v1/clinics/{lab}/closures", new { reason = code, starts_on = $"{Today.AddDays(30):yyyy-MM-dd}" }, Guid.NewGuid())).Status);
        var history = await Api.GetAsync(admin, $"/api/v1/clinics/{lab}/closures");
        Assert.Equal($"انقطاع الكهرباء {suffix}", history.Body!["items"]![0]!["reason_ar"]!.GetValue<string>());
    }
}
