using Xunit;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class ServiceTests(TestApp app)
{
    [SkippableFact]
    public async Task Create_list_and_change_price_with_audit()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var c = app.Client(await app.AdminTokenAsync());

        var created = await Api.PostAsync(c, "/api/v1/services", new
        {
            name = "كشفية أطفال - جديد", type = "consultation", visit_kind = "new_visit",
            clinic_id = app.ClinicPediatrics, price = 25000,
        }, Guid.NewGuid());
        Assert.Equal(201, created.Status);
        Assert.True(created.Body!["is_commissionable"]!.GetValue<bool>());
        var id = created.Body!["service_id"]!.GetValue<Guid>();

        var dup = await Api.PostAsync(c, "/api/v1/services", new
        {
            name = "كشفية أطفال 2", type = "consultation", visit_kind = "new_visit", clinic_id = app.ClinicPediatrics, price = 30000,
        }, Guid.NewGuid());
        Assert.Equal(409, dup.Status);
        Assert.Equal("DUPLICATE_SERVICE", dup.Code);

        var updated = await Api.PatchAsync(c, $"/api/v1/services/{id}", new { price = 30000, reason = "تحديث الأسعار" });
        Assert.Equal(200, updated.Status);
        Assert.Equal(30000, updated.Body!["price"]!.GetValue<long>());
        Assert.Equal(1, await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE action = 'PRICE_CHANGED' AND entity_id = '{id}'"));

        var list = await Api.GetAsync(c, "/api/v1/services?type=consultation");
        Assert.Contains(list.Body!["items"]!.AsArray(), s => s!["service_id"]!.GetValue<Guid>() == id);

        Assert.Equal(200, (await Api.PatchAsync(c, $"/api/v1/services/{id}", new { is_active = false })).Status);
        var afterDeactivate = await Api.GetAsync(c, "/api/v1/services?type=consultation");
        Assert.DoesNotContain(afterDeactivate.Body!["items"]!.AsArray(), s => s!["service_id"]!.GetValue<Guid>() == id);
        var withInactive = await Api.GetAsync(c, "/api/v1/services?type=consultation&include_inactive=true");
        Assert.Contains(withInactive.Body!["items"]!.AsArray(), s => s!["service_id"]!.GetValue<Guid>() == id);
    }

    [SkippableFact]
    public async Task Validation_rules_for_types()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var c = app.Client(await app.AdminTokenAsync());

        var noVisitKind = await Api.PostAsync(c, "/api/v1/services", new { name = "كشفية", type = "consultation", clinic_id = app.ClinicInternal, price = 1000 }, Guid.NewGuid());
        Assert.Equal(400, noVisitKind.Status);
        Assert.NotNull(noVisitKind.Body!["errors"]!["visit_kind"]);

        var radiologyNoModality = await Api.PostAsync(c, "/api/v1/services", new { name = "أشعة", type = "radiology", price = 1000 }, Guid.NewGuid());
        Assert.Equal(400, radiologyNoModality.Status);
        Assert.NotNull(radiologyNoModality.Body!["errors"]!["modality"]);

        var negative = await Api.PostAsync(c, "/api/v1/services", new { name = "تحليل", type = "lab", price = -5 }, Guid.NewGuid());
        Assert.Equal(400, negative.Status);
        Assert.NotNull(negative.Body!["errors"]!["price"]);

        var labInClinic = await Api.PostAsync(c, "/api/v1/services", new { name = "تحليل", type = "lab", price = 5000, clinic_id = app.ClinicInternal }, Guid.NewGuid());
        Assert.Equal(400, labInClinic.Status);
        Assert.NotNull(labInClinic.Body!["errors"]!["clinic_id"]);

        var otherBranchClinic = await Api.PostAsync(c, "/api/v1/services", new
        {
            name = "كشفية", type = "consultation", visit_kind = "follow_up", clinic_id = app.ClinicBranch2, price = 1000,
        }, Guid.NewGuid());
        Assert.Equal(400, otherBranchClinic.Status);

        var ok = await Api.PostAsync(c, "/api/v1/services", new { name = "سونار بطن", type = "radiology", modality = "ultrasound", price = 35000 }, Guid.NewGuid());
        Assert.Equal(201, ok.Status);
        Assert.False(ok.Body!["is_commissionable"]!.GetValue<bool>());
    }

    [SkippableFact]
    public async Task Receptionist_can_read_but_not_change_prices()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (_, token) = await app.CreateStaffAsync("receptionist");
        var c = app.Client(token);
        Assert.Equal(200, (await Api.GetAsync(c, "/api/v1/services")).Status);
        var create = await Api.PostAsync(c, "/api/v1/services", new { name = "x", type = "lab", price = 1 }, Guid.NewGuid());
        Assert.Equal(403, create.Status);
    }
}

