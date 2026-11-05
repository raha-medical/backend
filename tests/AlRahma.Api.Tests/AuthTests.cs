using Xunit;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class AuthTests(TestApp app)
{
    [SkippableFact]
    public async Task Bootstrap_admin_must_change_password_before_using_other_endpoints()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        // مستخدم جديد بكلمة مرور مؤقتة
        var adminClient = app.Client(await app.AdminTokenAsync());
        var username = $"rec_{Guid.NewGuid():N}"[..16];
        var created = await Api.PostAsync(adminClient, "/api/v1/users", new
        {
            username, full_name = "علي حسن", role = "receptionist",
            branch_ids = new[] { app.Branch1 }, clinic_ids = new[] { app.ClinicInternal },
            temporary_password = "Temp-Password-123",
        }, Guid.NewGuid());
        Assert.Equal(201, created.Status);

        var login = await Api.PostAsync(app.Client(), "/api/v1/auth/login",
            new { username, password = "Temp-Password-123", branch_id = app.Branch1, device_id = "unknown-device" });
        Assert.Equal(200, login.Status);
        Assert.True(login.Body!["user"]!["must_change_password"]!.GetValue<bool>());
        Assert.Equal(900, login.Body!["access_expires_in"]!.GetValue<int>());
        Assert.StartsWith("rt_", login.Body!["refresh_token"]!.GetValue<string>());

        var token = login.Body!["access_token"]!.GetValue<string>();
        var blocked = await Api.GetAsync(app.Client(token), "/api/v1/branches");
        Assert.Equal(403, blocked.Status);
        Assert.Equal("PASSWORD_CHANGE_REQUIRED", blocked.Code);

        var me = await Api.GetAsync(app.Client(token), "/api/v1/auth/me");
        Assert.Equal(200, me.Status);
        Assert.Contains("queue.manage", me.Body!["permissions"]!.AsArray().Select(p => p!.GetValue<string>()));

        var weak = await Api.PostAsync(app.Client(token), "/api/v1/auth/change-password",
            new { current_password = "Temp-Password-123", new_password = "short" });
        Assert.Equal(422, weak.Status);
        Assert.Equal("WEAK_PASSWORD", weak.Code);

        var changed = await Api.PostAsync(app.Client(token), "/api/v1/auth/change-password",
            new { current_password = "Temp-Password-123", new_password = "New-Password-789" });
        Assert.Equal(204, changed.Status);

        var refreshed = await Api.PostAsync(app.Client(), "/api/v1/auth/refresh",
            new { refresh_token = login.Body!["refresh_token"]!.GetValue<string>() });
        Assert.Equal(200, refreshed.Status);
        var ok = await Api.GetAsync(app.Client(refreshed.Body!["access_token"]!.GetValue<string>()), "/api/v1/branches");
        Assert.Equal(200, ok.Status);
        Assert.True(ok.Body!["items"]!.AsArray().Count >= 2);
    }

    [SkippableFact]
    public async Task Wrong_password_returns_401_and_locks_after_five_attempts()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (userId, _) = await app.CreateStaffAsync("accountant");
        var username = await app.ScalarAsync<string>($"SELECT username FROM users WHERE user_id = '{userId}'");
        var c = app.Client();

        for (var i = 0; i < 5; i++)
        {
            var bad = await Api.PostAsync(c, "/api/v1/auth/login", new { username, password = "wrong-password!", branch_id = app.Branch1 });
            Assert.Equal(401, bad.Status);
            Assert.Equal("INVALID_CREDENTIALS", bad.Code);
        }

        var locked = await Api.PostAsync(c, "/api/v1/auth/login", new { username, password = "Final-Password-456", branch_id = app.Branch1 });
        Assert.Equal(423, locked.Status);
        Assert.Equal("ACCOUNT_LOCKED", locked.Code);
    }

    [SkippableFact]
    public async Task Unknown_user_and_missing_fields()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var c = app.Client();
        var unknown = await Api.PostAsync(c, "/api/v1/auth/login", new { username = "nobody", password = "whatever-123", branch_id = app.Branch1 });
        Assert.Equal(401, unknown.Status);
        Assert.Equal("INVALID_CREDENTIALS", unknown.Code);

        var missing = await Api.PostAsync(c, "/api/v1/auth/login", new { username = "x" });
        Assert.Equal(400, missing.Status);
        Assert.Equal("VALIDATION_ERROR", missing.Code);
        Assert.NotNull(missing.Body!["errors"]!["password"]);
        Assert.NotNull(missing.Body!["errors"]!["branch_id"]);
        Assert.NotNull(missing.Body!["trace_id"]);
    }

    [SkippableFact]
    public async Task Login_to_unassigned_branch_is_forbidden()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (userId, _) = await app.CreateStaffAsync("lab_tech");
        var username = await app.ScalarAsync<string>($"SELECT username FROM users WHERE user_id = '{userId}'");
        var res = await Api.PostAsync(app.Client(), "/api/v1/auth/login",
            new { username, password = "Final-Password-456", branch_id = app.Branch2 });
        Assert.Equal(403, res.Status);
        Assert.Equal("BRANCH_NOT_ASSIGNED", res.Code);
    }

    [SkippableFact]
    public async Task Refresh_rotates_and_reuse_revokes_all_sessions()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (userId, _) = await app.CreateStaffAsync("pharmacist");
        var username = await app.ScalarAsync<string>($"SELECT username FROM users WHERE user_id = '{userId}'");
        var c = app.Client();
        var login = await Api.PostAsync(c, "/api/v1/auth/login", new { username, password = "Final-Password-456", branch_id = app.Branch1 });
        var rt1 = login.Body!["refresh_token"]!.GetValue<string>();

        var r2 = await Api.PostAsync(c, "/api/v1/auth/refresh", new { refresh_token = rt1 });
        Assert.Equal(200, r2.Status);
        var rt2 = r2.Body!["refresh_token"]!.GetValue<string>();
        Assert.NotEqual(rt1, rt2);

        // إعادة استخدام القديم = إلغاء كل الجلسات
        var reuse = await Api.PostAsync(c, "/api/v1/auth/refresh", new { refresh_token = rt1 });
        Assert.Equal(401, reuse.Status);
        var afterReuse = await Api.PostAsync(c, "/api/v1/auth/refresh", new { refresh_token = rt2 });
        Assert.Equal(401, afterReuse.Status);

        var audits = await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE action = 'REFRESH_TOKEN_REUSE' AND entity_id = '{userId}'");
        Assert.Equal(1, audits);
    }

    [SkippableFact]
    public async Task Logout_revokes_the_session_refresh_token()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (userId, _) = await app.CreateStaffAsync("radiologist");
        var username = await app.ScalarAsync<string>($"SELECT username FROM users WHERE user_id = '{userId}'");
        var login = await Api.PostAsync(app.Client(), "/api/v1/auth/login", new { username, password = "Final-Password-456", branch_id = app.Branch1 });
        var token = login.Body!["access_token"]!.GetValue<string>();
        var rt = login.Body!["refresh_token"]!.GetValue<string>();

        var logout = await Api.PostAsync(app.Client(token), "/api/v1/auth/logout");
        Assert.Equal(204, logout.Status);
        var refresh = await Api.PostAsync(app.Client(), "/api/v1/auth/refresh", new { refresh_token = rt });
        Assert.Equal(401, refresh.Status);
    }

    [SkippableFact]
    public async Task Switch_branch_is_blocked_while_a_shift_is_open()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var adminClient = app.Client(await app.AdminTokenAsync());
        var (userId, token) = await app.CreateStaffAsync("accountant");
        await Api.PatchAsync(adminClient, $"/api/v1/users/{userId}", new { branch_ids = new[] { app.Branch1, app.Branch2 } });

        var switched = await Api.PostAsync(app.Client(token), "/api/v1/auth/switch-branch", new { branch_id = app.Branch2 });
        Assert.Equal(200, switched.Status);
        Assert.Equal(app.Branch2, switched.Body!["user"]!["branch_id"]!.GetValue<Guid>());

        await app.ExecAsync($"INSERT INTO shifts (shift_id, branch_id, user_id) VALUES ('{Guid.NewGuid()}', '{app.Branch2}', '{userId}')");
        var blocked = await Api.PostAsync(app.Client(switched.Body!["access_token"]!.GetValue<string>()), "/api/v1/auth/switch-branch", new { branch_id = app.Branch1 });
        Assert.Equal(409, blocked.Status);
        Assert.Equal("SHIFT_OPEN", blocked.Code);
    }

    [SkippableFact]
    public async Task Missing_or_invalid_token_returns_problem_details()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var none = await Api.GetAsync(app.Client(), "/api/v1/auth/me");
        Assert.Equal(401, none.Status);
        Assert.Equal("UNAUTHORIZED", none.Code);

        var bad = await Api.GetAsync(app.Client("not-a-jwt"), "/api/v1/auth/me");
        Assert.Equal(401, bad.Status);
    }
}
