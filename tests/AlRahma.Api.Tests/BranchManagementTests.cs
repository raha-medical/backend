using System.Text.Json.Nodes;
using Xunit;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class BranchManagementTests(TestApp app)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    private record BranchSetup(Guid BranchId, Guid ClinicId, Guid LabId, Guid DoctorId, Guid DoctorUserId, Guid TomorrowSession,
        HttpClient Admin, HttpClient Reception);

    /// <summary>فرع جديد كامل: عيادة بطبيب وكشفية، مختبر بخدمة، موظف استقبال، ووجبة للطبيب غداً.</summary>
    private async Task<BranchSetup> NewBranchAsync()
    {
        var admin = app.Client(await app.AdminTokenAsync());
        var code = "K" + Random.Shared.Next(10000, 99999);
        var branch = await Api.PostAsync(admin, "/api/v1/branches", new
        {
            code, name = $"فرع الكرادة {code}", city = "بغداد", address = "شارع 62", phone = "07701234567",
            latitude = 33.3045m, longitude = 44.4290m, working_hours = "السبت - الخميس، 9 صباحاً - 9 مساءً",
        }, Guid.NewGuid());
        Assert.Equal(201, branch.Status);
        var branchId = branch.Body!["branch_id"]!.GetValue<Guid>();
        Assert.Equal("open", branch.Body!["status"]!.GetValue<string>());

        var clinicId = (await Api.PostAsync(admin, $"/api/v1/branches/{branchId}/clinics", new { name = "عيادة الباطنية", department = "clinic", floor = 1 }, Guid.NewGuid()))
            .Body!["clinic_id"]!.GetValue<Guid>();
        var labId = (await Api.PostAsync(admin, $"/api/v1/branches/{branchId}/clinics", new { name = "المختبر", department = "lab" }, Guid.NewGuid()))
            .Body!["clinic_id"]!.GetValue<Guid>();
        await app.ExecAsync($"""
            INSERT INTO services (branch_id, clinic_id, name, type, visit_kind, price, is_commissionable) VALUES
              ('{branchId}', '{clinicId}', 'كشفية جديد', 'consultation', 'new_visit', 25000, true),
              ('{branchId}', '{clinicId}', 'كشفية مراجعة', 'consultation', 'follow_up', 15000, true),
              ('{branchId}', '{labId}', 'تحليل CBC', 'lab', NULL, 10000, false);
            """);
        var (doctorUser, _) = await app.CreateStaffAsync("doctor", branch: branchId, doctorProfile: new { specialty = "باطنية", clinic_id = clinicId });
        var doctorId = await app.ScalarAsync<Guid>($"SELECT doctor_id FROM doctors WHERE user_id = '{doctorUser}'");
        var (_, rec) = await app.CreateStaffAsync("receptionist", branch: branchId, clinics: [clinicId]);
        var session = Guid.NewGuid();
        await app.ExecAsync($"""
            INSERT INTO doctor_sessions (session_id, doctor_id, branch_id, clinic_id, session_date, start_time, end_time, slot_minutes,
                                         capacity_total, online_quota, status)
            VALUES ('{session}', '{doctorId}', '{branchId}', '{clinicId}', '{Today.AddDays(1):yyyy-MM-dd}', '09:00', '13:00', 10, 24, 12, 'open');
            """);
        return new BranchSetup(branchId, clinicId, labId, doctorId, doctorUser, session, admin, app.Client(rec));
    }

    private async Task<Guid> BookAsync(BranchSetup s)
    {
        var patient = await new QueueTests(app).PatientAsync(s.Reception);
        var booking = await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = patient, session_id = s.TomorrowSession }, Guid.NewGuid());
        Assert.Equal(201, booking.Status);
        return booking.Body!["booking_id"]!.GetValue<Guid>();
    }

    private static JsonNode? Find(ApiResult list, Guid branchId) =>
        list.Body!["items"]!.AsArray().FirstOrDefault(b => b!["branch_id"]!.GetValue<Guid>() == branchId);

    [SkippableFact]
    public async Task Patient_sees_all_branches_and_the_home_of_the_chosen_branch()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await NewBranchAsync();
        var anonymous = app.Client();

        var list = await Api.GetAsync(anonymous, "/api/v1/public/branches");
        Assert.Equal(200, list.Status);
        var mine = Find(list, s.BranchId)!;
        Assert.Equal("open", mine["status"]!.GetValue<string>());
        Assert.Equal(33.3045m, mine["latitude"]!.GetValue<decimal>());
        Assert.Equal(["clinic", "lab"], mine["departments"]!.AsArray().Select(d => d!.GetValue<string>()).ToArray());
        Assert.Equal(1, mine["doctors_count"]!.GetValue<int>());
        Assert.NotNull(Find(await Api.GetAsync(anonymous, "/api/v1/public/branches?city=بغداد"), s.BranchId));

        var home = await Api.GetAsync(anonymous, $"/api/v1/public/branches/{s.BranchId}");
        Assert.Equal(200, home.Status);
        Assert.Equal("السبت - الخميس، 9 صباحاً - 9 مساءً", home.Body!["branch"]!["working_hours"]!.GetValue<string>());
        var departments = home.Body!["departments"]!.AsArray();
        Assert.Equal("العيادات", departments[0]!["department_ar"]!.GetValue<string>());
        Assert.Equal("المختبر", departments[1]!["clinics"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("internal", home.Body!["specialties"]![0]!["code"]!.GetValue<string>());
        var doctor = home.Body!["doctors"]![0]!;
        Assert.Equal(s.DoctorId, doctor["doctor_id"]!.GetValue<Guid>());
        Assert.Equal(25000, doctor["new_visit_price"]!.GetValue<long>());

        Assert.Equal(404, (await Api.GetAsync(anonymous, $"/api/v1/public/branches/{Guid.NewGuid()}")).Status);
    }

    [SkippableFact]
    public async Task Branch_closure_closes_every_department_and_reopening_restores_sessions()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await NewBranchAsync();
        await BookAsync(s);
        var tomorrow = Today.AddDays(1);
        var url = $"/api/v1/branches/{s.BranchId}/closures";

        // مدير فرع آخر لا يغلق هذا الفرع، وموظف الاستقبال لا يغلق
        var (_, otherManager) = await app.CreateStaffAsync("branch_manager");
        Assert.True((await Api.PostAsync(app.Client(otherManager), url, new { reason = "maintenance" }, Guid.NewGuid())).Status is 403 or 404);
        Assert.Equal(403, (await Api.PostAsync(s.Reception, url, new { reason = "maintenance" }, Guid.NewGuid())).Status);

        var body = new { reason = "maintenance", note = "صيانة المبنى", starts_on = $"{tomorrow:yyyy-MM-dd}", ends_on = $"{tomorrow.AddDays(2):yyyy-MM-dd}" };
        var blocked = await Api.PostAsync(s.Admin, url, body, Guid.NewGuid());
        Assert.Equal("BRANCH_HAS_BOOKINGS", blocked.Code);
        Assert.Equal(1, blocked.Body!["bookings_count"]!.GetValue<int>());

        var closed = await Api.PostAsync(s.Admin, url, new { body.reason, body.note, body.starts_on, body.ends_on, cancel_bookings = true }, Guid.NewGuid());
        Assert.Equal(201, closed.Status);
        Assert.Equal(1, closed.Body!["cancelled_bookings"]!.GetValue<int>());
        Assert.Equal("scheduled", closed.Body!["closure"]!["status"]!.GetValue<string>());
        Assert.StartsWith("إغلاق الفرع - صيانة", await app.ScalarAsync<string>($"SELECT cancel_reason FROM doctor_sessions WHERE session_id = '{s.TomorrowSession}'"));
        Assert.Equal("CLOSURE_OVERLAP", (await Api.PostAsync(s.Admin, url, new { reason = "holiday", starts_on = $"{tomorrow:yyyy-MM-dd}" }, Guid.NewGuid())).Code);

        // الفرع مفتوح اليوم ويظهر معه الإغلاق القادم
        var branch = await Api.GetAsync(s.Admin, $"/api/v1/branches/{s.BranchId}");
        Assert.Equal("open", branch.Body!["status"]!.GetValue<string>());
        Assert.Equal("scheduled", branch.Body!["closure"]!["status"]!.GetValue<string>());

        // إعادة الفتح تعيد الوجبات
        var closureId = closed.Body!["closure"]!["closure_id"]!.GetValue<Guid>();
        Assert.Equal(200, (await Api.PostAsync(s.Admin, $"{url}/{closureId}/end")).Status);
        Assert.Equal("open", await app.ScalarAsync<string>($"SELECT status FROM doctor_sessions WHERE session_id = '{s.TomorrowSession}'"));

        // إغلاق من اليوم حتى إشعار آخر: الحالة "مغلق" مع السبب في كل مكان، والحجز يُرفض
        var now = await Api.PostAsync(s.Admin, url, new { reason = "opening_soon" }, Guid.NewGuid());
        Assert.Equal(201, now.Status);
        var pub = Find(await Api.GetAsync(app.Client(), "/api/v1/public/branches"), s.BranchId)!;
        Assert.Equal("closed", pub["status"]!.GetValue<string>());
        Assert.Equal("افتتاح قريباً", pub["status_reason_ar"]!.GetValue<string>());
        var home = await Api.GetAsync(app.Client(), $"/api/v1/public/branches/{s.BranchId}");
        Assert.False(home.Body!["departments"]![0]!["clinics"]![0]!["is_open_today"]!.GetValue<bool>());
        Assert.False((await Api.GetAsync(s.Reception, $"/api/v1/clinics/{s.LabId}")).Body!["is_open_today"]!.GetValue<bool>());
        var patient = await new QueueTests(app).PatientAsync(s.Reception);
        var booking = await Api.PostAsync(s.Reception, "/api/v1/bookings", new { patient_id = patient, session_id = s.TomorrowSession }, Guid.NewGuid());
        Assert.Equal("BRANCH_CLOSED", booking.Code);
        Assert.Equal("cancelled", await app.ScalarAsync<string>($"SELECT status FROM doctor_sessions WHERE session_id = '{s.TomorrowSession}'"));

        // إغلاق خاص بالعيادة داخل فترة إغلاق الفرع: إعادة فتح الفرع لا تفتح وجبات العيادة المغلقة
        var clinicClosure = await Api.PostAsync(s.Admin, $"/api/v1/clinics/{s.ClinicId}/closures",
            new { reason = "travel", starts_on = $"{tomorrow:yyyy-MM-dd}", ends_on = $"{tomorrow:yyyy-MM-dd}" }, Guid.NewGuid());
        Assert.Equal(201, clinicClosure.Status);
        var nowId = now.Body!["closure"]!["closure_id"]!.GetValue<Guid>();
        Assert.Equal(200, (await Api.PostAsync(s.Admin, $"{url}/{nowId}/end")).Status);
        Assert.Equal("cancelled", await app.ScalarAsync<string>($"SELECT status FROM doctor_sessions WHERE session_id = '{s.TomorrowSession}'"));
        Assert.StartsWith("إغلاق مؤقت - سفر", await app.ScalarAsync<string>($"SELECT cancel_reason FROM doctor_sessions WHERE session_id = '{s.TomorrowSession}'"));
        Assert.Equal(200, (await Api.PostAsync(s.Admin,
            $"/api/v1/clinics/{s.ClinicId}/closures/{clinicClosure.Body!["closure"]!["closure_id"]}/end")).Status);
        Assert.Equal("open", await app.ScalarAsync<string>($"SELECT status FROM doctor_sessions WHERE session_id = '{s.TomorrowSession}'"));

        var history = await Api.GetAsync(s.Admin, url);
        Assert.Equal(2, history.Body!["items"]!.AsArray().Count);
    }

    [SkippableFact]
    public async Task Overview_shows_what_is_inside_the_branch_and_what_is_missing()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await NewBranchAsync();
        var overview = await Api.GetAsync(s.Admin, $"/api/v1/branches/{s.BranchId}/overview");
        Assert.Equal(200, overview.Status);
        var b = overview.Body!;
        Assert.Equal("open", b["branch"]!["status"]!.GetValue<string>());
        var deps = b["departments"]!.AsArray();
        Assert.Equal(1, deps.First(d => d!["department"]!.GetValue<string>() == "lab")!["active"]!.GetValue<int>());
        Assert.Equal(0, deps.First(d => d!["department"]!.GetValue<string>() == "pharmacy")!["total"]!.GetValue<int>());
        var clinic = b["clinics"]!.AsArray().First(c => c!["clinic_id"]!.GetValue<Guid>() == s.ClinicId)!;
        Assert.Equal(2, clinic["active_services"]!.GetValue<int>());
        Assert.Equal(s.DoctorId, clinic["doctors"]![0]!["doctor_id"]!.GetValue<Guid>());
        Assert.Equal(1, b["active_doctors"]!.GetValue<int>());
        Assert.Contains(b["staff"]!.AsArray(), x => x!["role"]!.GetValue<string>() == "receptionist");
        Assert.Equal(0, b["today"]!["sessions"]!.GetValue<int>()); // وجبة الطبيب غداً
        var warnings = b["setup_warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        Assert.Contains("لم يُعيَّن مدير للفرع", warnings);
        Assert.Contains("يوجد مختبر بلا فني مختبر", warnings);

        // موظف الاستقبال لا يرى النظرة الإدارية، ومدير الفرع يراها لفرعه فقط
        Assert.Equal(403, (await Api.GetAsync(s.Reception, $"/api/v1/branches/{s.BranchId}/overview")).Status);
        var (_, manager) = await app.CreateStaffAsync("branch_manager", branch: s.BranchId);
        Assert.Equal(200, (await Api.GetAsync(app.Client(manager), $"/api/v1/branches/{s.BranchId}/overview")).Status);
        Assert.Equal(403, (await Api.GetAsync(app.Client(manager), $"/api/v1/branches/{app.Branch1}/overview")).Status);
    }

    [SkippableFact]
    public async Task Deactivating_a_doctor_or_branch_does_not_leave_bookings_hanging()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await NewBranchAsync();
        var booking = await BookAsync(s);

        var blocked = await Api.PostAsync(s.Admin, $"/api/v1/users/{s.DoctorUserId}/disable");
        Assert.Equal("DOCTOR_HAS_BOOKINGS", blocked.Code);
        var disabled = await Api.PostAsync(s.Admin, $"/api/v1/users/{s.DoctorUserId}/disable?cancel_bookings=true");
        Assert.Equal(200, disabled.Status);
        Assert.Equal(1, disabled.Body!["cancelled_bookings"]!.GetValue<int>());
        Assert.Equal("cancelled", await app.ScalarAsync<string>($"SELECT status FROM bookings WHERE booking_id = '{booking}'"));
        Assert.Equal(404, (await Api.GetAsync(app.Client(), $"/api/v1/public/doctors/{s.DoctorId}/availability")).Status);

        // إيقاف الفرع: حجوزات طبيب آخر تمنع الإيقاف إلا مع cancel_bookings
        var (doctor2, _) = await app.CreateStaffAsync("doctor", branch: s.BranchId, doctorProfile: new { specialty = "أطفال", clinic_id = s.ClinicId });
        var doctor2Id = await app.ScalarAsync<Guid>($"SELECT doctor_id FROM doctors WHERE user_id = '{doctor2}'");
        var session2 = Guid.NewGuid();
        await app.ExecAsync($"""
            INSERT INTO doctor_sessions (session_id, doctor_id, branch_id, clinic_id, session_date, start_time, end_time, slot_minutes,
                                         capacity_total, online_quota, status)
            VALUES ('{session2}', '{doctor2Id}', '{s.BranchId}', '{s.ClinicId}', '{Today.AddDays(3):yyyy-MM-dd}', '09:00', '13:00', 10, 24, 12, 'open');
            """);
        await BookAsync(s with { TomorrowSession = session2 });
        Assert.Equal("BRANCH_HAS_BOOKINGS", (await Api.PatchAsync(s.Admin, $"/api/v1/branches/{s.BranchId}", new { is_active = false })).Code);
        var off = await Api.PatchAsync(s.Admin, $"/api/v1/branches/{s.BranchId}", new { is_active = false, cancel_bookings = true });
        Assert.Equal(200, off.Status);
        Assert.Equal("inactive", off.Body!["status"]!.GetValue<string>());
        Assert.Equal(0, await app.ScalarAsync<int>($"SELECT count(*)::int FROM bookings WHERE session_id = '{session2}' AND status = 'booked'"));

        // الفرع الموقوف يختفي من تطبيق المريض
        Assert.Null(Find(await Api.GetAsync(app.Client(), "/api/v1/public/branches"), s.BranchId));
        Assert.Equal(404, (await Api.GetAsync(app.Client(), $"/api/v1/public/branches/{s.BranchId}")).Status);
        Assert.Equal(404, (await Api.GetAsync(app.Client(), $"/api/v1/public/branches/{s.BranchId}/doctors")).Status);
    }

    [SkippableFact]
    public async Task Deactivating_a_lab_deactivates_its_services()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await NewBranchAsync();
        var off = await Api.PatchAsync(s.Admin, $"/api/v1/clinics/{s.LabId}", new { is_active = false });
        Assert.Equal(200, off.Status);
        Assert.Equal(0, await app.ScalarAsync<int>($"SELECT count(*)::int FROM services WHERE clinic_id = '{s.LabId}' AND is_active"));
    }

    [SkippableFact]
    public async Task Branch_settings_override_the_global_settings()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var s = await NewBranchAsync();
        var url = $"/api/v1/branches/{s.BranchId}/settings";
        static JsonNode Setting(ApiResult r, string key) => r.Body!["items"]!.AsArray().First(x => x!["key"]!.GetValue<string>() == key)!;

        var initial = await Api.GetAsync(s.Admin, url);
        Assert.Equal(200, initial.Status);
        Assert.Equal("global", Setting(initial, "ban.no_show_limit")["source"]!.GetValue<string>());
        Assert.Equal(3, Setting(initial, "ban.no_show_limit")["value"]!.GetValue<long>());

        var patched = await Api.PatchAsync(s.Admin, url, new { values = new Dictionary<string, long?> { ["ban.no_show_limit"] = 5 } });
        Assert.Equal(200, patched.Status);
        Assert.Equal(5, Setting(patched, "ban.no_show_limit")["value"]!.GetValue<long>());
        Assert.Equal("branch", Setting(patched, "ban.no_show_limit")["source"]!.GetValue<string>());
        Assert.Equal(3, Setting(patched, "ban.no_show_limit")["global_value"]!.GetValue<long>());
        // الفرع الأول لا يتأثر
        Assert.Equal(3, Setting(await Api.GetAsync(s.Admin, $"/api/v1/branches/{app.Branch1}/settings"), "ban.no_show_limit")["value"]!.GetValue<long>());

        Assert.Equal(400, (await Api.PatchAsync(s.Admin, url, new { values = new Dictionary<string, long?> { ["ban.no_show_limit"] = 500 } })).Status);
        Assert.Equal(400, (await Api.PatchAsync(s.Admin, url, new { values = new Dictionary<string, long?> { ["unknown.key"] = 1 } })).Status);
        Assert.Equal(403, (await Api.PatchAsync(s.Reception, url, new { values = new Dictionary<string, long?> { ["ban.no_show_limit"] = 4 } })).Status);

        var reset = await Api.PatchAsync(s.Admin, url, new { values = new Dictionary<string, long?> { ["ban.no_show_limit"] = null } });
        Assert.Equal("global", Setting(reset, "ban.no_show_limit")["source"]!.GetValue<string>());

        var (_, manager) = await app.CreateStaffAsync("branch_manager", branch: s.BranchId);
        Assert.Equal(200, (await Api.PatchAsync(app.Client(manager), url, new { values = new Dictionary<string, long?> { ["booking.cancel_min_hours"] = 4 } })).Status);
        Assert.Equal(403, (await Api.GetAsync(app.Client(manager), "/api/v1/settings")).Status);
        Assert.Equal(200, (await Api.GetAsync(s.Admin, "/api/v1/settings")).Status);
    }
}