[Collection(ApiCollection.Name)]
public class DoctorTests(TestApp app)
{
    private async Task<Guid> CreateDoctorAsync(HttpClient admin)
    {
        var res = await Api.PostAsync(admin, "/api/v1/users", new
        {
            username = $"doc_{Guid.NewGuid():N}"[..14], full_name = "د. سعد الحيدري", role = "doctor",
            branch_ids = new[] { app.Branch1 }, temporary_password = "Temp-Password-123",
            doctor_profile = new { specialty = "باطنية", clinic_id = app.ClinicInternal },
        }, Guid.NewGuid());
        return res.Body!["doctor_profile"]!["doctor_id"]!.GetValue<Guid>();
    }

    [SkippableFact]
    public async Task Commission_history_and_effective_dates()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var doctorId = await CreateDoctorAsync(admin);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

        var empty = await Api.GetAsync(admin, $"/api/v1/doctors/{doctorId}/commissions");
        Assert.Equal(200, empty.Status);
        Assert.Null(empty.Body!["current"]);

        // أول نسبة يمكن أن تكون بتاريخ سابق
        var first = await Api.PostAsync(admin, $"/api/v1/doctors/{doctorId}/commissions",
            new { new_visit_rate = 70, follow_up_rate = 60, effective_from = "2026-01-01" });
        Assert.Equal(201, first.Status);

        var past = await Api.PostAsync(admin, $"/api/v1/doctors/{doctorId}/commissions",
            new { new_visit_rate = 75, follow_up_rate = 65, effective_from = today.AddDays(-1).ToString("yyyy-MM-dd") });
        Assert.Equal(422, past.Status);
        Assert.Equal("EFFECTIVE_DATE_IN_PAST", past.Code);

        var future = today.AddDays(30).ToString("yyyy-MM-dd");
        Assert.Equal(201, (await Api.PostAsync(admin, $"/api/v1/doctors/{doctorId}/commissions",
            new { new_visit_rate = 75, follow_up_rate = 65, effective_from = future })).Status);
        // تعديل نسبة مستقبلية لم تسرِ بعد = استبدال
        Assert.Equal(201, (await Api.PostAsync(admin, $"/api/v1/doctors/{doctorId}/commissions",
            new { new_visit_rate = 80, follow_up_rate = 65, effective_from = future })).Status);

        var history = await Api.GetAsync(admin, $"/api/v1/doctors/{doctorId}/commissions");
        Assert.Equal(2, history.Body!["items"]!.AsArray().Count);
        Assert.Equal(70m, history.Body!["current"]!["new_visit_rate"]!.GetValue<decimal>());
        Assert.Equal(80m, history.Body!["items"]![0]!["new_visit_rate"]!.GetValue<decimal>());

