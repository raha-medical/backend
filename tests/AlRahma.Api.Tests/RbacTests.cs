using System.Text.Json.Nodes;
using Xunit;

namespace AlRahma.Api.Tests;

/// <summary>الأدوار والصلاحيات من قاعدة البيانات (الترحيل 009): الكتالوج، الأدوار النظامية، الأدوار المخصصة، والصلاحيات الدقيقة.</summary>
[Collection(ApiCollection.Name)]
public class RbacTests(TestApp app)
{
    private static string[] Strings(JsonNode? arr) => arr!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    private static readonly Dictionary<string, string[]> Original = new()
    {
        ["branch_manager"] = ["users.manage", "services.manage", "patients.write", "patients.unban", "queue.manage", "discounts.grant",
            "refunds.approve", "shifts.review", "deferrals.grant", "reports.finance", "expenses.manage", "roles.view", "branches.view",
            // الترحيل 012 (عيب D3): قراءة كل فرعه، بلا صلاحية كتابة جديدة
            "lab.view", "radiology.view", "pharmacy.view", "audit.read", "notifications.branch_view", "backups.view",
            // الترحيل 017 (8.6-ق1): دمج الملفات بالصلاحية لا بالدور
            "patients.merge"],
        ["doctor"] = ["clinical.write", "queue.manage", "deferrals.grant"],
        ["receptionist"] = ["queue.manage", "payments.collect", "patients.write"],
        ["accountant"] = ["shifts.review", "refunds.approve", "reports.finance", "patients.unban", "payments.collect", "audit.read", "expenses.manage"],
        ["lab_tech"] = ["lab.write"],
        ["radiologist"] = ["radiology.write"],
        ["pharmacist"] = ["pharmacy.dispense"],
    };

    private static string Code() => "qa_" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>ينشئ موظفاً بدور معين (مخصص أو نظامي) ويعيد توكنه بعد تغيير كلمة المرور.</summary>
    private async Task<(Guid UserId, string Token, JsonNode Login)> StaffWithRoleAsync(string role, Guid? branch = null)
    {
        var admin = app.Client(await app.AdminTokenAsync());
        var username = $"r_{Guid.NewGuid():N}"[..18];
        var created = await Api.PostAsync(admin, "/api/v1/users", new
        {
            username, full_name = "موظف دور مخصص", role, branch_ids = new[] { branch ?? app.Branch1 }, temporary_password = "Temp-Password-123",
            clinic_ids = role == "receptionist" ? new[] { app.ClinicInternal } : Array.Empty<Guid>(),
        }, Guid.NewGuid());
        Assert.True(created.Status == 201, created.Raw);
        var c = app.Client();
        var first = await Api.PostAsync(c, "/api/v1/auth/login", new { username, password = "Temp-Password-123", branch_id = branch ?? app.Branch1 });
        Assert.Equal(200, first.Status);
        await Api.PostAsync(app.Client(first.Body!["access_token"]!.GetValue<string>()), "/api/v1/auth/change-password",
            new { current_password = "Temp-Password-123", new_password = "Final-Password-456" });
        var login = await Api.PostAsync(c, "/api/v1/auth/login", new { username, password = "Final-Password-456", branch_id = branch ?? app.Branch1 });
        Assert.Equal(200, login.Status);
        return (created.Body!["user_id"]!.GetValue<Guid>(), login.Body!["access_token"]!.GetValue<string>(), login.Body!);
    }

