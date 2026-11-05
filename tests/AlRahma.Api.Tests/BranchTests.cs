using Xunit;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class BranchTests(TestApp app)
{
    private static string Code() => "Z" + Random.Shared.Next(10000, 99999);

    [SkippableFact]
    public async Task Admin_creates_and_updates_branches()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var code = Code();

        var created = await Api.PostAsync(admin, "/api/v1/branches", new { code = code.ToLower(), name = "فرع المنصور", city = "بغداد" }, Guid.NewGuid());
        Assert.Equal(201, created.Status);
        Assert.Equal(code, created.Body!["code"]!.GetValue<string>());
        var id = created.Body!["branch_id"]!.GetValue<Guid>();

        Assert.Equal("DUPLICATE_BRANCH_CODE", (await Api.PostAsync(admin, "/api/v1/branches", new { code, name = "x" }, Guid.NewGuid())).Code);
        Assert.Equal(400, (await Api.PostAsync(admin, "/api/v1/branches", new { code = "1-ب", name = "x" }, Guid.NewGuid())).Status);

        var patch = await Api.PatchAsync(admin, $"/api/v1/branches/{id}", new { phone = "07700000000", is_active = false });
        Assert.Equal(200, patch.Status);
        Assert.False(patch.Body!["is_active"]!.GetValue<bool>());
        var visible = await Api.GetAsync(admin, "/api/v1/branches");
        Assert.DoesNotContain(visible.Body!["items"]!.AsArray(), b => b!["branch_id"]!.GetValue<Guid>() == id);
        var all = await Api.GetAsync(admin, "/api/v1/branches?include_inactive=true");
        Assert.Contains(all.Body!["items"]!.AsArray(), b => b!["branch_id"]!.GetValue<Guid>() == id);
        Assert.Equal("BRANCH_IN_USE", (await Api.PatchAsync(admin, $"/api/v1/branches/{app.Branch1}", new { is_active = false })).Code);

        var (_, manager) = await app.CreateStaffAsync("branch_manager");
        Assert.Equal(403, (await Api.PostAsync(app.Client(manager), "/api/v1/branches", new { code = Code(), name = "x" }, Guid.NewGuid())).Status);
    }

    [SkippableFact]
    public async Task Clinics_labs_and_radiology_are_managed_per_branch()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var (_, managerToken) = await app.CreateStaffAsync("branch_manager");
        var manager = app.Client(managerToken);
        var suffix = Guid.NewGuid().ToString()[..6];

        var clinic = await Api.PostAsync(manager, $"/api/v1/branches/{app.Branch1}/clinics", new { name = $"عيادة القلب {suffix}", department = "clinic", floor = 2 }, Guid.NewGuid());
        Assert.Equal(201, clinic.Status);
        var lab = await Api.PostAsync(manager, $"/api/v1/branches/{app.Branch1}/clinics", new { name = $"مختبر {suffix}", department = "lab" }, Guid.NewGuid());
        Assert.Equal("lab", lab.Body!["department"]!.GetValue<string>());
        var rad = await Api.PostAsync(manager, $"/api/v1/branches/{app.Branch1}/clinics", new { name = $"أشعة {suffix}", department = "radiology" }, Guid.NewGuid());
        Assert.Equal(201, rad.Status);

        Assert.Equal("DUPLICATE_CLINIC", (await Api.PostAsync(manager, $"/api/v1/branches/{app.Branch1}/clinics",
            new { name = $"مختبر {suffix}", department = "lab" }, Guid.NewGuid())).Code);
        Assert.Equal(400, (await Api.PostAsync(manager, $"/api/v1/branches/{app.Branch1}/clinics",
            new { name = "x", department = "dental" }, Guid.NewGuid())).Status);
        // مدير الفرع لا يدير فرعاً غير مسند إليه، والمدير العام يدير الكل
        Assert.Equal(403, (await Api.PostAsync(manager, $"/api/v1/branches/{app.Branch2}/clinics",
            new { name = "x", department = "clinic" }, Guid.NewGuid())).Status);
        Assert.Equal(201, (await Api.PostAsync(admin, $"/api/v1/branches/{app.Branch2}/clinics",
            new { name = $"مختبر الفرع الثاني {suffix}", department = "lab" }, Guid.NewGuid())).Status);

        var labs = await Api.GetAsync(manager, $"/api/v1/branches/{app.Branch1}/clinics?department=lab");
        Assert.All(labs.Body!["items"]!.AsArray(), c => Assert.Equal("lab", c!["department"]!.GetValue<string>()));

        // عيادة عليها طبيب: لا يتغير نوعها ولا تُوقف
        var clinicId = clinic.Body!["clinic_id"]!.GetValue<Guid>();
        await app.CreateStaffAsync("doctor", doctorProfile: new { specialty = "قلبية", clinic_id = clinicId });
        Assert.Equal("CLINIC_IN_USE", (await Api.PatchAsync(manager, $"/api/v1/clinics/{clinicId}", new { department = "lab" })).Code);
        Assert.Equal("CLINIC_HAS_DOCTORS", (await Api.PatchAsync(manager, $"/api/v1/clinics/{clinicId}", new { is_active = false })).Code);
        var renamed = await Api.PatchAsync(manager, $"/api/v1/clinics/{clinicId}", new { name = $"عيادة القلبية {suffix}", floor = 3 });
        Assert.Equal(3, renamed.Body!["floor"]!.GetValue<int>());

        var radId = rad.Body!["clinic_id"]!.GetValue<Guid>();
        Assert.False((await Api.PatchAsync(manager, $"/api/v1/clinics/{radId}", new { is_active = false })).Body!["is_active"]!.GetValue<bool>());
        var active = await Api.GetAsync(manager, $"/api/v1/branches/{app.Branch1}/clinics");
        Assert.DoesNotContain(active.Body!["items"]!.AsArray(), c => c!["clinic_id"]!.GetValue<Guid>() == radId);

        var (_, rec) = await app.CreateStaffAsync("receptionist");
        Assert.Equal(403, (await Api.PostAsync(app.Client(rec), $"/api/v1/branches/{app.Branch1}/clinics",
            new { name = "x", department = "clinic" }, Guid.NewGuid())).Status);
    }
}
