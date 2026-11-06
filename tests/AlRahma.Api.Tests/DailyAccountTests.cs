using System.Text.Json.Nodes;
using AlRahma.Application.Billing;
using Microsoft.Extensions.DependencyInjection;

namespace AlRahma.Api.Tests;

/// <summary>الحساب اليومي (قرار المالك، البند 5): الملخص للموظف، الوصل غير المطبوع، ذمة الموظف، الإغلاق التلقائي، ورفض حساب الأمس.</summary>
[Collection(ApiCollection.Name)]
public class DailyAccountTests(TestApp app)
{
    private static Guid Id(JsonNode? n, string k) => n![k]!.GetValue<Guid>();
    private static string S(JsonNode? n, string k) => n![k]!.GetValue<string>();
    private static long L(JsonNode? n, string k) => n![k]!.GetValue<long>();
    private static int I(JsonNode? n, string k) => n![k]!.GetValue<int>();

    [SkippableFact]
    public async Task Cashier_sees_cash_electronic_refunds_unpaid_and_closes_with_difference_carried_to_accountant()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var shiftId = Id((await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid())).Body, "shift_id");
        var (_, accToken) = await app.CreateStaffAsync("accountant");
        var acc = app.Client(accToken);

        // مراجع نقدي + مراجع بتطبيق يدفع إلكترونياً، وكلاهما أكّد حضورهما هذا الموظف
        var cashPatient = await q.PatientAsync(s.Reception);
        var t1 = Id((await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = cashPatient, doctor_id = s.DoctorId }, Guid.NewGuid())).Body, "ticket_id");
        var cashItem = await app.ScalarAsync<Guid>($"SELECT invoice_item_id FROM invoice_items WHERE ticket_id = '{t1}'");
        var phone = "0776" + Random.Shared.Next(1000000, 9999999);
        var ep = Id((await Api.PostAsync(s.Reception, "/api/v1/patients", new { full_name = "مريض إلكتروني", phone_number = phone, gender = "male", approx_age = 30 }, Guid.NewGuid())).Body, "patient_id");
        var t2 = Id((await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = ep, doctor_id = s.DoctorId }, Guid.NewGuid())).Body, "ticket_id");
        var eItem = await app.ScalarAsync<Guid>($"SELECT invoice_item_id FROM invoice_items WHERE ticket_id = '{t2}'");
        var eInvoice = await app.ScalarAsync<Guid>($"SELECT invoice_id FROM invoice_items WHERE invoice_item_id = '{eItem}'");

        var cur = await Api.GetAsync(s.Reception, "/api/v1/shifts/current");
        Assert.Equal(2, I(cur.Body!["summary"], "checked_in_count"));
        Assert.Equal(2, I(cur.Body!["summary"], "unpaid_count"));

        // نقد
        var paid = await Api.PostAsync(s.Reception, "/api/v1/payments", new { patient_id = cashPatient, invoice_item_ids = new[] { cashItem }, tenders = new[] { new { method = "cash", amount = 25000 } } }, Guid.NewGuid());
        Assert.Equal(201, paid.Status);
        // إلكتروني من التطبيق لمراجع أكّدتُ حضوره
        var otp = await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/request", new { phone_number = phone });
        var me = app.Client(S((await Api.PostAsync(app.Client(), "/api/v1/patient-auth/otp/verify", new { phone_number = phone, code = S(otp.Body, "sandbox_code") })).Body, "access_token"));
        var op = await Api.PostAsync(me, "/api/v1/online-payments", new { purpose = "invoice", patient_id = ep, invoice_id = eInvoice, invoice_item_id = eItem, provider = "zaincash" }, Guid.NewGuid());
        Assert.Equal("succeeded", S((await MockPay.SucceedAsync(me, Id(op.Body, "online_payment_id"))).Body, "status"));

        // الاسترجاع النقدي اليدوي ينتظر موافقة المحاسب مهما صغر
        var rq = await Api.PostAsync(s.Reception, $"/api/v1/invoice-items/{cashItem}/refunds", new { amount = 1000, method = "cash", reason = "خطأ إدخال" }, Guid.NewGuid());
        Assert.Equal("pending_approval", S(rq.Body, "status"));
        Assert.Equal("completed", S((await Api.PostAsync(acc, $"/api/v1/refunds/{Id(rq.Body, "refund_id")}/approve")).Body, "status"));

        var sum = (await Api.GetAsync(s.Reception, "/api/v1/shifts/current")).Body!["summary"];
        Assert.Equal(25000, L(sum, "cash_received"));
        Assert.Equal(1000, L(sum, "cash_refunds"));
        Assert.Equal(24000, L(sum, "cash_in_hand"));                  // ما في ذمته ويسلّمه
        Assert.Equal(25000, L(sum, "electronic_for_my_check_ins"));   // مفصول عن النقد
        Assert.Equal(1, I(sum, "refunds_count"));
        Assert.Equal(0, I(sum, "unpaid_count"));
        Assert.Equal(2, sum!["unprinted_receipts"]!.AsArray().Count);  // وصل التحصيل ووصل الاسترجاع
        Assert.False(sum["from_previous_day"]!.GetValue<bool>());
        // لا يرى حسابات غيره ولا مطابقتها
        Assert.Equal(403, (await Api.GetAsync(s.Reception, "/api/v1/shifts")).Status);
        Assert.Equal(403, (await Api.GetAsync(s.Reception, $"/api/v1/shifts/{shiftId}/reconciliation")).Status);
        Assert.Equal(403, (await Api.GetAsync(s.Reception, "/api/v1/shifts/liabilities")).Status);

        // الإغلاق: الوصل غير المطبوع لا يمنع، لكن يُطلب سبب أو طباعة
        var blocked = await Api.PostAsync(s.Reception, $"/api/v1/shifts/{shiftId}/close", new { declared = new { cash = 23500 } }, Guid.NewGuid());
        Assert.Equal("UNPRINTED_RECEIPTS", blocked.Code);
        var unprinted = blocked.Body!["unprinted_receipts"]!.AsArray();
        Assert.Equal(204, (await Api.PostAsync(s.Reception, $"/api/v1/receipts/{unprinted[0]!["receipt_id"]!.GetValue<Guid>()}/printed")).Status);
        var reasons = new Dictionary<string, string> { [unprinted[1]!["receipt_id"]!.GetValue<string>()] = "نفد ورق الطابعة" };
        var closed = await Api.PostAsync(s.Reception, $"/api/v1/shifts/{shiftId}/close", new { declared = new { cash = 23500 }, unprinted_reasons = reasons }, Guid.NewGuid());
        Assert.True(closed.Status == 200, closed.Raw);
        Assert.Equal("closed_pending_review", S(closed.Body, "status"));

        // المحاسب يرى الفرق باسم الموظف وسبب الوصل غير المطبوع، ثم يراجع فيُرحَّل
        var rec = await Api.GetAsync(acc, $"/api/v1/shifts/{shiftId}/reconciliation");
        Assert.Equal("SHORTAGE", S(rec.Body, "audit_status"));
        Assert.Equal(-500, L(rec.Body, "variance_total"));
        Assert.Equal("نفد ورق الطابعة", S(rec.Body!["unprinted_receipts"]![0], "reason"));
        Assert.Equal(25000, L(rec.Body, "electronic_for_check_ins"));
        var liab = (await Api.GetAsync(acc, $"/api/v1/shifts/liabilities?status=pending")).Body!["items"]!.AsArray().First(x => Id(x, "shift_id") == shiftId);
        Assert.Equal(-500, L(liab, "amount"));
        Assert.Equal("shortage", S(liab, "kind"));
        Assert.Equal("reviewed", S((await Api.PostAsync(acc, $"/api/v1/shifts/{shiftId}/review", new { decision = "accepted", note = "يُقيد على الموظف" })).Body, "status"));
        var after = (await Api.GetAsync(acc, $"/api/v1/shifts/liabilities")).Body!["items"]!.AsArray().First(x => Id(x, "shift_id") == shiftId);
        Assert.Equal("accepted", S(after, "status"));
    }

    [SkippableFact]
    public async Task Previous_day_account_rejects_collection_auto_closes_and_owner_acknowledges()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var shiftId = Id((await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 5000 }, Guid.NewGuid())).Body, "shift_id");
        var patient = await q.PatientAsync(s.Reception);
        var t = Id((await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid())).Body, "ticket_id");
        var item = await app.ScalarAsync<Guid>($"SELECT invoice_item_id FROM invoice_items WHERE ticket_id = '{t}'");
        await app.ExecAsync($"UPDATE shifts SET opened_at = opened_at - interval '1 day' WHERE shift_id = '{shiftId}'");

        var pay = await Api.PostAsync(s.Reception, "/api/v1/payments", new { patient_id = patient, invoice_item_ids = new[] { item }, tenders = new[] { new { method = "cash", amount = 25000 } } }, Guid.NewGuid());
        Assert.Equal(409, pay.Status);
        Assert.Equal("SHIFT_FROM_PREVIOUS_DAY", pay.Code);
        Assert.True((await Api.GetAsync(s.Reception, "/api/v1/shifts/current")).Body!["summary"]!["from_previous_day"]!.GetValue<bool>());

        // المهمة الدورية: إغلاق تلقائي بانتظار إقرار الموظف
        using (var scope = app.Factory.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<ShiftService>().AutoClosePreviousDayAsync(default) >= 1);
        Assert.Equal("closed_pending_review", await app.ScalarAsync<string>($"SELECT status FROM shifts WHERE shift_id = '{shiftId}'"));
        Assert.True(await app.ScalarAsync<bool>($"SELECT auto_closed FROM shifts WHERE shift_id = '{shiftId}'"));
        Assert.Equal(404, (await Api.GetAsync(s.Reception, "/api/v1/shifts/current")).Status);

        // صاحبه يقرّ بالمبلغ المعدود بعد الإغلاق التلقائي
        var ack = await Api.PostAsync(s.Reception, $"/api/v1/shifts/{shiftId}/close", new { declared = new { cash = 5000 } }, Guid.NewGuid());
        Assert.True(ack.Status == 200, ack.Raw);
        Assert.Equal(5000, await app.ScalarAsync<long>($"SELECT declared_cash FROM shifts WHERE shift_id = '{shiftId}'"));
        Assert.Equal("MATCHED", await app.ScalarAsync<string>($"SELECT audit_status FROM shifts WHERE shift_id = '{shiftId}'"));

        // حساب اليوم الجديد يقبل التحصيل
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid())).Status);
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/payments", new { patient_id = patient, invoice_item_ids = new[] { item }, tenders = new[] { new { method = "cash", amount = 25000 } } }, Guid.NewGuid())).Status);
    }

    [SkippableFact]
    public async Task Distributed_collection_mode_limits_items_to_the_point_department()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var (_, labCashierToken) = await app.CreateStaffAsync("receptionist", clinics: [app.LabBranch1]);
        var labCashier = app.Client(labCashierToken);
        Assert.Equal(201, (await Api.PostAsync(labCashier, "/api/v1/shifts/open", new { opening_float = 0, clinic_id = app.LabBranch1 }, Guid.NewGuid())).Status);
        var patient = await q.PatientAsync(s.Reception);
        var t = Id((await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid())).Body, "ticket_id");
        var item = await app.ScalarAsync<Guid>($"SELECT invoice_item_id FROM invoice_items WHERE ticket_id = '{t}'");
        var body = new { patient_id = patient, invoice_item_ids = new[] { item }, tenders = new[] { new { method = "cash", amount = 25000 } } };
        await app.ExecAsync($"INSERT INTO system_settings (branch_id, key, value) VALUES ('{app.Branch1}', 'billing.collection_mode', '\"distributed\"')");
        try
        {
            Assert.Equal("ITEM_OUTSIDE_COLLECTION_POINT", (await Api.PostAsync(labCashier, "/api/v1/payments", body, Guid.NewGuid())).Code);
        }
        finally
        {
            await app.ExecAsync($"DELETE FROM system_settings WHERE branch_id = '{app.Branch1}' AND key = 'billing.collection_mode'");
        }
        // المركزي (الافتراضي): نفس البند يُحصَّل من أي نقطة
        Assert.Equal(201, (await Api.PostAsync(labCashier, "/api/v1/payments", body, Guid.NewGuid())).Status);
    }
}
