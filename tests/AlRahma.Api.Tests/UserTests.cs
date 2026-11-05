using Xunit;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class UserTests(TestApp app)
{
    [SkippableFact]
    public async Task Create_user_is_idempotent()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var c = app.Client(await app.AdminTokenAsync());
        var key = Guid.NewGuid();
        var username = $"idem_{Guid.NewGuid():N}"[..16];
        var body = new
        {
            username, full_name = "سارة كريم", role = "lab_tech",
            branch_ids = new[] { app.Branch1 }, temporary_password = "Temp-Password-123",
        };

        var first = await Api.PostAsync(c, "/api/v1/users", body, key);
        Assert.Equal(201, first.Status);
        var second = await Api.PostAsync(c, "/api/v1/users", body, key);
        Assert.Equal(201, second.Status);
        Assert.Equal(first.Body!["user_id"]!.GetValue<Guid>(), second.Body!["user_id"]!.GetValue<Guid>());
        Assert.True(second.Response.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(1, await app.ScalarAsync<long>($"SELECT count(*) FROM users WHERE username = '{username}'"));

        var different = await Api.PostAsync(c, "/api/v1/users", body with { full_name = "اسم آخر" }, key);
        Assert.Equal(409, different.Status);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", different.Code);

        var noKey = await Api.PostAsync(c, "/api/v1/users", body);
        Assert.Equal(400, noKey.Status);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", noKey.Code);
    }

    [SkippableFact]
    public async Task Failed_request_can_be_retried_with_same_key()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var c = app.Client(await app.AdminTokenAsync());
        var key = Guid.NewGuid();
        var username = $"retry_{Guid.NewGuid():N}"[..16];
        var bad = await Api.PostAsync(c, "/api/v1/users", new
        {
            username, full_name = "x", role = "receptionist", branch_ids = new[] { app.Branch1 },
            temporary_password = "Temp-Password-123",
        }, key);
        Assert.Equal(400, bad.Status);
        Assert.NotNull(bad.Body!["errors"]!["clinic_ids"]);

        // نفس المفتاح بعد التصحيح: ليس "مستخدماً" لأن الطلب الأول فشل
        var fixedReq = await Api.PostAsync(c, "/api/v1/users", new
        {
            username, full_name = "x", role = "receptionist", branch_ids = new[] { app.Branch1 },
            clinic_ids = new[] { app.ClinicInternal }, temporary_password = "Temp-Password-123",
        }, key);
        Assert.Equal(201, fixedReq.Status);
    }

    [SkippableFact]
    public async Task Create_user_validation_and_conflicts()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var c = app.Client(await app.AdminTokenAsync());

        var invalid = await Api.PostAsync(c, "/api/v1/users", new
        {
            username = "a", role = "janitor", branch_ids = Array.Empty<Guid>(), temporary_password = "x",
        }, Guid.NewGuid());
        Assert.Equal(400, invalid.Status);
        var errors = invalid.Body!["errors"]!;
        Assert.NotNull(errors["username"]);
        Assert.NotNull(errors["full_name"]);
        Assert.NotNull(errors["role"]);
        Assert.NotNull(errors["branch_ids"]);

        var wrongClinic = await Api.PostAsync(c, "/api/v1/users", new
        {
            username = $"wc_{Guid.NewGuid():N}"[..14], full_name = "x", role = "receptionist",
            branch_ids = new[] { app.Branch1 }, clinic_ids = new[] { app.ClinicBranch2 },
            temporary_password = "Temp-Password-123",
        }, Guid.NewGuid());
        Assert.Equal(400, wrongClinic.Status);
        Assert.NotNull(wrongClinic.Body!["errors"]!["clinic_ids"]);

        var dup = await Api.PostAsync(c, "/api/v1/users", new
        {
            username = TestApp.AdminUsername, full_name = "x", role = "lab_tech",
            branch_ids = new[] { app.Branch1 }, temporary_password = "Temp-Password-123",
        }, Guid.NewGuid());
        Assert.Equal(409, dup.Status);
        Assert.Equal("USERNAME_TAKEN", dup.Code);
    }

    [SkippableFact]
    public async Task Creating_a_doctor_creates_the_doctor_profile()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var c = app.Client(await app.AdminTokenAsync());

        var noProfile = await Api.PostAsync(c, "/api/v1/users", new
        {
            username = $"doc_{Guid.NewGuid():N}"[..14], full_name = "د. سعد الحيدري", role = "doctor",
            branch_ids = new[] { app.Branch1 }, temporary_password = "Temp-Password-123",
        }, Guid.NewGuid());
        Assert.Equal(400, noProfile.Status);
        Assert.NotNull(noProfile.Body!["errors"]!["doctor_profile"]);

        var labAsClinic = await Api.PostAsync(c, "/api/v1/users", new
        {
            username = $"doc_{Guid.NewGuid():N}"[..14], full_name = "د. سعد الحيدري", role = "doctor",
            branch_ids = new[] { app.Branch1 }, temporary_password = "Temp-Password-123",
            doctor_profile = new { specialty = "باطنية", clinic_id = app.LabBranch1 },
        }, Guid.NewGuid());
        Assert.Equal(400, labAsClinic.Status);

        var ok = await Api.PostAsync(c, "/api/v1/users", new
        {
            username = $"doc_{Guid.NewGuid():N}"[..14], full_name = "د. سعد الحيدري", role = "doctor",
            branch_ids = new[] { app.Branch1 }, temporary_password = "Temp-Password-123",
            doctor_profile = new { specialty = "باطنية", clinic_id = app.ClinicInternal, default_slot_minutes = 12 },
        }, Guid.NewGuid());
        Assert.Equal(201, ok.Status);
        var profile = ok.Body!["doctor_profile"]!;
        Assert.Equal(12, profile["default_slot_minutes"]!.GetValue<int>());
        var doctorId = profile["doctor_id"]!.GetValue<Guid>();
        Assert.Equal("باطنية", await app.ScalarAsync<string>($"SELECT specialty FROM doctors WHERE doctor_id = '{doctorId}'"));
    }

    [SkippableFact]
    public async Task Receptionist_cannot_manage_users()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (_, token) = await app.CreateStaffAsync("receptionist");
        var res = await Api.GetAsync(app.Client(token), "/api/v1/users");
        Assert.Equal(403, res.Status);
        Assert.Equal("FORBIDDEN", res.Code);
    }

    [SkippableFact]
    public async Task Branch_manager_is_limited_to_own_branch_and_non_admin_roles()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (_, token) = await app.CreateStaffAsync("branch_manager");
        var c = app.Client(token);

        var admin = await Api.PostAsync(c, "/api/v1/users", new
        {
            username = $"bm_{Guid.NewGuid():N}"[..14], full_name = "x", role = "admin",
            branch_ids = new[] { app.Branch1 }, temporary_password = "Temp-Password-123",
        }, Guid.NewGuid());
        Assert.Equal(403, admin.Status);

        var otherBranch = await Api.PostAsync(c, "/api/v1/users", new
        {
            username = $"bm_{Guid.NewGuid():N}"[..14], full_name = "x", role = "lab_tech",
            branch_ids = new[] { app.Branch2 }, temporary_password = "Temp-Password-123",
        }, Guid.NewGuid());
        Assert.Equal(400, otherBranch.Status);

        var (b2UserId, _) = await app.CreateStaffAsync("lab_tech", branch: app.Branch2);
        var hidden = await Api.GetAsync(c, $"/api/v1/users/{b2UserId}");
        Assert.Equal(404, hidden.Status);

        var list = await Api.GetAsync(c, "/api/v1/users?page_size=100");
        Assert.Equal(200, list.Status);
        Assert.DoesNotContain(list.Body!["items"]!.AsArray(), u => u!["user_id"]!.GetValue<Guid>() == b2UserId);
    }

    [SkippableFact]
    public async Task Update_disable_enable_and_reset_password()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var c = app.Client(await app.AdminTokenAsync());
        var (userId, staffToken) = await app.CreateStaffAsync("receptionist");

        var updated = await Api.PatchAsync(c, $"/api/v1/users/{userId}", new
        {
            role = "accountant", clinic_ids = Array.Empty<Guid>(), extra_permissions = new[] { "discounts.grant" },
        });
        Assert.Equal(200, updated.Status);
        Assert.Equal("accountant", updated.Body!["role"]!.GetValue<string>());
        Assert.Contains("discounts.grant", updated.Body!["permissions"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal(1, await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE action = 'USER_ROLE_CHANGED' AND entity_id = '{userId}'"));

        var disable = await Api.PostAsync(c, $"/api/v1/users/{userId}/disable");
        Assert.Equal(204, disable.Status);
        var username = await app.ScalarAsync<string>($"SELECT username FROM users WHERE user_id = '{userId}'");
        var login = await Api.PostAsync(app.Client(), "/api/v1/auth/login", new { username, password = "Final-Password-456", branch_id = app.Branch1 });
        Assert.Equal(403, login.Status);
        Assert.Equal("USER_DISABLED", login.Code);

        Assert.Equal(204, (await Api.PostAsync(c, $"/api/v1/users/{userId}/enable")).Status);

        var reset = await Api.PostAsync(c, $"/api/v1/users/{userId}/reset-password");
        Assert.Equal(200, reset.Status);
        var temp = reset.Body!["temporary_password"]!.GetValue<string>();
        var relogin = await Api.PostAsync(app.Client(), "/api/v1/auth/login", new { username, password = temp, branch_id = app.Branch1 });
        Assert.Equal(200, relogin.Status);
        Assert.True(relogin.Body!["user"]!["must_change_password"]!.GetValue<bool>());
    }

    [SkippableFact]
    public async Task Admin_cannot_disable_self_and_roles_are_listed()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var token = await app.AdminTokenAsync();
        var c = app.Client(token);
        var me = await Api.GetAsync(c, "/api/v1/auth/me");
        var self = await Api.PostAsync(c, $"/api/v1/users/{me.Body!["user_id"]!.GetValue<Guid>()}/disable");
        Assert.Equal(403, self.Status);

        var roles = await Api.GetAsync(c, "/api/v1/roles");
        Assert.Equal(200, roles.Status);
        Assert.Equal(8, roles.Body!["items"]!.AsArray().Count);
    }

    [SkippableFact]
    public async Task Clinics_of_branch()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var c = app.Client(await app.AdminTokenAsync());
        var res = await Api.GetAsync(c, $"/api/v1/branches/{app.Branch1}/clinics");
        Assert.Equal(200, res.Status);
        // اختبارات أخرى تضيف عيادات للفرع، فيُتحقق من وجود العيادات الأساسية
        var ids = res.Body!["items"]!.AsArray().Select(i => i!["clinic_id"]!.GetValue<Guid>()).ToList();
        Assert.Contains(app.ClinicInternal, ids);
        Assert.Contains(app.ClinicPediatrics, ids);
        Assert.Contains(app.LabBranch1, ids);
        Assert.DoesNotContain(app.ClinicBranch2, ids);
        Assert.Equal(404, (await Api.GetAsync(c, $"/api/v1/branches/{Guid.NewGuid()}/clinics")).Status);
    }
}
