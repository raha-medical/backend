using System.Text.Json.Nodes;
using Xunit;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class AdminFinanceTests(TestApp app)
{
    private static Guid Id(JsonNode? n, string key) => n![key]!.GetValue<Guid>();
    private static long L(JsonNode? n, string key) => n![key]!.GetValue<long>();
    private static string S(JsonNode? n, string key) => n![key]!.GetValue<string>();
    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)).ToString("yyyy-MM-dd");

    private async Task<Guid> NewBranchAsync(HttpClient admin)
    {
        var code = "F" + Random.Shared.Next(10000, 99999);
        return Id((await Api.PostAsync(admin, "/api/v1/branches", new { latitude = 33.3m, longitude = 44.4m, code, name = $"فرع المال {code}" }, Guid.NewGuid())).Body, "branch_id");
    }

    /// <summary>مريض يثبت حضوره عند الطبيب ويعيد بند الكشفية في سلته.</summary>
    private async Task<(Guid Patient, Guid Item, Guid Ticket)> VisitAsync(QueueTests q, ClinicSetup s)
    {
        var patient = await q.PatientAsync(s.Reception);
        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.Equal(201, checkIn.Status);
        var basket = await Api.GetAsync(s.Reception, $"/api/v1/patients/{patient}/billable-items");
        return (patient, Id(basket.Body!["items"]![0], "invoice_item_id"), Id(checkIn.Body, "ticket_id"));
    }

    [SkippableFact]
    public async Task Revenue_expenses_doctor_earnings_and_dashboard_per_branch()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var branch = await NewBranchAsync(admin);
        var q = new QueueTests(app);
        var s = await q.SetupAsync(branch, ["expenses.manage"]);
        var (_, managerToken) = await app.CreateStaffAsync("branch_manager", branch);
        var manager = app.Client(managerToken);
        var (_, accountantToken) = await app.CreateStaffAsync("accountant", branch);
        var accountant = app.Client(accountantToken);

        var (patient, item, ticket) = await VisitAsync(q, s);
        var shift = Id((await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid())).Body, "shift_id");
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/payments",
            new { patient_id = patient, invoice_item_ids = new[] { item }, tenders = new[] { new { method = "cash", amount = 25000 } } }, Guid.NewGuid())).Status);

        // مصروف نقدي من الدرج يفتح الدرج، ومصروف من صندوق الفرع لا يمسه
        Assert.Equal(400, (await Api.PostAsync(s.Reception, "/api/v1/expenses",
            new { category = "no_such", amount = 1000, description = "x", paid_from = "treasury" }, Guid.NewGuid())).Status);
        var cashExpense = await Api.PostAsync(s.Reception, "/api/v1/expenses",
            new { category = "cleaning", amount = 5000, description = "مواد تنظيف", payee = "محل النور", paid_from = "shift_cash" }, Guid.NewGuid());
        Assert.Equal(201, cashExpense.Status);
        Assert.False(cashExpense.Body!["open_drawer"]!.GetValue<bool>()); // 7.1-أ: الدرج موقوف
        Assert.Contains("-EXP-", S(cashExpense.Body, "expense_number"));
        Assert.Equal(shift, Id(cashExpense.Body, "shift_id"));
        Assert.Equal("NO_OPEN_SHIFT", (await Api.PostAsync(manager, "/api/v1/expenses",
            new { category = "rent", amount = 1000, description = "x", paid_from = "shift_cash" }, Guid.NewGuid())).Code);
        Assert.Equal(201, (await Api.PostAsync(manager, "/api/v1/expenses",
            new { category = "rent", amount = 100000, description = "إيجار الشهر", paid_from = "treasury" }, Guid.NewGuid())).Status);
        var (_, plainRec) = await app.CreateStaffAsync("receptionist", branch, clinics: [s.ClinicId]);
        Assert.Equal(403, (await Api.GetAsync(app.Client(plainRec), "/api/v1/expenses")).Status);

        var expenses = await Api.GetAsync(accountant, $"/api/v1/expenses?branch_id={branch}");
        Assert.Equal(105000, L(expenses.Body, "total_amount"));
        Assert.Equal(2, expenses.Body!["by_category"]!.AsArray().Count);
        Assert.Equal("إيجار", S(expenses.Body!["by_category"]![0], "category_name"));

        // الإغلاق الأعمى يطرح مصروف الدرج من المتوقع: 25,000 − 5,000
        await DailyAccount.PrintAllAsync(s.Reception);
        var closed = await Api.PostAsync(s.Reception, $"/api/v1/shifts/{shift}/close", new { declared = new { cash = 20000 } }, Guid.NewGuid());
        Assert.Equal(200, closed.Status);
        var rec = await Api.GetAsync(accountant, $"/api/v1/shifts/{shift}/reconciliation");
        Assert.Equal("MATCHED", S(rec.Body, "audit_status"));
        Assert.Equal(-5000, L(rec.Body!["cash_breakdown"], "cash_expenses"));
        Assert.Equal("SHIFT_CLOSED", (await Api.PostAsync(accountant, $"/api/v1/expenses/{Id(cashExpense.Body, "expense_id")}/void", new { reason = "خطأ" })).Code);

        var revenue = await Api.GetAsync(admin, $"/api/v1/reports/daily-revenue?branch_id={branch}&date={Today}");
        Assert.Equal(200, revenue.Status);
        Assert.Equal(25000, L(revenue.Body, "total_net"));
        Assert.Equal(105000, L(revenue.Body, "expenses"));
        Assert.Equal(-80000, L(revenue.Body, "net_after_expenses"));
        Assert.Equal(25000, L(revenue.Body!["by_department"]!.AsArray().First(d => S(d, "department") == "clinic"), "net"));
        Assert.Equal(25000, L(revenue.Body!["by_method"]!.AsArray().First(d => S(d, "method") == "cash"), "amount"));
        Assert.Equal(1, revenue.Body!["payments_count"]!.GetValue<int>());

        // أرباح الطبيب بنسبة 70%: الطبيب يرى أرباحه فقط
        var earnings = await Api.GetAsync(admin, $"/api/v1/reports/doctor-earnings?doctor_id={s.DoctorId}&from={Today}&to={Today}");
        Assert.Equal(1, earnings.Body!["new_visits"]!["count"]!.GetValue<int>());
        Assert.Equal(17500, L(earnings.Body, "total_doctor_share"));
        Assert.Equal(7500, L(earnings.Body, "total_complex_share"));
        var own = await Api.GetAsync(s.Doctor, $"/api/v1/reports/doctor-earnings?from={Today}&to={Today}");
        Assert.Equal(s.DoctorId, Id(own.Body, "doctor_id"));
        var other = await new QueueTests(app).SetupAsync();
        Assert.Equal(403, (await Api.GetAsync(s.Doctor, $"/api/v1/reports/doctor-earnings?doctor_id={other.DoctorId}")).Status);
        var all = await Api.GetAsync(manager, $"/api/v1/reports/doctor-earnings?from={Today}&to={Today}");
        Assert.Contains(all.Body!["items"]!.AsArray(), x => Id(x, "doctor_id") == s.DoctorId);

        // الغياب حسب الطبيب
        await app.ExecAsync($"INSERT INTO patient_no_shows (patient_id, ticket_id) VALUES ('{patient}', '{ticket}')");
        var noShows = await Api.GetAsync(manager, $"/api/v1/reports/no-shows?from={Today}&to={Today}");
        Assert.Equal(1, noShows.Body!["total_no_shows"]!.GetValue<int>());
        Assert.Equal(s.DoctorId, Id(noShows.Body!["by_doctor"]![0], "doctor_id"));

        // لوحة المدير العام: كل الفروع للمدير العام، وفرعه فقط لمدير الفرع
        var dash = await Api.GetAsync(admin, $"/api/v1/reports/dashboard?date={Today}");
        Assert.Equal(200, dash.Status);
        var row = dash.Body!["branches"]!.AsArray().First(b => Id(b, "branch_id") == branch)!;
        Assert.Equal(25000, L(row["today"], "revenue_net"));
        Assert.Equal(-80000, L(row["today"], "net_after_expenses"));
        Assert.Equal(25000, L(row["today"], "cash_in"));
        Assert.Equal(1, row["alerts"]!["shifts_pending_review"]!.GetValue<int>());
        Assert.Equal(1, row["activity"]!["checked_in"]!.GetValue<int>());
        Assert.True(dash.Body!["branches"]!.AsArray().Count >= 2);
        var mine = await Api.GetAsync(manager, "/api/v1/reports/dashboard");
        Assert.Single(mine.Body!["branches"]!.AsArray());
        Assert.Equal(25000, L(mine.Body!["totals"]!["month_to_date"], "revenue_net"));
        Assert.Equal(403, (await Api.GetAsync(manager, $"/api/v1/reports/dashboard?branch_id={app.Branch1}")).Status);
        Assert.Equal(403, (await Api.GetAsync(s.Reception, "/api/v1/reports/dashboard")).Status);

        // لوحة الإحصائيات: السلسلة اليومية والتوزيعات بنفس تعريف الإيراد، والنطاق نفسه
        var stats = await Api.GetAsync(admin, $"/api/v1/reports/stats?branch_id={branch}");
        Assert.Equal(200, stats.Status);
        Assert.Equal(30, stats.Body!["days"]!.AsArray().Count);
        var todayRow = stats.Body!["days"]!.AsArray().Last()!;
        Assert.Equal(Today, S(todayRow, "date"));
        Assert.Equal(25000, L(todayRow, "revenue"));
        Assert.Equal(1, todayRow["visits"]!.GetValue<int>());
        Assert.Equal(25000, L(stats.Body!["totals"], "revenue"));
        Assert.Equal(25000, L(stats.Body!["departments"]!.AsArray().First(d => S(d, "key") == "clinic"), "value"));
        Assert.Single(stats.Body!["branches"]!.AsArray());
        Assert.Equal(7, stats.Body!["weekdays"]!.AsArray().Count);
        // الدينار والدولار منفصلان: الدفعة نقداً بالدينار
        Assert.Equal(25000, L(stats.Body!["currency"], "iqd"));
        Assert.Equal(0, L(stats.Body!["currency"], "usd"));
        Assert.Equal(25000, L(todayRow, "collected_iqd"));
        Assert.Contains(stats.Body!["methods"]!.AsArray(), m => S(m, "key") == "cash_usd");
        // مؤشرات خريطة المدير العام (UI-85، UI-87)
        var ov = await Api.GetAsync(admin, "/api/v1/reports/branches-overview");
        Assert.Equal(200, ov.Status);
        var ovRow = ov.Body!["items"]!.AsArray().First(b => Id(b, "branch_id") == branch)!;
        Assert.Equal(0, L(ovRow, "revenue_today_usd"));
        Assert.Equal(1, ovRow["shifts_pending_review"]!.GetValue<int>());
        Assert.NotNull(ov.Body!["queue_now"]);
        Assert.Equal(403, (await Api.GetAsync(manager, "/api/v1/reports/branches-overview")).Status);
        Assert.Equal(200, (await Api.GetAsync(manager, "/api/v1/reports/stats")).Status);
        Assert.Equal(403, (await Api.GetAsync(manager, $"/api/v1/reports/stats?branch_id={app.Branch1}")).Status);
        Assert.Equal(403, (await Api.GetAsync(s.Reception, "/api/v1/reports/stats")).Status);
        Assert.Equal(400, (await Api.GetAsync(admin, "/api/v1/reports/stats?from=2024-01-01&to=2026-01-01")).Status);
    }

    [SkippableFact]
    public async Task Deferral_is_settled_by_payment_or_written_off_by_admin_and_audited()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var (_, managerToken) = await app.CreateStaffAsync("branch_manager");
        var manager = app.Client(managerToken);
        var due = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)).AddDays(7).ToString("yyyy-MM-dd");

        var (patient, item, _) = await VisitAsync(q, s);
        Assert.Equal(400, (await Api.PostAsync(s.Doctor, "/api/v1/deferrals", new { patient_id = patient, invoice_item_ids = new[] { item }, reason = "other" }, Guid.NewGuid())).Status);
        Assert.Equal(403, (await Api.PostAsync(s.Reception, "/api/v1/deferrals", new { patient_id = patient, invoice_item_ids = new[] { item }, reason = "emergency" }, Guid.NewGuid())).Status);
        var deferral = await Api.PostAsync(s.Doctor, "/api/v1/deferrals",
            new { patient_id = patient, invoice_item_ids = new[] { item }, reason = "humanitarian", note = "حالة إنسانية", due_date = due }, Guid.NewGuid());
        Assert.Equal(201, deferral.Status);
        Assert.Equal(25000, L(deferral.Body, "amount"));
        Assert.Equal("active", S(deferral.Body, "status"));
        Assert.Equal("deferred", S(deferral.Body!["items"]![0], "status"));
        var deferralId = Id(deferral.Body, "deferral_id");
        Assert.Equal("ITEM_NOT_PAYABLE", (await Api.PostAsync(s.Doctor, "/api/v1/deferrals",
            new { patient_id = patient, invoice_item_ids = new[] { item }, reason = "emergency" }, Guid.NewGuid())).Code);
        var live = await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}");
        Assert.Contains(live.Body!["items"]!.AsArray(), t => S(t, "payment_status") == "deferred");

        var list = await Api.GetAsync(manager, $"/api/v1/deferrals?patient_id={patient}");
        Assert.Equal(25000, L(list.Body, "total_outstanding"));
        Assert.Equal(25000, L(list.Body!["items"]![0], "remaining"));
        Assert.Equal(403, (await Api.GetAsync(s.Reception, "/api/v1/deferrals")).Status);

        // التسديد لاحقاً بنفس البنود يغلق الآجل تلقائياً
        await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid());
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/payments",
            new { patient_id = patient, invoice_item_ids = new[] { item }, tenders = new[] { new { method = "cash", amount = 25000 } } }, Guid.NewGuid())).Status);
        var settled = await Api.GetAsync(manager, $"/api/v1/deferrals/{deferralId}");
        Assert.Equal("settled", S(settled.Body, "status"));
        Assert.Equal(25000, L(settled.Body, "paid"));
        Assert.Equal(0, L(settled.Body, "remaining"));

        // الإعفاء للمدير العام فقط، وبعده لا يُحصّل البند
        var (patient2, item2, _) = await VisitAsync(q, s);
        var d2 = Id((await Api.PostAsync(s.Doctor, "/api/v1/deferrals",
            new { patient_id = patient2, invoice_item_ids = new[] { item2 }, reason = "emergency" }, Guid.NewGuid())).Body, "deferral_id");
        Assert.Equal(403, (await Api.PostAsync(manager, $"/api/v1/deferrals/{d2}/write-off", new { reason = "إعفاء" })).Status);
        var admin = app.Client(await app.AdminTokenAsync());
        var off = await Api.PostAsync(admin, $"/api/v1/deferrals/{d2}/write-off", new { reason = "إعفاء نهائي" });
        Assert.Equal("written_off", S(off.Body, "status"));
        Assert.Equal("DEFERRAL_NOT_ACTIVE", (await Api.PostAsync(admin, $"/api/v1/deferrals/{d2}/write-off", new { reason = "مرة ثانية" })).Code);
        Assert.Empty((await Api.GetAsync(s.Reception, $"/api/v1/patients/{patient2}/billable-items")).Body!["items"]!.AsArray());
        Assert.Equal("ITEM_NOT_PAYABLE", (await Api.PostAsync(s.Reception, "/api/v1/payments",
            new { patient_id = patient2, invoice_item_ids = new[] { item2 }, tenders = new[] { new { method = "cash", amount = 25000 } } }, Guid.NewGuid())).Code);

        // سجل التدقيق: للمدير العام والمحاسب، ولمدير الفرع بنطاق فرعه (عيب D3)، وغيرهم ممنوع
        var logs = await Api.GetAsync(admin, $"/api/v1/reports/audit-logs?action=deferral_written_off&entity_id={d2}");
        Assert.Equal(200, logs.Status);
        var log = logs.Body!["items"]![0]!;
        Assert.Equal("إعفاء نهائي", S(log, "reason"));
        Assert.Equal(25000, L(log["after"], "written_off_amount"));
        var (_, accountantToken) = await app.CreateStaffAsync("accountant");
        var granted = await Api.GetAsync(app.Client(accountantToken), $"/api/v1/reports/audit-logs?action=DEFERRAL_GRANTED&entity_id={deferralId}");
        Assert.Equal(1, granted.Body!["total"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(S(granted.Body!["items"]![0], "user_name")));
        Assert.Equal(200, (await Api.GetAsync(manager, "/api/v1/reports/audit-logs")).Status);
        Assert.Equal(403, (await Api.GetAsync(s.Reception, "/api/v1/reports/audit-logs")).Status);

        var revenue = await Api.GetAsync(manager, "/api/v1/reports/daily-revenue");
        Assert.True(L(revenue.Body, "deferred_new") >= 50000);
    }
}
