using System.Text.Json.Nodes;
using Xunit;

namespace AlRahma.Api.Tests;

/// <summary>هيكل الفرع (الترحيل 010): أوقات الدوام المهيكلة، المدراء، عدد الأقسام، والحالة الحية في شبكة الفروع.</summary>
[Collection(ApiCollection.Name)]
public class BranchStructureTests(TestApp app)
{
    private static readonly string[] Week = ["sat", "sun", "mon", "tue", "wed", "thu", "fri"];
    private static DateTime BaghdadNow => DateTime.UtcNow.AddHours(3);
    private static string TodayCode => Week[((int)BaghdadNow.DayOfWeek + 1) % 7];

    private static JsonNode Find(ApiResult list, Guid branchId) =>
        list.Body!["items"]!.AsArray().First(b => b!["branch_id"]!.GetValue<Guid>() == branchId)!;

    [SkippableFact]
    public async Task Structured_working_hours_are_validated_summarised_and_replaced()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var code = "H" + Random.Shared.Next(10000, 99999);
        var schedule = Week.Select(d => d == "fri"
            ? (object)new { weekday = d, is_closed = true }
            : new { weekday = d, shifts = new[] { new { open = "09:00", close = "14:00" }, new { open = "16:00", close = "21:00" } } }).ToArray();

        Assert.Equal(400, (await Api.PostAsync(admin, "/api/v1/branches", new { code, name = "x", latitude = 95m, longitude = 44m }, Guid.NewGuid())).Status);
        Assert.Equal(400, (await Api.PostAsync(admin, "/api/v1/branches", new { code, name = "x", latitude = 33m }, Guid.NewGuid())).Status);
        var bad = await Api.PostAsync(admin, "/api/v1/branches", new
        {
            code, name = "x", latitude = 33.3m, longitude = 44.4m,
            working_hours_schedule = new object[]
            {
                new { weekday = "sat", shifts = new[] { new { open = "14:00", close = "09:00" } } },
                new { weekday = "sun", shifts = new[] { new { open = "09:00", close = "14:00" }, new { open = "13:00", close = "18:00" } } },
                new { weekday = "xyz", shifts = new[] { new { open = "09:00", close = "14:00" } } },
                new { weekday = "mon", shifts = new[] { new { open = "9am", close = "14:00" } } },
            },
        }, Guid.NewGuid());
        Assert.Equal(400, bad.Status);
        Assert.Equal(4, bad.Body!["errors"]!.AsObject().Count);

        var created = await Api.PostAsync(admin, "/api/v1/branches", new
        {
            code, name = $"فرع الدوام {code}", city = "بغداد", phone = "07701112233", latitude = 33.31m, longitude = 44.36m,
            working_hours_schedule = schedule,
        }, Guid.NewGuid());
        Assert.Equal(201, created.Status);
        var branchId = created.Body!["branch_id"]!.GetValue<Guid>();
        var days = created.Body!["working_hours_schedule"]!.AsArray();
        Assert.Equal(Week, days.Select(d => d!["weekday"]!.GetValue<string>()).ToArray());
        Assert.True(days[6]!["is_closed"]!.GetValue<bool>());
        Assert.Equal("الجمعة", days[6]!["weekday_ar"]!.GetValue<string>());
        Assert.Equal(2, days[0]!["shifts"]!.AsArray().Count);
        Assert.Equal("16:00", days[0]!["shifts"]![1]!["open"]!.GetValue<string>());
        var summary = created.Body!["working_hours_summary"]!.GetValue<string>();
        Assert.Equal("السبت - الخميس: 09:00-14:00 و 16:00-21:00، الجمعة: مغلق", summary);
        Assert.Equal(summary, created.Body!["working_hours"]!.GetValue<string>()); // النص القديم يُولد من الجدول
        Assert.Equal(TodayCode == "fri" ? 0 : 2, created.Body!["today_hours"]!.AsArray().Count);

        // تطبيق المريض يرى الجدول وهل الفرع مفتوح الآن
        var pub = await Api.GetAsync(app.Client(), "/api/v1/public/branches");
        var mine = Find(pub, branchId);
        Assert.Equal(7, mine["working_hours_schedule"]!.AsArray().Count);
        Assert.NotNull(mine["open_now"]);