        var invalid = await Api.PostAsync(admin, $"/api/v1/doctors/{doctorId}/commissions",
            new { new_visit_rate = 120, follow_up_rate = 60.555, effective_from = future });
        Assert.Equal(400, invalid.Status);
    }

    [SkippableFact]
    public async Task Only_admin_sets_commissions_and_receptionist_cannot_read_them()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var doctorId = await CreateDoctorAsync(admin);

        var (_, accountant) = await app.CreateStaffAsync("accountant");
        Assert.Equal(200, (await Api.GetAsync(app.Client(accountant), $"/api/v1/doctors/{doctorId}/commissions")).Status);
        var set = await Api.PostAsync(app.Client(accountant), $"/api/v1/doctors/{doctorId}/commissions",
            new { new_visit_rate = 70, follow_up_rate = 60, effective_from = "2027-01-01" });
        Assert.Equal(403, set.Status);

        var (_, receptionist) = await app.CreateStaffAsync("receptionist");
        Assert.Equal(403, (await Api.GetAsync(app.Client(receptionist), $"/api/v1/doctors/{doctorId}/commissions")).Status);
        Assert.Equal(200, (await Api.GetAsync(app.Client(receptionist), "/api/v1/doctors")).Status);
    }

    [SkippableFact]
    public async Task Weekly_schedule_capacity_and_generated_sessions()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var doctorId = await CreateDoctorAsync(admin);

        var allDays = new[] { "sun", "mon", "tue", "wed", "thu", "fri", "sat" }
            .Select(d => new { weekday = d, start = "16:00", end = "21:00", slot_minutes = 10, online_quota_percent = 60 })
            .ToArray();
        var put = await Api.SendAsync(admin, HttpMethod.Put, $"/api/v1/doctors/{doctorId}/schedule", new { weekly = allDays });
        Assert.Equal(200, put.Status);
        var weekly = put.Body!["weekly"]!.AsArray();
        Assert.Equal(7, weekly.Count);
        Assert.Equal(30, weekly[0]!["capacity"]!.GetValue<int>());      // (21:00-16:00)/10
        Assert.Equal(18, weekly[0]!["online_quota"]!.GetValue<int>());  // 60%

        var sessions = await Api.GetAsync(admin, $"/api/v1/doctors/{doctorId}/sessions");
        Assert.Equal(200, sessions.Status);
        var items = sessions.Body!["items"]!.AsArray();
        Assert.Equal(7, items.Count);
        Assert.All(items, s => Assert.Equal("open", s!["status"]!.GetValue<string>()));
        // 16:00 بتوقيت بغداد = 13:00 UTC
        Assert.EndsWith("T13:00:00+00:00", items[0]!["starts_at"]!.GetValue<string>());

        // الطلب مرة ثانية لا يكرر الوجبات
        await Api.GetAsync(admin, $"/api/v1/doctors/{doctorId}/sessions");
        Assert.Equal(7, await app.ScalarAsync<long>($"SELECT count(*) FROM doctor_sessions WHERE doctor_id = '{doctorId}'"));

        // تغيير الجدول يعيد توليد الوجبات المستقبلية غير المحجوزة
        var put2 = await Api.SendAsync(admin, HttpMethod.Put, $"/api/v1/doctors/{doctorId}/schedule", new
        {
            weekly = allDays.Select(d => new { d.weekday, start = "16:00", end = "20:00", slot_minutes = 15, online_quota_percent = 50 }).ToArray(),
        });
        Assert.Equal(200, put2.Status);
        var regenerated = await Api.GetAsync(admin, $"/api/v1/doctors/{doctorId}/sessions");
        var futureItems = regenerated.Body!["items"]!.AsArray().Skip(1).ToList();
        Assert.All(futureItems, s => Assert.Equal(16, s!["capacity_total"]!.GetValue<int>()));
    }

    [SkippableFact]
    public async Task Schedule_validation_rejects_overlaps_and_bad_times()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var doctorId = await CreateDoctorAsync(admin);

        var res = await Api.SendAsync(admin, HttpMethod.Put, $"/api/v1/doctors/{doctorId}/schedule", new
        {
            weekly = new object[]
            {
                new { weekday = "sat", start = "16:00", end = "21:00", slot_minutes = 10 },
                new { weekday = "sat", start = "20:00", end = "22:00", slot_minutes = 10 },
                new { weekday = "friday", start = "9", end = "08:00", slot_minutes = 0 },
            },
        });
        Assert.Equal(400, res.Status);
        var errors = res.Body!["errors"]!;
        Assert.NotNull(errors["weekly[1]"]);
        Assert.NotNull(errors["weekly[2].weekday"]);
        Assert.NotNull(errors["weekly[2].start"]);
        Assert.NotNull(errors["weekly[2].slot_minutes"]);
    }

    [SkippableFact]
    public async Task Cancel_day_cancels_sessions_and_turns_paid_bookings_into_credit()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var doctorId = await CreateDoctorAsync(admin);
        await Api.SendAsync(admin, HttpMethod.Put, $"/api/v1/doctors/{doctorId}/schedule", new
        {
            weekly = new[] { "sun", "mon", "tue", "wed", "thu", "fri", "sat" }
                .Select(d => new { weekday = d, start = "16:00", end = "21:00", slot_minutes = 10 }).ToArray(),
        });

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var day1 = today.AddDays(2);
        var day2 = today.AddDays(3);
        var sessions = await Api.GetAsync(admin, $"/api/v1/doctors/{doctorId}/sessions?from={day1:yyyy-MM-dd}&to={day2:yyyy-MM-dd}");
        var s1 = sessions.Body!["items"]![0]!["session_id"]!.GetValue<Guid>();
        var s2 = sessions.Body!["items"]![1]!["session_id"]!.GetValue<Guid>();

        var patient = Guid.NewGuid();
        await app.ExecAsync($"""
            INSERT INTO patients (patient_id, file_number, full_name, phone_number) VALUES ('{patient}', 'T-{patient.ToString("N")[..12]}', 'مريض', '0780');
            INSERT INTO bookings (booking_id, patient_id, session_id, sequence_number, visit_kind, price, source, payment_status)
              VALUES ('{Guid.NewGuid()}', '{patient}', '{s1}', 1, 'new_visit', 25000, 'online', 'unpaid'),
                     ('{Guid.NewGuid()}', '{patient}', '{s2}', 1, 'new_visit', 25000, 'online', 'paid');
            """);

        var noReason = await Api.PostAsync(admin, $"/api/v1/doctors/{doctorId}/sessions/{day1:yyyy-MM-dd}/cancel", new { });
        Assert.Equal(400, noReason.Status);

        var ok = await Api.PostAsync(admin, $"/api/v1/doctors/{doctorId}/sessions/{day1:yyyy-MM-dd}/cancel", new { reason = "سفر الطبيب" });
        Assert.Equal(200, ok.Status);
        Assert.Equal(1, ok.Body!["affected_bookings"]!.GetValue<int>());
        Assert.Equal("cancelled", await app.ScalarAsync<string>($"SELECT status FROM doctor_sessions WHERE session_id = '{s1}'"));

        // الحجز المدفوع يُلغى ويتحول مبلغه إلى رصيد للمريض
        var paid = await Api.PostAsync(admin, $"/api/v1/doctors/{doctorId}/sessions/{day2:yyyy-MM-dd}/cancel", new { reason = "سفر" });
        Assert.Equal(200, paid.Status);
        Assert.Equal(25000L, await app.ScalarAsync<long>($"SELECT COALESCE(sum(amount), 0)::bigint FROM patient_credit_ledger WHERE patient_id = '{patient}'"));
        Assert.Equal("credited", await app.ScalarAsync<string>($"SELECT payment_status FROM bookings WHERE session_id = '{s2}'"));

        var past = await Api.PostAsync(admin, $"/api/v1/doctors/{doctorId}/sessions/{today.AddDays(-1):yyyy-MM-dd}/cancel", new { reason = "x" });
        Assert.Equal(422, past.Status);

        // الجدول الجديد لا يحذف وجبة عليها حجز
        await Api.SendAsync(admin, HttpMethod.Put, $"/api/v1/doctors/{doctorId}/schedule", new { weekly = Array.Empty<object>() });
        Assert.Equal(1, await app.ScalarAsync<long>($"SELECT count(*) FROM doctor_sessions WHERE session_id = '{s2}'"));
    }

    [SkippableFact]
    public async Task Doctors_are_scoped_to_current_branch()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var res = await Api.PostAsync(admin, "/api/v1/users", new
        {
            username = $"doc_{Guid.NewGuid():N}"[..14], full_name = "د. فرع ثاني", role = "doctor",
            branch_ids = new[] { app.Branch2 }, temporary_password = "Temp-Password-123",
            doctor_profile = new { specialty = "باطنية", clinic_id = app.ClinicBranch2 },
        }, Guid.NewGuid());
        var b2Doctor = res.Body!["doctor_profile"]!["doctor_id"]!.GetValue<Guid>();

        Assert.Equal(404, (await Api.GetAsync(admin, $"/api/v1/doctors/{b2Doctor}")).Status);
        var list = await Api.GetAsync(admin, "/api/v1/doctors");
        Assert.DoesNotContain(list.Body!["items"]!.AsArray(), d => d!["doctor_id"]!.GetValue<Guid>() == b2Doctor);
    }
}
