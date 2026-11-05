using System.Text.Json.Nodes;
using Xunit;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class BillingTests(TestApp app)
{
    private static Guid Id(JsonNode? n, string key) => n![key]!.GetValue<Guid>();
    private static long L(JsonNode? n, string key) => n![key]!.GetValue<long>();
    private static string S(JsonNode? n, string key) => n![key]!.GetValue<string>();

    [SkippableFact]
    public async Task Cashier_collects_refunds_and_closes_shift_blind()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var patient = await q.PatientAsync(s.Reception);
        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        var ticket = Id(checkIn.Body, "ticket_id");

        var basket = await Api.GetAsync(s.Reception, $"/api/v1/patients/{patient}/billable-items?department=clinic");
        Assert.Equal(25000, L(basket.Body, "total_due"));
        var item = Id(basket.Body!["items"]![0], "invoice_item_id");
        Assert.True(basket.Body!["items"]![0]!["is_current_department"]!.GetValue<bool>());

        var pay = new
        {
            patient_id = patient, invoice_item_ids = new[] { item },
            tenders = new object[] { new { method = "cash", amount = 20000 }, new { method = "pos", amount = 5000, pos_terminal = "qi_1", pos_reference = "TX-1" } },
        };
        Assert.Equal("NO_OPEN_SHIFT", (await Api.PostAsync(s.Reception, "/api/v1/payments", pay, Guid.NewGuid())).Code);

        var shift = await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 50000 }, Guid.NewGuid());
        Assert.Equal(201, shift.Status);
        var shiftId = Id(shift.Body, "shift_id");
        Assert.Equal("SHIFT_ALREADY_OPEN", (await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid())).Code);

        var wrong = await Api.PostAsync(s.Reception, "/api/v1/payments",
            new { patient_id = patient, invoice_item_ids = new[] { item }, tenders = new[] { new { method = "cash", amount = 1000 } } }, Guid.NewGuid());
        Assert.Equal("AMOUNT_MISMATCH", wrong.Code);
        Assert.Equal(25000, wrong.Body!["expected_amount"]!.GetValue<long>());

        var paid = await Api.PostAsync(s.Reception, "/api/v1/payments", pay, Guid.NewGuid());
        Assert.Equal(201, paid.Status);
        Assert.True(paid.Body!["open_drawer"]!.GetValue<bool>());
        Assert.Equal(3, S(paid.Body, "drawer_token").Split('.').Length);
        var receipt = paid.Body!["receipt"]!;
        Assert.Contains("-R-", S(receipt, "receipt_number"));
        Assert.Equal("ITEM_NOT_PAYABLE", (await Api.PostAsync(s.Reception, "/api/v1/payments", pay, Guid.NewGuid())).Code);

        // الكشفية مدفوعة: الطابور يعرضها مدفوعة والطبيب يبدأ المعاينة
        var live = await Api.GetAsync(s.Reception, $"/api/v1/queue/live?doctor_id={s.DoctorId}");
        Assert.Equal("paid", S(live.Body!["items"]!.AsArray().First(t => Id(t, "ticket_id") == ticket), "payment_status"));
        await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/call");
        Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{ticket}/start")).Status);

        // الإيصال: بيانات الطباعة، والتحقق العام من الـ QR، وإعادة الطباعة مرة واحدة للاستقبال
        var receiptId = Id(receipt, "receipt_id");
        var print = await Api.GetAsync(s.Reception, $"/api/v1/receipts/{receiptId}/print-payload");
        Assert.Equal(25000, L(print.Body, "total"));
        Assert.Equal(2, print.Body!["tenders"]!.AsArray().Count);
        Assert.NotNull(print.Body!["doctor_name"]); // الإيصال يذكر الطبيب ورقم الدور
        Assert.NotNull(print.Body!["ticket_number"]);
        Assert.NotNull(print.Body!["lines"]![0]!["doctor_name"]);
        var mine = await Api.GetAsync(s.Reception, "/api/v1/receipts");
        Assert.True(mine.Status == 200, mine.Raw);
        Assert.Contains(mine.Body!["items"]!.AsArray(), x => Id(x, "receipt_id") == receiptId && x!["can_reprint"]!.GetValue<bool>());
        var code = S(receipt, "qr_code").Split('/').Last();
        var verify = await Api.GetAsync(app.Client(), $"/api/v1/public/receipts/verify/{code}");
        Assert.True(verify.Body!["valid"]!.GetValue<bool>());
        Assert.Null(verify.Body!["patient_name"]);
        Assert.Equal(404, (await Api.GetAsync(app.Client(), "/api/v1/public/receipts/verify/FAKE-CODE-00")).Status);
        Assert.Equal(2, (await Api.PostAsync(s.Reception, $"/api/v1/receipts/{receiptId}/reprint")).Body!["copy_number"]!.GetValue<int>());
        Assert.Equal("REPRINT_LIMIT", (await Api.PostAsync(s.Reception, $"/api/v1/receipts/{receiptId}/reprint")).Code);

        // استرجاع نقدي ضمن حد الاستقبال: فوري، وحصة الطبيب تنقص بنسبتها (70%)
        var refund = await Api.PostAsync(s.Reception, $"/api/v1/invoice-items/{item}/refunds",
            new { amount = 10000, method = "cash", reason = "خطأ في التسعير" }, Guid.NewGuid());
        Assert.Equal(201, refund.Status);
        Assert.Equal("completed", S(refund.Body, "status"));
        Assert.Equal(-7000, L(refund.Body, "doctor_share_adjustment"));
        Assert.Equal(-3000, L(refund.Body, "complex_share_adjustment"));
        Assert.Equal(15000, L(refund.Body, "invoice_paid_after"));
        Assert.True(refund.Body!["open_drawer"]!.GetValue<bool>());
        Assert.Contains("-RF-", S(refund.Body!["receipt"], "receipt_number"));
        Assert.Equal("REFUND_EXCEEDS_PAID", (await Api.PostAsync(s.Reception, $"/api/v1/invoice-items/{item}/refunds",
            new { amount = 20000, method = "cash", reason = "x" }, Guid.NewGuid())).Code);
        Assert.Equal("partially_refunded", S((await Api.GetAsync(app.Client(), $"/api/v1/public/receipts/verify/{code}")).Body, "status"));

        // بعد إنهاء المعاينة: البند منفذ، واسترجاعه للمحاسب أو المدير فقط
        await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{ticket}/complete");
        Assert.Equal("ITEM_ALREADY_PERFORMED", (await Api.PostAsync(s.Reception, $"/api/v1/invoice-items/{item}/refunds",
            new { amount = 1000, method = "cash", reason = "x" }, Guid.NewGuid())).Code);

        // الإغلاق الأعمى: الموظف يدخل ما عدّه (ناقص 1000)، ولا يرى المتوقع
        Assert.Equal(403, (await Api.GetAsync(s.Reception, $"/api/v1/shifts/{shiftId}/reconciliation")).Status);
        var close = await Api.PostAsync(s.Reception, $"/api/v1/shifts/{shiftId}/close",
            new { declared = new { cash = 59000, pos_slips_count = 1, pos_total = 5000 }, cash_denominations = new Dictionary<string, int> { ["25000"] = 2, ["5000"] = 1, ["1000"] = 4 } },
            Guid.NewGuid());
        Assert.Equal("closed_pending_review", S(close.Body, "status"));
        Assert.Null(close.Body!["expected_cash"]);
        Assert.Equal(404, (await Api.GetAsync(s.Reception, "/api/v1/shifts/current")).Status);

        var (_, accountantToken) = await app.CreateStaffAsync("accountant");
        var accountant = app.Client(accountantToken);
        var rec = await Api.GetAsync(accountant, $"/api/v1/shifts/{shiftId}/reconciliation");
        var cash = rec.Body!["lines"]!.AsArray().First(l => S(l, "method") == "cash");
        Assert.Equal(60000, L(cash, "expected")); // 50000 فكة + 20000 نقد − 10000 مسترجع
        Assert.Equal(-1000, L(cash, "variance"));
        Assert.Equal("SHORTAGE", S(rec.Body, "audit_status"));
        Assert.Equal(1, rec.Body!["refunds_count"]!.GetValue<int>());
        Assert.Equal(400, (await Api.PostAsync(accountant, $"/api/v1/shifts/{shiftId}/review", new { decision = "accepted" })).Status);
        var reviewed = await Api.PostAsync(accountant, $"/api/v1/shifts/{shiftId}/review", new { decision = "accepted", note = "يُخصم العجز من الموظف" });
        Assert.Equal("reviewed", S(reviewed.Body, "status"));
        var list = await Api.GetAsync(accountant, "/api/v1/shifts?status=reviewed");
        Assert.Contains(list.Body!["items"]!.AsArray(), x => Id(x, "shift_id") == shiftId);
    }

    [SkippableFact]
    public async Task Manual_invoice_discount_deposit_credit_and_refund_approval()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var lab = await app.ScalarAsync<Guid>($"""
            INSERT INTO services (branch_id, clinic_id, name, type, price) VALUES ('{app.Branch1}', '{app.LabBranch1}', 'فحص شامل {Guid.NewGuid().ToString()[..4]}', 'lab', 100000)
            RETURNING service_id
            """);
        var (_, recToken) = await app.CreateStaffAsync("receptionist", clinics: [app.LabBranch1], extraPermissions: ["patients.unban"]);
        var rec = app.Client(recToken);
        var (_, discountToken) = await app.CreateStaffAsync("receptionist", clinics: [app.LabBranch1], extraPermissions: ["discounts.grant"]);
        var (_, accountantToken) = await app.CreateStaffAsync("accountant");
        var accountant = app.Client(accountantToken);
        var patient = await new QueueTests(app).PatientAsync(rec);

        var discount = new { type = "percent", value = 10, reason = "موظف في المجمع" };
        Assert.Equal(403, (await Api.PostAsync(rec, "/api/v1/invoices", new { patient_id = patient, items = new[] { new { service_id = lab } }, discount }, Guid.NewGuid())).Status);
        var invoice = await Api.PostAsync(app.Client(discountToken), "/api/v1/invoices",
            new { patient_id = patient, items = new[] { new { service_id = lab, quantity = 1 } }, discount }, Guid.NewGuid());
        Assert.Equal(201, invoice.Status);
        Assert.Equal(90000, L(invoice.Body, "net_amount"));
        Assert.Contains("-INV-", S(invoice.Body, "invoice_number"));
        var item = Id(invoice.Body!["items"]![0], "invoice_item_id");
        Assert.Equal("lab", S(invoice.Body!["items"]![0], "department"));

        // فاتورة خاطئة تُلغى قبل الدفع
        var wrong = await Api.PostAsync(rec, "/api/v1/invoices", new { patient_id = patient, items = new[] { new { service_id = lab } } }, Guid.NewGuid());
        Assert.Equal(403, (await Api.PostAsync(rec, $"/api/v1/invoices/{Id(wrong.Body, "invoice_id")}/void", new { reason = "مكرر" })).Status);
        Assert.Equal("void", S((await Api.PostAsync(accountant, $"/api/v1/invoices/{Id(wrong.Body, "invoice_id")}/void", new { reason = "مكرر" })).Body, "status"));

        // فك الحظر بعربون نقدي: إيصال عربون، ورصيد للمريض
        await app.ExecAsync($"UPDATE patients SET is_banned = true, banned_at = now(), ban_reason = 'manual' WHERE patient_id = '{patient}'");
        Assert.Equal("NO_OPEN_SHIFT", (await Api.PostAsync(rec, $"/api/v1/patients/{patient}/unban", new { method = "cash", amount = 10000 }, Guid.NewGuid())).Code);
        await Api.PostAsync(rec, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid());
        Assert.Equal(400, (await Api.PostAsync(rec, $"/api/v1/patients/{patient}/unban", new { method = "cash", amount = 500 }, Guid.NewGuid())).Status);
        var unban = await Api.PostAsync(rec, $"/api/v1/patients/{patient}/unban", new { method = "cash", amount = 10000 }, Guid.NewGuid());
        Assert.Equal(200, unban.Status);
        Assert.False(unban.Body!["is_banned"]!.GetValue<bool>());
        Assert.Equal(10000, L(unban.Body, "deposit_credit"));
        Assert.Equal("deposit", S(unban.Body!["receipt"], "type"));
        Assert.True(unban.Body!["open_drawer"]!.GetValue<bool>());

        // التحصيل باستخدام الرصيد
        var basket = await Api.GetAsync(rec, $"/api/v1/patients/{patient}/billable-items?department=lab");
        Assert.Equal(10000, L(basket.Body, "deposit_credit"));
        Assert.Equal("INSUFFICIENT_CREDIT", (await Api.PostAsync(rec, "/api/v1/payments",
            new { patient_id = patient, invoice_item_ids = new[] { item }, use_deposit_credit = 20000, tenders = new[] { new { method = "cash", amount = 70000 } } }, Guid.NewGuid())).Code);
        var paid = await Api.PostAsync(rec, "/api/v1/payments",
            new { patient_id = patient, invoice_item_ids = new[] { item }, use_deposit_credit = 10000, tenders = new[] { new { method = "cash", amount = 80000 } } }, Guid.NewGuid());
        Assert.Equal(201, paid.Status);
        Assert.Equal(10000, L(paid.Body, "deposit_used"));
        Assert.Equal(0, L((await Api.GetAsync(rec, $"/api/v1/patients/{patient}/billable-items")).Body, "deposit_credit"));

        // استرجاع فوق حد الاستقبال (50000): ينتظر موافقة المحاسب، والنقد يخرج من درج الطالب
        var pending = await Api.PostAsync(rec, $"/api/v1/invoice-items/{item}/refunds", new { amount = 60000, method = "cash", reason = "ألغى المريض التحليل" }, Guid.NewGuid());
        Assert.Equal("pending_approval", S(pending.Body, "status"));
        Assert.Null(pending.Body!["receipt"]);
        var refundId = Id(pending.Body, "refund_id");
        Assert.Equal(403, (await Api.PostAsync(rec, $"/api/v1/refunds/{refundId}/approve")).Status);
        var queue = await Api.GetAsync(accountant, "/api/v1/refunds?status=pending_approval");
        Assert.Contains(queue.Body!["items"]!.AsArray(), r => Id(r, "refund_id") == refundId);
        var approved = await Api.PostAsync(accountant, $"/api/v1/refunds/{refundId}/approve");
        Assert.Equal("completed", S(approved.Body, "status"));
        Assert.Equal(30000, L(approved.Body, "invoice_paid_after"));
        Assert.Equal("REFUND_NOT_PENDING", (await Api.PostAsync(accountant, $"/api/v1/refunds/{refundId}/reject", new { reason = "x" })).Code);

        // استرجاع كرصيد للمريض
        var credit = await Api.PostAsync(rec, $"/api/v1/invoice-items/{item}/refunds", new { amount = 30000, method = "patient_credit", reason = "يُستخدم لاحقاً" }, Guid.NewGuid());
        Assert.Equal("completed", S(credit.Body, "status"));
        Assert.False(credit.Body!["open_drawer"]!.GetValue<bool>());
        var full = await Api.GetAsync(accountant, $"/api/v1/invoices/{Id(invoice.Body, "invoice_id")}");
        Assert.Equal("refunded", S(full.Body!["items"]![0], "status"));
        Assert.Equal(90000, L(full.Body, "refunded_amount"));
        Assert.Equal(2, full.Body!["refunds"]!.AsArray().Count);
        Assert.Single(full.Body!["payments"]!.AsArray());
        Assert.Equal(30000, L((await Api.GetAsync(rec, $"/api/v1/patients/{patient}/billable-items")).Body, "deposit_credit"));
        var list = await Api.GetAsync(accountant, $"/api/v1/invoices?patient_id={patient}");
        Assert.Equal(2, list.Body!["total"]!.GetValue<int>());
    }
}