        // PATCH يستبدل الجدول كاملاً، والنص اليدوي يبقى كما أُرسل
        var patched = await Api.PatchAsync(admin, $"/api/v1/branches/{branchId}", new
        {
            working_hours = "دوام 24 ساعة",
            working_hours_schedule = Week.Select(d => new { weekday = d, shifts = new[] { new { open = "00:00", close = "23:59" } } }).ToArray(),
        });
        Assert.Equal(200, patched.Status);
        Assert.Equal("دوام 24 ساعة", patched.Body!["working_hours"]!.GetValue<string>());
        Assert.Equal("السبت - الجمعة: 00:00-23:59", patched.Body!["working_hours_summary"]!.GetValue<string>());
        Assert.Equal(7, await app.ScalarAsync<long>($"SELECT count(*) FROM branch_working_hours WHERE branch_id = '{branchId}'"));
        Assert.Equal(400, (await Api.PatchAsync(admin, $"/api/v1/branches/{branchId}", new { latitude = 33m, longitude = 200m })).Status);
        Assert.Equal(1, await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE action = 'BRANCH_UPDATED' AND entity_id = '{branchId}'"));

        // [] يلغي الجدول المهيكل
        var cleared = await Api.PatchAsync(admin, $"/api/v1/branches/{branchId}", new { working_hours_schedule = Array.Empty<object>() });
        Assert.Equal(200, cleared.Status);
        Assert.Null(cleared.Body!["working_hours_schedule"]);
        Assert.Null(cleared.Body!["working_hours_summary"]);
        Assert.Equal("open", cleared.Body!["live_status"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task Branch_grid_shows_managers_department_counts_and_live_status()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var code = "G" + Random.Shared.Next(10000, 99999);
        // اليوم الحالي مغلق، وبقية الأيام دوام كامل: الفرع "مغلق الآن" مهما كانت الساعة
        var schedule = Week.Select(d => d == TodayCode
            ? (object)new { weekday = d, is_closed = true }
            : new { weekday = d, shifts = new[] { new { open = "00:00", close = "23:59" } } }).ToArray();
        var branchId = (await Api.PostAsync(admin, "/api/v1/branches", new { latitude = 33.3m, longitude = 44.4m, code, name = $"فرع الشبكة {code}", working_hours_schedule = schedule }, Guid.NewGuid()))
            .Body!["branch_id"]!.GetValue<Guid>();
        await app.ExecAsync($"""
            INSERT INTO clinics (branch_id, name, department) VALUES
              ('{branchId}', 'عيادة 1', 'clinic'), ('{branchId}', 'عيادة 2', 'clinic'), ('{branchId}', 'مختبر', 'lab'),
              ('{branchId}', 'صيدلية موقوفة', 'pharmacy');
            UPDATE clinics SET is_active = false WHERE branch_id = '{branchId}' AND department = 'pharmacy';
            """);
        var (managerId, managerToken) = await app.CreateStaffAsync("branch_manager", branchId);

        var row = Find(await Api.GetAsync(admin, "/api/v1/branches"), branchId);
        Assert.Equal("closed", row["live_status"]!.GetValue<string>());
        Assert.Equal("مغلق الآن", row["live_status_ar"]!.GetValue<string>());
        Assert.Equal("open", row["status"]!.GetValue<string>()); // لا إغلاق مؤقت
        Assert.Empty(row["today_hours"]!.AsArray());
        var managers = row["managers"]!.AsArray();
        Assert.Single(managers);
        Assert.Equal(managerId, managers[0]!["user_id"]!.GetValue<Guid>());
        Assert.Equal("موظف branch_manager", managers[0]!["full_name"]!.GetValue<string>());
        Assert.Equal(2, row["department_counts"]!["clinic"]!.GetValue<int>());
        Assert.Equal(1, row["department_counts"]!["lab"]!.GetValue<int>());
        Assert.Equal(0, row["department_counts"]!["pharmacy"]!.GetValue<int>());
        Assert.Equal(3, row["department_counts"]!["total"]!.GetValue<int>());
        Assert.Equal(0, row["activity"]!["open_shifts"]!.GetValue<int>());
        Assert.True(row["is_assigned"]!.GetValue<bool>());

        // نشاط بعد الدوام (وردية صندوق مفتوحة): يعمل بعد الدوام
        await app.ExecAsync($"INSERT INTO shifts (shift_id, branch_id, user_id, status) VALUES ('{Guid.NewGuid()}', '{branchId}', '{managerId}', 'open')");
        row = Find(await Api.GetAsync(admin, "/api/v1/branches"), branchId);
        Assert.Equal("open_after_hours", row["live_status"]!.GetValue<string>());
        Assert.Equal(1, row["activity"]!["open_shifts"]!.GetValue<int>());

        // مدير الفرع يرى نفس التفاصيل لفرعه، وموظف فرع آخر يرى المعلومات العامة فقط ("مغلق الآن")
        var own = Find(await Api.GetAsync(app.Client(managerToken), "/api/v1/branches"), branchId);
        Assert.Equal("open_after_hours", own["live_status"]!.GetValue<string>());
        var (_, recToken) = await app.CreateStaffAsync("receptionist");
        var foreign = Find(await Api.GetAsync(app.Client(recToken), "/api/v1/branches"), branchId);
        Assert.False(foreign["is_assigned"]!.GetValue<bool>());
        Assert.Null(foreign["managers"]);
        Assert.Equal("closed", foreign["live_status"]!.GetValue<string>());
        Assert.Equal(7, foreign["working_hours_schedule"]!.AsArray().Count);

        // دوام كامل اليوم: مفتوح. إغلاق مؤقت: مغلق مؤقتاً. إيقاف: موقوف
        Assert.Equal(200, (await Api.PatchAsync(admin, $"/api/v1/branches/{branchId}",
            new { working_hours_schedule = Week.Select(d => new { weekday = d, shifts = new[] { new { open = "00:00", close = "23:59" } } }).ToArray() })).Status);
        await app.ExecAsync($"UPDATE shifts SET status = 'closed_pending_review' WHERE branch_id = '{branchId}'");
        Assert.Equal("open", Find(await Api.GetAsync(admin, "/api/v1/branches"), branchId)["live_status"]!.GetValue<string>());
        await app.ExecAsync($"INSERT INTO branch_closures (branch_id, reason, starts_on) VALUES ('{branchId}', 'maintenance', (now() AT TIME ZONE 'Asia/Baghdad')::date)");
        Assert.Equal("temporarily_closed", Find(await Api.GetAsync(admin, "/api/v1/branches"), branchId)["live_status"]!.GetValue<string>());
        Assert.Equal(200, (await Api.PatchAsync(admin, $"/api/v1/branches/{branchId}", new { is_active = false })).Status);
        Assert.Equal("inactive", Find(await Api.GetAsync(admin, "/api/v1/branches?include_inactive=true"), branchId)["live_status"]!.GetValue<string>());
    }
}
