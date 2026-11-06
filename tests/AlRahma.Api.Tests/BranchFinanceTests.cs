using Xunit;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class BranchFinanceTests(TestApp app)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    [SkippableFact]
    public async Task Credit_received_in_one_branch_and_used_in_another_shows_in_settlements()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var patient = await new QueueTests(app).PatientAsync(admin);
        var code = "S" + Random.Shared.Next(10000, 99999);
        var b3 = (await Api.PostAsync(admin, "/api/v1/branches", new { latitude = 33.3m, longitude = 44.4m, code, name = $"فرع التسوية {code}" }, Guid.NewGuid()))
            .Body!["branch_id"]!.GetValue<Guid>();

        // عربون 10,000 قُبض في الفرع الأول، استُخدم منه 6,000 في الفرع الجديد
        await app.ExecAsync($"""
            INSERT INTO patient_credit_ledger (patient_id, branch_id, amount, kind, created_at) VALUES
              ('{patient}', '{app.Branch1}', 10000, 'unban_deposit', now() - interval '1 hour'),
              ('{patient}', '{b3}', -6000, 'applied_to_payment', now());
            """);

        var report = await Api.GetAsync(admin, $"/api/v1/reports/branch-settlements?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.Equal(200, report.Status);
        var flow = report.Body!["flows"]!.AsArray().First(f => f!["serving_branch_id"]!.GetValue<Guid>() == b3)!;
        Assert.Equal(app.Branch1, flow["holder_branch_id"]!.GetValue<Guid>());
        Assert.Equal(6000, flow["amount"]!.GetValue<long>());
        var row = report.Body!["branches"]!.AsArray().First(b => b!["branch_id"]!.GetValue<Guid>() == b3)!;
        Assert.Equal(6000, row["used_from_other_branches"]!.GetValue<long>());
        Assert.Equal(6000, row["net_settlement"]!.GetValue<long>());

        // فرع واحد فقط
        var only = await Api.GetAsync(admin, $"/api/v1/reports/branch-settlements?branch_id={b3}");
        Assert.Single(only.Body!["branches"]!.AsArray());

        // مدير فرع لا يرى فرعاً غير مسند إليه، وموظف الاستقبال لا يرى التقارير
        var (_, manager) = await app.CreateStaffAsync("branch_manager");
        Assert.Equal(403, (await Api.GetAsync(app.Client(manager), $"/api/v1/reports/branch-settlements?branch_id={b3}")).Status);
        var mine = await Api.GetAsync(app.Client(manager), "/api/v1/reports/branch-settlements");
        Assert.All(mine.Body!["branches"]!.AsArray(), b => Assert.Equal(app.Branch1, b!["branch_id"]!.GetValue<Guid>()));
        var (_, rec) = await app.CreateStaffAsync("receptionist");
        Assert.Equal(403, (await Api.GetAsync(app.Client(rec), "/api/v1/reports/branch-settlements")).Status);
        Assert.Equal(400, (await Api.GetAsync(admin, "/api/v1/reports/branch-settlements?from=2026-01-01&to=2025-01-01")).Status);
    }

    [SkippableFact]
    public async Task Shift_is_opened_on_a_collection_point_of_the_branch()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (_, rec) = await app.CreateStaffAsync("receptionist");
        var c = app.Client(rec);
        Assert.Equal(400, (await Api.PostAsync(c, "/api/v1/shifts/open", new { opening_float = 0, clinic_id = app.ClinicBranch2 }, Guid.NewGuid())).Status);
        var opened = await Api.PostAsync(c, "/api/v1/shifts/open", new { opening_float = 0, clinic_id = app.LabBranch1 }, Guid.NewGuid());
        Assert.Equal(201, opened.Status);
        Assert.Equal("lab", opened.Body!["department"]!.GetValue<string>());
        Assert.Equal(app.LabBranch1, (await Api.GetAsync(c, "/api/v1/shifts/current")).Body!["clinic_id"]!.GetValue<Guid>());

        var admin = app.Client(await app.AdminTokenAsync());
        var list = await Api.GetAsync(admin, $"/api/v1/shifts?clinic_id={app.LabBranch1}");
        Assert.Contains(list.Body!["items"]!.AsArray(), s => s!["shift_id"]!.GetValue<Guid>() == opened.Body!["shift_id"]!.GetValue<Guid>());
    }
}