    [SkippableFact]
    public async Task Catalog_groups_permissions_by_section_and_action()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var catalog = await Api.GetAsync(admin, "/api/v1/permissions/catalog");
        Assert.Equal(200, catalog.Status);
        Assert.Equal(["view", "create", "edit", "delete", "all", "special"], catalog.Body!["actions"]!.AsArray().Select(a => a!["code"]!.GetValue<string>()).ToArray());
        var sections = catalog.Body!["sections"]!.AsArray();
        foreach (var name in new[] { "queue", "patients", "billing", "shifts", "refunds", "clinical", "lab", "radiology", "pharmacy", "reports",
                     "users", "roles", "branches", "services", "devices", "settings", "audit", "expenses", "deferrals" })
            Assert.Contains(sections, s => s!["code"]!.GetValue<string>() == name);
        foreach (var section in new[] { "users", "roles", "branches", "services" })
        {
            var perms = sections.First(s => s!["code"]!.GetValue<string>() == section)!["permissions"]!.AsArray();
            foreach (var action in new[] { "view", "create", "edit", "delete" })
                Assert.Contains(perms, p => p!["action"]!.GetValue<string>() == action && p["code"]!.GetValue<string>() == $"{section}.{action}");
        }
        var users = sections.First(s => s!["code"]!.GetValue<string>() == "users")!["permissions"]!.AsArray();
        var legacy = users.First(p => p!["code"]!.GetValue<string>() == "users.manage")!;
        Assert.True(legacy["legacy"]!.GetValue<bool>());
        Assert.Equal(["users.view", "users.create", "users.edit", "users.delete"], Strings(legacy["implies"]));
        var roles = sections.First(s => s!["code"]!.GetValue<string>() == "roles")!["permissions"]!.AsArray();
        Assert.True(roles.First(p => p!["code"]!.GetValue<string>() == "roles.edit")!["admin_only"]!.GetValue<bool>());
        // كل الرموز الأصلية السبعة عشر موجودة في الكتالوج
        var all = sections.SelectMany(s => s!["permissions"]!.AsArray()).Select(p => p!["code"]!.GetValue<string>()).ToHashSet();
        foreach (var code in Original.Values.SelectMany(x => x).Append("lab.write"))
            Assert.Contains(code, all);

