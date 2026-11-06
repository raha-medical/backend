using System.Text.Json.Nodes;
using Xunit;

namespace AlRahma.Api.Tests;

/// <summary>
/// عزل الفروع (المرحلة 2): مدير فرع ومحاسب في الفرع B2 لا يصلان إلى أي بيانات فرع آخر، لا بالمعرف ولا بفلاتر القوائم.
/// المرضى والوصفات ورصيد المريض مشتركة بين الفروع عمداً (ملف المريض الموحد) فلا تُختبر هنا.
/// </summary>
[Collection(ApiCollection.Name)]
public class BranchIsolationTests(TestApp app)
{
    private static Guid Id(JsonNode? n, string key) => n![key]!.GetValue<Guid>();
    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)).ToString("yyyy-MM-dd");

    private sealed record Victim(Guid Branch, ClinicSetup S, Guid Patient, Guid Item, Guid Ticket, Guid Invoice, Guid Receipt, Guid Shift,
        Guid Expense, Guid Refund, Guid Deferral, Guid Service, Guid LabService, Guid Device, Guid StaffUser, Guid OnlinePayment);

    /// <summary>فرع "ضحية" كامل: زيارة مدفوعة بإيصال ووردية مغلقة، مصروف، استرجاع بانتظار الموافقة، آجل، جهاز، وموظف.</summary>
    private async Task<Victim> VictimAsync()
    {
        var admin = app.Client(await app.AdminTokenAsync());
        var code = "V" + Random.Shared.Next(10000, 99999);
        var branch = Id((await Api.PostAsync(admin, "/api/v1/branches", new { code, name = $"فرع معزول {code}" }, Guid.NewGuid())).Body, "branch_id");
        await app.ExecAsync($"INSERT INTO system_settings (branch_id, key, value) VALUES ('{branch}', 'refund.cashier_limit', '1000')");
        var q = new QueueTests(app);
        var s = await q.SetupAsync(branch, ["expenses.manage"]);

        // زيارة مدفوعة
        var patient = await q.PatientAsync(s.Reception);
        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.Equal(201, checkIn.Status);
        var ticket = Id(checkIn.Body, "ticket_id");
        var invoice = Id(checkIn.Body, "invoice_id");
        var item = Id((await Api.GetAsync(s.Reception, $"/api/v1/patients/{patient}/billable-items")).Body!["items"]![0], "invoice_item_id");
        var shift = Id((await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid())).Body, "shift_id");
        var paid = await Api.PostAsync(s.Reception, "/api/v1/payments",
            new { patient_id = patient, invoice_item_ids = new[] { item }, tenders = new[] { new { method = "cash", amount = 25000 } } }, Guid.NewGuid());
        Assert.Equal(201, paid.Status);
        var receipt = Id(paid.Body!["receipt"], "receipt_id");

        // استرجاع فوق حد الاستقبال (1000) = بانتظار الموافقة
        var refund = await Api.PostAsync(s.Reception, $"/api/v1/invoice-items/{item}/refunds", new { amount = 5000, method = "cash", reason = "اختبار العزل" }, Guid.NewGuid());
        Assert.Equal(201, refund.Status);
        Assert.Equal("pending_approval", refund.Body!["status"]!.GetValue<string>());

        var expense = await Api.PostAsync(s.Reception, "/api/v1/expenses",
            new { category = "cleaning", amount = 3000, description = "مواد تنظيف", paid_from = "shift_cash" }, Guid.NewGuid());
        Assert.Equal(201, expense.Status);
        await DailyAccount.PrintAllAsync(s.Reception);
        Assert.Equal(200, (await Api.PostAsync(s.Reception, $"/api/v1/shifts/{shift}/close", new { declared = new { cash = 22000 } }, Guid.NewGuid())).Status);

        // آجل على زيارة ثانية
        var p2 = await q.PatientAsync(s.Reception);
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = p2, doctor_id = s.DoctorId }, Guid.NewGuid())).Status);
        var item2 = Id((await Api.GetAsync(s.Reception, $"/api/v1/patients/{p2}/billable-items")).Body!["items"]![0], "invoice_item_id");
        var deferral = await Api.PostAsync(s.Doctor, "/api/v1/deferrals",
            new { patient_id = p2, invoice_item_ids = new[] { item2 }, reason = "humanitarian", note = "عزل" }, Guid.NewGuid());
        Assert.Equal(201, deferral.Status);

        var service = await app.ScalarAsync<Guid>($"SELECT service_id FROM services WHERE branch_id = '{branch}' LIMIT 1");
        var lab = Guid.NewGuid();
        var labService = Guid.NewGuid();
        await app.ExecAsync($"""
            INSERT INTO clinics (clinic_id, branch_id, name, department) VALUES ('{lab}', '{branch}', 'مختبر {code}', 'lab');
            INSERT INTO services (service_id, branch_id, clinic_id, name, type, price) VALUES ('{labService}', '{branch}', '{lab}', 'CBC', 'lab', 5000);
            """);
        var device = Id((await Api.PostAsync(s.Admin, "/api/v1/devices", new { kind = "display", name = "شاشة", clinic_id = s.ClinicId })).Body, "device_id");
        var (staffUser, _) = await app.CreateStaffAsync("lab_tech", branch);
        var onlinePayment = Guid.NewGuid();
        await app.ExecAsync($"""
            INSERT INTO online_payments (online_payment_id, patient_id, purpose, invoice_id, provider, amount, expires_at)
            VALUES ('{onlinePayment}', '{patient}', 'invoice', '{invoice}', 'sandbox', 25000, now() + interval '1 hour');
            """);
        return new Victim(branch, s, patient, item, ticket, invoice, receipt, shift, Id(expense.Body, "expense_id"), Id(refund.Body, "refund_id"),
            Id(deferral.Body, "deferral_id"), service, labService, device, staffUser, onlinePayment);
    }

    private static void Hidden(ApiResult r, string what) =>
        Assert.True(r.Status is 403 or 404, $"{what}: توقعنا 403/404 ووصل {r.Status} {r.Raw}");

    [SkippableFact]
    public async Task Branch_manager_of_another_branch_cannot_reach_any_branch_data_by_id_or_filter()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var v = await VictimAsync();
        var (_, managerToken) = await app.CreateStaffAsync("branch_manager", app.Branch2);
        var m = app.Client(managerToken);

        // بالمعرف: قراءة
        Hidden(await Api.GetAsync(m, $"/api/v1/branches/{v.Branch}"), "branch");
        Hidden(await Api.GetAsync(m, $"/api/v1/branches/{v.Branch}/overview"), "overview");
        Hidden(await Api.GetAsync(m, $"/api/v1/branches/{v.Branch}/clinics"), "clinics of branch");
        Hidden(await Api.GetAsync(m, $"/api/v1/branches/{v.Branch}/settings"), "branch settings");
        Hidden(await Api.GetAsync(m, $"/api/v1/branches/{v.Branch}/closures"), "branch closures");
        Hidden(await Api.GetAsync(m, $"/api/v1/clinics/{v.S.ClinicId}"), "clinic");
        Hidden(await Api.GetAsync(m, $"/api/v1/clinics/{v.S.ClinicId}/closures"), "clinic closures");
        Hidden(await Api.GetAsync(m, $"/api/v1/users/{v.StaffUser}"), "user");
        Hidden(await Api.GetAsync(m, $"/api/v1/services/{v.Service}"), "service");
        Hidden(await Api.GetAsync(m, $"/api/v1/doctors/{v.S.DoctorId}"), "doctor");
        Hidden(await Api.GetAsync(m, $"/api/v1/doctors/{v.S.DoctorId}/schedule"), "doctor schedule");
        Hidden(await Api.GetAsync(m, $"/api/v1/doctors/{v.S.DoctorId}/settings"), "doctor settings");
        Hidden(await Api.GetAsync(m, $"/api/v1/doctors/{v.S.DoctorId}/commissions"), "doctor commissions");
        Hidden(await Api.GetAsync(m, $"/api/v1/doctors/{v.S.DoctorId}/sessions"), "doctor sessions");
        Hidden(await Api.GetAsync(m, $"/api/v1/lab/tests/{v.LabService}/template"), "lab template");
        Hidden(await Api.GetAsync(m, $"/api/v1/invoices/{v.Invoice}"), "invoice");
        Hidden(await Api.GetAsync(m, $"/api/v1/receipts/{v.Receipt}/print-payload"), "receipt");
        Hidden(await Api.GetAsync(m, $"/api/v1/receipts?shift_id={v.Shift}"), "receipts of shift");
        Hidden(await Api.GetAsync(m, $"/api/v1/shifts/{v.Shift}/reconciliation"), "shift reconciliation");
        Hidden(await Api.GetAsync(m, $"/api/v1/refunds/{v.Refund}"), "refund");
        Hidden(await Api.GetAsync(m, $"/api/v1/expenses/{v.Expense}"), "expense");
        Hidden(await Api.GetAsync(m, $"/api/v1/deferrals/{v.Deferral}"), "deferral");
        Hidden(await Api.GetAsync(m, $"/api/v1/online-payments/{v.OnlinePayment}"), "online payment");
        Hidden(await Api.GetAsync(m, $"/api/v1/reports/doctor-earnings?doctor_id={v.S.DoctorId}"), "doctor earnings");

        // بالمعرف: تعديل
        Hidden(await Api.PatchAsync(m, $"/api/v1/users/{v.StaffUser}", new { full_name = "اختراق" }), "patch user");
        Hidden(await Api.PostAsync(m, $"/api/v1/users/{v.StaffUser}/disable"), "disable user");
        Hidden(await Api.PostAsync(m, $"/api/v1/users/{v.StaffUser}/reset-password"), "reset password");
        Hidden(await Api.PatchAsync(m, $"/api/v1/services/{v.Service}", new { price = 1 }), "patch service");
        Hidden(await Api.PatchAsync(m, $"/api/v1/clinics/{v.S.ClinicId}", new { name = "اختراق" }), "patch clinic");
        Hidden(await Api.PostAsync(m, $"/api/v1/branches/{v.Branch}/clinics", new { name = "اختراق", department = "clinic" }, Guid.NewGuid()), "create clinic");
        Hidden(await Api.PatchAsync(m, $"/api/v1/branches/{v.Branch}", new { name = "اختراق" }), "patch branch");
        Hidden(await Api.PatchAsync(m, $"/api/v1/branches/{v.Branch}/settings", new { values = new Dictionary<string, object> { ["ban.no_show_limit"] = 9 } }), "branch settings");
        Hidden(await Api.PostAsync(m, $"/api/v1/branches/{v.Branch}/closures", new { reason = "maintenance", starts_on = Today }, Guid.NewGuid()), "close branch");
        Hidden(await Api.PostAsync(m, $"/api/v1/clinics/{v.S.ClinicId}/closures", new { reason = "maintenance", starts_on = Today }, Guid.NewGuid()), "close clinic");
        Hidden(await Api.SendAsync(m, HttpMethod.Put, $"/api/v1/doctors/{v.S.DoctorId}/schedule", new { weekly = Array.Empty<object>() }), "doctor schedule");
        Hidden(await Api.PostAsync(m, $"/api/v1/devices/{v.Device}/display-key"), "rotate display key");
        Hidden(await Api.PostAsync(m, $"/api/v1/receipts/{v.Receipt}/reprint"), "reprint receipt");
        Hidden(await Api.PostAsync(m, $"/api/v1/refunds/{v.Refund}/approve"), "approve refund");
        Hidden(await Api.PostAsync(m, $"/api/v1/shifts/{v.Shift}/review", new { decision = "accepted" }), "review shift");
        Hidden(await Api.PostAsync(m, $"/api/v1/expenses/{v.Expense}/void", new { reason = "اختراق" }), "void expense");
        Hidden(await Api.PostAsync(m, $"/api/v1/invoices/{v.Invoice}/void", new { reason = "اختراق" }), "void invoice");
        Hidden(await Api.PostAsync(m, $"/api/v1/invoice-items/{v.Item}/refunds", new { amount = 1000, method = "cash", reason = "x" }, Guid.NewGuid()), "refund item");
        Hidden(await Api.PostAsync(m, $"/api/v1/queue/tickets/{v.Ticket}/call"), "call ticket");
        Hidden(await Api.PostAsync(m, $"/api/v1/queue/tickets/{v.Ticket}/no-show", new { mode = "skip" }), "no-show ticket");
        // إعادة إرسال تذكرة أوفلاين بمعرف تذكرة الفرع الآخر لا تكشف بياناتها
        var replay = await Api.PostAsync(m, "/api/v1/queue/check-in", new { ticket_id = v.Ticket, patient_id = v.Patient, doctor_id = v.S.DoctorId }, Guid.NewGuid());
        Assert.True(replay.Status is 404 or 409, replay.Raw);
        Assert.Null(replay.Body?["ticket_number"]);

        // فلاتر القوائم بفرع آخر: 403 أو قائمة بلا بيانات الفرع
        foreach (var url in new[]
                 {
                     $"/api/v1/reports/dashboard?branch_id={v.Branch}", $"/api/v1/reports/daily-revenue?branch_id={v.Branch}",
                     $"/api/v1/reports/no-shows?branch_id={v.Branch}", $"/api/v1/reports/audit-logs?branch_id={v.Branch}",
                     $"/api/v1/reports/branch-settlements?branch_id={v.Branch}", $"/api/v1/reports/doctor-earnings?branch_id={v.Branch}",
                     $"/api/v1/expenses?branch_id={v.Branch}", $"/api/v1/deferrals?branch_id={v.Branch}",
                 })
            Hidden(await Api.GetAsync(m, url), url);
        Assert.Empty((await Api.GetAsync(m, $"/api/v1/users?branch_id={v.Branch}&page_size=100")).Body!["items"]!.AsArray());
        Assert.DoesNotContain((await Api.GetAsync(m, "/api/v1/users?page_size=100")).Body!["items"]!.AsArray(), u => Id(u, "user_id") == v.StaffUser);
        Assert.DoesNotContain((await Api.GetAsync(m, "/api/v1/invoices?page_size=100")).Body!["items"]!.AsArray(), i => Id(i, "invoice_id") == v.Invoice);
        Assert.DoesNotContain((await Api.GetAsync(m, "/api/v1/refunds?page_size=100")).Body!["items"]!.AsArray(), r => Id(r, "refund_id") == v.Refund);
        Assert.DoesNotContain((await Api.GetAsync(m, "/api/v1/shifts?page_size=100")).Body!["items"]!.AsArray(), x => Id(x, "shift_id") == v.Shift);
        Assert.DoesNotContain((await Api.GetAsync(m, "/api/v1/services?include_inactive=true")).Body!["items"]!.AsArray(), x => Id(x, "service_id") == v.Service);
        Assert.DoesNotContain((await Api.GetAsync(m, "/api/v1/doctors")).Body!["items"]!.AsArray(), x => Id(x, "doctor_id") == v.S.DoctorId);
        Assert.DoesNotContain((await Api.GetAsync(m, "/api/v1/devices")).Body!["items"]!.AsArray(), x => Id(x, "device_id") == v.Device);
        Assert.DoesNotContain((await Api.GetAsync(m, $"/api/v1/queue/live?doctor_id={v.S.DoctorId}")).Raw, v.Ticket.ToString());
        var audit = await Api.GetAsync(m, "/api/v1/reports/audit-logs?page_size=200");
        if (audit.Status == 200)
            Assert.DoesNotContain(audit.Body!["items"]!.AsArray(), l => l!["branch_id"]?.GetValue<Guid>() == v.Branch);
        var dash = await Api.GetAsync(m, "/api/v1/reports/dashboard");
        Assert.DoesNotContain(dash.Body!["branches"]!.AsArray(), b => Id(b, "branch_id") == v.Branch);
        var settlements = await Api.GetAsync(m, "/api/v1/reports/branch-settlements");
        Assert.DoesNotContain(settlements.Body!["branches"]!.AsArray(), b => Id(b, "branch_id") == v.Branch);

        // شبكة الفروع: الفرع الآخر بمعلوماته العامة فقط (بلا مدراء ولا نشاط ولا أعداد)
        var grid = (await Api.GetAsync(m, "/api/v1/branches")).Body!["items"]!.AsArray();
        var other = grid.First(b => Id(b, "branch_id") == v.Branch)!;
        Assert.False(other["is_assigned"]!.GetValue<bool>());
        Assert.Null(other["managers"]);
        Assert.Null(other["activity"]);
        Assert.Null(other["department_counts"]);
        Assert.True(grid.First(b => Id(b, "branch_id") == app.Branch2)!["is_assigned"]!.GetValue<bool>());

        // بيانات الفرع لم تتغير
        Assert.Equal("pending_approval", await app.ScalarAsync<string>($"SELECT status FROM refunds WHERE refund_id = '{v.Refund}'"));
        Assert.True(await app.ScalarAsync<bool>($"SELECT is_active FROM users WHERE user_id = '{v.StaffUser}'"));
        Assert.Equal(0L, await app.ScalarAsync<long>($"SELECT count(*) FROM branch_closures WHERE branch_id = '{v.Branch}'"));
    }

    [SkippableFact]
    public async Task Accountant_of_another_branch_cannot_reach_finance_data_of_the_branch()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var v = await VictimAsync();
        var (_, accountantToken) = await app.CreateStaffAsync("accountant", app.Branch2);
        var a = app.Client(accountantToken);

        Hidden(await Api.GetAsync(a, $"/api/v1/invoices/{v.Invoice}"), "invoice");
        Hidden(await Api.GetAsync(a, $"/api/v1/receipts/{v.Receipt}/print-payload"), "receipt");
        Hidden(await Api.GetAsync(a, $"/api/v1/receipts?shift_id={v.Shift}"), "receipts of shift");
        Hidden(await Api.GetAsync(a, $"/api/v1/shifts/{v.Shift}/reconciliation"), "reconciliation");
        Hidden(await Api.PostAsync(a, $"/api/v1/shifts/{v.Shift}/review", new { decision = "accepted" }), "review");
        Hidden(await Api.GetAsync(a, $"/api/v1/refunds/{v.Refund}"), "refund");
        Hidden(await Api.PostAsync(a, $"/api/v1/refunds/{v.Refund}/approve"), "approve");
        Hidden(await Api.PostAsync(a, $"/api/v1/refunds/{v.Refund}/reject", new { reason = "x" }), "reject");
        Hidden(await Api.GetAsync(a, $"/api/v1/expenses/{v.Expense}"), "expense");
        Hidden(await Api.PostAsync(a, $"/api/v1/expenses/{v.Expense}/void", new { reason = "x" }), "void expense");
        Hidden(await Api.GetAsync(a, $"/api/v1/deferrals/{v.Deferral}"), "deferral");
        Hidden(await Api.GetAsync(a, $"/api/v1/online-payments/{v.OnlinePayment}"), "online payment");
        Hidden(await Api.GetAsync(a, $"/api/v1/doctors/{v.S.DoctorId}/commissions"), "commissions");
        Hidden(await Api.GetAsync(a, $"/api/v1/reports/doctor-earnings?doctor_id={v.S.DoctorId}"), "doctor earnings");
        // المريض مشترك بين الفروع، لكن سلته في كل فرع تعرض بنود ذلك الفرع فقط
        var basket = await Api.GetAsync(a, $"/api/v1/patients/{v.Patient}/billable-items");
        Assert.True(basket.Status != 200 || basket.Body!["items"]!.AsArray().Count == 0, basket.Raw);
        foreach (var url in new[]
                 {
                     $"/api/v1/reports/dashboard?branch_id={v.Branch}", $"/api/v1/reports/daily-revenue?branch_id={v.Branch}",
                     $"/api/v1/reports/audit-logs?branch_id={v.Branch}", $"/api/v1/reports/branch-settlements?branch_id={v.Branch}",
                     $"/api/v1/expenses?branch_id={v.Branch}", $"/api/v1/deferrals?branch_id={v.Branch}",
                 })
            Hidden(await Api.GetAsync(a, url), url);
        var audit = await Api.GetAsync(a, "/api/v1/reports/audit-logs?page_size=200");
        Assert.Equal(200, audit.Status);
        Assert.DoesNotContain(audit.Body!["items"]!.AsArray(), l => l!["branch_id"]?.GetValue<Guid>() == v.Branch);
        Assert.DoesNotContain((await Api.GetAsync(a, "/api/v1/invoices?page_size=100")).Body!["items"]!.AsArray(), i => Id(i, "invoice_id") == v.Invoice);
        Assert.DoesNotContain((await Api.GetAsync(a, "/api/v1/shifts?page_size=100")).Body!["items"]!.AsArray(), x => Id(x, "shift_id") == v.Shift);
        Assert.DoesNotContain((await Api.GetAsync(a, "/api/v1/expenses")).Body!["items"]!.AsArray(), x => Id(x, "expense_id") == v.Expense);
        Assert.DoesNotContain((await Api.GetAsync(a, "/api/v1/deferrals")).Body!["items"]!.AsArray(), x => Id(x, "deferral_id") == v.Deferral);

        // المدير العام يصل لكل شيء
        var admin = app.Client(await app.AdminTokenAsync());
        Assert.Equal(200, (await Api.GetAsync(admin, $"/api/v1/invoices/{v.Invoice}")).Status);
        Assert.Equal(200, (await Api.GetAsync(admin, $"/api/v1/receipts?shift_id={v.Shift}")).Status);
        Assert.Equal(200, (await Api.GetAsync(admin, $"/api/v1/online-payments/{v.OnlinePayment}")).Status);
        Assert.Equal(200, (await Api.GetAsync(admin, $"/api/v1/branches/{v.Branch}/clinics")).Status);
        Assert.Equal(200, (await Api.GetAsync(admin, $"/api/v1/lab/tests/{v.LabService}/template")).Status);
        // ومحاسب الفرع نفسه يرى بيانات فرعه
        var (_, ownToken) = await app.CreateStaffAsync("accountant", v.Branch);
        var own = app.Client(ownToken);
        Assert.Equal(200, (await Api.GetAsync(own, $"/api/v1/receipts?shift_id={v.Shift}")).Status);
        Assert.Equal(200, (await Api.GetAsync(own, $"/api/v1/online-payments/{v.OnlinePayment}")).Status);
        Assert.Equal(200, (await Api.GetAsync(own, $"/api/v1/reports/doctor-earnings?doctor_id={v.S.DoctorId}")).Status);
    }

    [SkippableFact]
    public async Task Removing_a_user_from_the_branch_or_disabling_him_cuts_his_current_token()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var (managerId, managerToken) = await app.CreateStaffAsync("branch_manager", app.Branch2);
        var m = app.Client(managerToken);
        Assert.Equal(200, (await Api.GetAsync(m, "/api/v1/users")).Status);

        // نقل مدير الفرع من B2 إلى B1: توكن B2 القديم يتوقف فوراً (قبل انتهاء الـ 15 دقيقة)
        Assert.Equal(200, (await Api.PatchAsync(admin, $"/api/v1/users/{managerId}", new { branch_ids = new[] { app.Branch1 } })).Status);
        var cut = await Api.GetAsync(m, "/api/v1/users");
        Assert.Equal(401, cut.Status);

        var (recId, recToken) = await app.CreateStaffAsync("receptionist");
        Assert.Equal(200, (await Api.GetAsync(app.Client(recToken), "/api/v1/auth/me")).Status);
        Assert.Equal(204, (await Api.PostAsync(admin, $"/api/v1/users/{recId}/disable")).Status);
        Assert.Equal(401, (await Api.GetAsync(app.Client(recToken), "/api/v1/auth/me")).Status);
    }
}