        var (_, managerToken) = await app.CreateStaffAsync("branch_manager");
        Assert.Equal(200, (await Api.GetAsync(app.Client(managerToken), "/api/v1/permissions/catalog")).Status);
        var (_, recToken) = await app.CreateStaffAsync("receptionist");
        Assert.Equal(403, (await Api.GetAsync(app.Client(recToken), "/api/v1/permissions/catalog")).Status);
        Assert.Equal(403, (await Api.GetAsync(app.Client(recToken), "/api/v1/roles")).Status);
    }

    [SkippableFact]
    public async Task System_roles_keep_their_original_permissions_and_admin_is_locked()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var list = await Api.GetAsync(admin, "/api/v1/roles");
        Assert.Equal(200, list.Status);
        var items = list.Body!["items"]!.AsArray();
        foreach (var (code, perms) in Original)
        {
            var role = items.First(r => r!["code"]!.GetValue<string>() == code)!;
            Assert.Equal(code, role["role"]!.GetValue<string>()); // الحقل القديم
            Assert.True(role["is_system"]!.GetValue<bool>());
            Assert.Equal(perms.Order(StringComparer.Ordinal).ToArray(), Strings(role["permissions"]));
            Assert.True(role["user_count"]!.GetValue<int>() >= 0);
        }
        var bm = items.First(r => r!["code"]!.GetValue<string>() == "branch_manager")!;
        Assert.Contains("users.delete", Strings(bm["effective_permissions"])); // users.manage يتضمنها
        Assert.Contains("devices.create", Strings(bm["effective_permissions"])); // services.manage يتضمنها
        Assert.DoesNotContain("branches.edit", Strings(bm["effective_permissions"]));
        var adminRole = items.First(r => r!["code"]!.GetValue<string>() == "admin")!;
        Assert.True(adminRole["is_locked"]!.GetValue<bool>());
        Assert.Contains("roles.delete", Strings(adminRole["effective_permissions"]));

        Assert.Equal("ROLE_LOCKED", (await Api.PatchAsync(admin, "/api/v1/roles/admin", new { permissions = new[] { "users.view" } })).Code);
        Assert.Equal("ROLE_IS_SYSTEM", (await Api.SendAsync(admin, HttpMethod.Delete, "/api/v1/roles/doctor")).Code);
        Assert.Equal("ROLE_IS_SYSTEM", (await Api.PatchAsync(admin, "/api/v1/roles/doctor", new { is_active = false })).Code);
        Assert.Equal(404, (await Api.GetAsync(admin, "/api/v1/roles/no_such_role")).Status);

        // الدخول يعيد الصلاحيات الفعلية والأقسام كما توجه الواجهة اليوم
        var (_, _, login) = await StaffWithRoleAsync("receptionist");
        var user = login["user"]!;
        Assert.Equal("reception", user["home_section"]!.GetValue<string>());
        Assert.Equal(["reception"], Strings(user["sections"]));
        Assert.True(user["is_system_role"]!.GetValue<bool>());
        Assert.Equal("موظف استقبال", user["role_name_ar"]!.GetValue<string>());
        Assert.Equal(["patients.write", "payments.collect", "queue.manage"], Strings(user["permissions"]));
    }

    [SkippableFact]
    public async Task Admin_manages_custom_roles_that_work_at_login_and_change_immediately()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var code = Code();
        var (_, managerToken) = await app.CreateStaffAsync("branch_manager");
        var manager = app.Client(managerToken);

        Assert.Equal(403, (await Api.PostAsync(manager, "/api/v1/roles", new { code, name_ar = "x", permissions = new[] { "queue.manage" } }, Guid.NewGuid())).Status);
        Assert.Equal(400, (await Api.PostAsync(admin, "/api/v1/roles", new { code = "Bad Code", name_ar = "x" }, Guid.NewGuid())).Status);
        Assert.Equal(400, (await Api.PostAsync(admin, "/api/v1/roles", new { code, name_ar = "x", permissions = new[] { "no.such" } }, Guid.NewGuid())).Status);
        Assert.Equal(400, (await Api.PostAsync(admin, "/api/v1/roles", new { code, name_ar = "x", permissions = new[] { "roles.edit" } }, Guid.NewGuid())).Status);
        Assert.Equal("DUPLICATE_ROLE", (await Api.PostAsync(admin, "/api/v1/roles", new { code = "doctor", name_ar = "طبيب 2" }, Guid.NewGuid())).Code);

        var created = await Api.PostAsync(admin, "/api/v1/roles", new
        {
            code, name_ar = $"مشرف استقبال {code}", description = "استقبال مع عرض الموظفين",
            permissions = new[] { "queue.manage", "payments.collect", "patients.write", "patients.unban", "users.view" },
        }, Guid.NewGuid());
        Assert.Equal(201, created.Status);
        Assert.False(created.Body!["is_system"]!.GetValue<bool>());
        Assert.Equal(["reception", "admin"], Strings(created.Body!["sections"]));
        Assert.Equal("reception", created.Body!["home_section"]!.GetValue<string>());
        Assert.Equal(1, await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE action = 'ROLE_CREATED' AND after->>'code' = '{code}'"));
        var managerView = await Api.GetAsync(manager, $"/api/v1/roles/{code}");
        Assert.Equal(200, managerView.Status); // مدير الفرع يرى الأدوار
        Assert.False(managerView.Body!["assignable"]!.GetValue<bool>()); // payments.collect ليست من صلاحيات مدير الفرع

        // موظف بالدور المخصص: يدخل ويعمل حسب صلاحيات الدور
        var (userId, token, login) = await StaffWithRoleAsync(code);
        var user = login["user"]!;
        Assert.Equal(code, user["role"]!.GetValue<string>());
        Assert.False(user["is_system_role"]!.GetValue<bool>());
        Assert.Contains("users.view", Strings(user["permissions"]));
        Assert.Equal("reception", user["home_section"]!.GetValue<string>());
        var me = await Api.GetAsync(app.Client(token), "/api/v1/auth/me");
        Assert.Equal(["reception", "admin"], Strings(me.Body!["sections"]));
        var c = app.Client(token);
        Assert.Equal(200, (await Api.GetAsync(c, "/api/v1/users")).Status);
        Assert.Equal(403, (await Api.PostAsync(c, "/api/v1/users", new { username = "zzz_test", full_name = "x", role = "lab_tech", branch_ids = new[] { app.Branch1 }, temporary_password = "Temp-Password-123" }, Guid.NewGuid())).Status);
        Assert.Equal(200, (await Api.GetAsync(c, "/api/v1/queue/live")).Status);

        // تعديل صلاحيات الدور يسري فوراً على التوكن الحالي
        var patched = await Api.PatchAsync(admin, $"/api/v1/roles/{code}", new { permissions = new[] { "queue.manage", "payments.collect", "patients.write" }, home_section = "reception" });
        Assert.Equal(200, patched.Status);
        Assert.Equal(1, await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE action = 'ROLE_PERMISSIONS_CHANGED' AND after->>'code' = '{code}'"));
        Assert.Equal(403, (await Api.GetAsync(c, "/api/v1/users")).Status);
        Assert.DoesNotContain("users.view", Strings((await Api.GetAsync(c, "/api/v1/auth/me")).Body!["permissions"]));

        // لا يُحذف ولا يُوقف دور عليه موظفون
        var inUse = await Api.SendAsync(admin, HttpMethod.Delete, $"/api/v1/roles/{code}");
        Assert.Equal("ROLE_IN_USE", inUse.Code);
        Assert.Equal(1, inUse.Body!["users_count"]!.GetValue<int>());
        Assert.Equal("ROLE_IN_USE", (await Api.PatchAsync(admin, $"/api/v1/roles/{code}", new { is_active = false })).Code);
        var listed = (await Api.GetAsync(admin, "/api/v1/roles")).Body!["items"]!.AsArray().First(r => r!["code"]!.GetValue<string>() == code)!;
        Assert.Equal(1, listed["user_count"]!.GetValue<int>());

        // نقل الموظف لدور نظامي ثم الإيقاف والحذف
        Assert.Equal(200, (await Api.PatchAsync(admin, $"/api/v1/users/{userId}", new { role = "lab_tech" })).Status);
        var off = await Api.PatchAsync(admin, $"/api/v1/roles/{code}", new { is_active = false });
        Assert.Equal(200, off.Status);
        Assert.DoesNotContain((await Api.GetAsync(admin, "/api/v1/roles")).Body!["items"]!.AsArray(), r => r!["code"]!.GetValue<string>() == code);
        Assert.Contains((await Api.GetAsync(admin, "/api/v1/roles?include_inactive=true")).Body!["items"]!.AsArray(), r => r!["code"]!.GetValue<string>() == code);
        Assert.Equal(400, (await Api.PatchAsync(admin, $"/api/v1/users/{userId}", new { role = code })).Status); // الدور الموقوف لا يُسند
        Assert.Equal(204, (await Api.SendAsync(admin, HttpMethod.Delete, $"/api/v1/roles/{code}")).Status);
        Assert.Equal(404, (await Api.GetAsync(admin, $"/api/v1/roles/{code}")).Status);
        Assert.Equal(1, await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE action = 'ROLE_DELETED' AND before->>'code' = '{code}'"));
        Assert.Equal(0, await app.ScalarAsync<long>($"SELECT count(*) FROM role_permissions WHERE role_code = '{code}'"));
    }

    [SkippableFact]
    public async Task Branch_manager_cannot_escalate_through_custom_roles_or_admin_only_permissions()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var (_, managerToken) = await app.CreateStaffAsync("branch_manager");
        var manager = app.Client(managerToken);
        var strong = Code();
        var weak = Code();
        Assert.Equal(201, (await Api.PostAsync(admin, "/api/v1/roles", new { code = strong, name_ar = $"دور قوي {strong}", permissions = new[] { "lab.write", "queue.manage" } }, Guid.NewGuid())).Status);
        Assert.Equal(201, (await Api.PostAsync(admin, "/api/v1/roles", new { code = weak, name_ar = $"دور محدود {weak}", permissions = new[] { "queue.manage" } }, Guid.NewGuid())).Status);
        try
        {
            object Body(string role) => new
            {
                username = $"e_{Guid.NewGuid():N}"[..16], full_name = "x", role, branch_ids = new[] { app.Branch1 }, temporary_password = "Temp-Password-123",
            };
            Assert.False((await Api.GetAsync(manager, $"/api/v1/roles/{strong}")).Body!["assignable"]!.GetValue<bool>());
            Assert.Equal(403, (await Api.PostAsync(manager, "/api/v1/users", Body(strong), Guid.NewGuid())).Status); // lab.write ليست لمدير الفرع (عنده lab.view فقط منذ الترحيل 012)
            var ok = await Api.PostAsync(manager, "/api/v1/users", Body(weak), Guid.NewGuid());
            Assert.Equal(201, ok.Status);
            var strongUser = (await Api.PostAsync(admin, "/api/v1/users", Body(strong), Guid.NewGuid())).Body!["user_id"]!.GetValue<Guid>();
            Assert.Equal(404, (await Api.PostAsync(manager, $"/api/v1/users/{strongUser}/reset-password")).Status); // لا يدير موظفاً أقوى منه

            // صلاحيات المدير العام فقط لا تُمنح كصلاحية إضافية حتى من المدير العام
            var extra = await Api.PostAsync(admin, "/api/v1/users", new
            {
                username = $"x_{Guid.NewGuid():N}"[..16], full_name = "x", role = "lab_tech", branch_ids = new[] { app.Branch1 },
                extra_permissions = new[] { "roles.edit" }, temporary_password = "Temp-Password-123",
            }, Guid.NewGuid());
            Assert.Equal(400, extra.Status);

            // الصلاحيات الدقيقة: مدير الفرع (users.manage) يحتفظ بكل عمليات المستخدمين
            var weakUser = ok.Body!["user_id"]!.GetValue<Guid>();
            Assert.Equal(200, (await Api.PatchAsync(manager, $"/api/v1/users/{weakUser}", new { full_name = "معدل" })).Status);
            Assert.Equal(204, (await Api.PostAsync(manager, $"/api/v1/users/{weakUser}/disable")).Status);
            Assert.Equal(204, (await Api.PostAsync(manager, $"/api/v1/users/{weakUser}/enable")).Status);
            await app.ExecAsync($"UPDATE users SET role = 'lab_tech' WHERE role IN ('{strong}', '{weak}')");
        }
        finally
        {
            await app.ExecAsync($"UPDATE users SET role = 'lab_tech' WHERE role IN ('{strong}', '{weak}')");
            await Api.SendAsync(admin, HttpMethod.Delete, $"/api/v1/roles/{strong}");
            await Api.SendAsync(admin, HttpMethod.Delete, $"/api/v1/roles/{weak}");
        }
    }

    [SkippableFact]
    public async Task Fine_grained_permissions_split_view_create_edit_delete()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var code = Code();
        Assert.Equal(201, (await Api.PostAsync(admin, "/api/v1/roles", new
        {
            code, name_ar = $"محرر أسعار {code}", permissions = new[] { "services.view", "services.edit", "users.view", "devices.view" },
        }, Guid.NewGuid())).Status);
        try
        {
            var (_, token, login) = await StaffWithRoleAsync(code);
            Assert.Equal(["admin"], Strings(login["user"]!["sections"]));
            var c = app.Client(token);
            var service = Guid.NewGuid();
            await app.ExecAsync($"INSERT INTO services (service_id, branch_id, clinic_id, name, type, price) VALUES ('{service}', '{app.Branch1}', '{app.ClinicInternal}', 'إجراء {code}', 'procedure', 7000)");
            Assert.Equal(200, (await Api.GetAsync(c, "/api/v1/services?include_inactive=true")).Status);
            Assert.Equal(403, (await Api.PostAsync(c, "/api/v1/services", new { name = "x", type = "procedure", price = 1000, clinic_id = app.ClinicInternal }, Guid.NewGuid())).Status);
            var price = await app.ScalarAsync<long>($"SELECT price FROM services WHERE service_id = '{service}'");
            Assert.Equal(200, (await Api.PatchAsync(c, $"/api/v1/services/{service}", new { price, reason = "اختبار" })).Status);
            Assert.Equal(403, (await Api.PatchAsync(c, $"/api/v1/services/{service}", new { is_active = false })).Status); // يتطلب services.delete
            Assert.Equal(200, (await Api.GetAsync(c, "/api/v1/devices")).Status);
            Assert.Equal(403, (await Api.PostAsync(c, "/api/v1/devices", new { kind = "reception_pc", name = "x" })).Status);
            Assert.Equal(200, (await Api.GetAsync(c, "/api/v1/users")).Status);
            Assert.Equal(200, (await Api.GetAsync(c, "/api/v1/roles")).Status); // users.view يكفي لقائمة الأدوار
            Assert.Equal(403, (await Api.PostAsync(c, $"/api/v1/users/{Guid.NewGuid()}/disable")).Status);
            Assert.Equal(403, (await Api.PostAsync(c, "/api/v1/branches", new { code = "ZZ1", name = "x" }, Guid.NewGuid())).Status);
            Assert.Equal(403, (await Api.GetAsync(c, "/api/v1/queue/live")).Status);
        }
        finally
        {
            await app.ExecAsync($"UPDATE users SET role = 'lab_tech' WHERE role = '{code}'");
            await Api.SendAsync(admin, HttpMethod.Delete, $"/api/v1/roles/{code}");
        }
    }

    [SkippableFact]
    public async Task Editing_a_system_role_changes_its_users_and_branches_edit_is_scoped_to_assigned_branches()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var (_, pharmacistToken) = await app.CreateStaffAsync("pharmacist");
        var ph = app.Client(pharmacistToken);
        Assert.Equal(403, (await Api.GetAsync(ph, "/api/v1/reports/no-shows")).Status);
        try
        {
            Assert.Equal(200, (await Api.PatchAsync(admin, "/api/v1/roles/pharmacist",
                new { name_ar = "صيدلي أول", permissions = new[] { "pharmacy.dispense", "reports.finance" } })).Status);
            Assert.Equal(200, (await Api.GetAsync(ph, "/api/v1/reports/no-shows")).Status);
            Assert.Equal("صيدلي أول", (await Api.GetAsync(ph, "/api/v1/auth/me")).Body!["role_name_ar"]!.GetValue<string>());
        }
        finally
        {
            Assert.Equal(200, (await Api.PatchAsync(admin, "/api/v1/roles/pharmacist", new { name_ar = "صيدلي", permissions = new[] { "pharmacy.dispense" } })).Status);
        }
        Assert.Equal(403, (await Api.GetAsync(ph, "/api/v1/reports/no-shows")).Status);

        // branches.edit لدور مخصص: يعدّل فرعه المسند إليه فقط ولا يوقفه
        var code = Code();
        Assert.Equal(201, (await Api.PostAsync(admin, "/api/v1/roles", new { code, name_ar = $"منسق فرع {code}", permissions = new[] { "branches.edit", "branches.view" } }, Guid.NewGuid())).Status);
        try
        {
            var (_, token, _) = await StaffWithRoleAsync(code, app.Branch2);
            var c = app.Client(token);
            Assert.Equal(200, (await Api.PatchAsync(c, $"/api/v1/branches/{app.Branch2}", new { phone = "07700000002" })).Status);
            Assert.Equal(403, (await Api.PatchAsync(c, $"/api/v1/branches/{app.Branch1}", new { phone = "07700000001" })).Status);
            Assert.Equal(403, (await Api.PatchAsync(c, $"/api/v1/branches/{app.Branch2}", new { is_active = false })).Status);
            Assert.Equal(200, (await Api.GetAsync(c, $"/api/v1/branches/{app.Branch2}/overview")).Status);
            Assert.Equal(403, (await Api.GetAsync(c, $"/api/v1/branches/{app.Branch1}/overview")).Status);
        }
        finally
        {
            await app.ExecAsync($"UPDATE users SET role = 'lab_tech' WHERE role = '{code}'");
            await Api.SendAsync(admin, HttpMethod.Delete, $"/api/v1/roles/{code}");
        }
    }
}
