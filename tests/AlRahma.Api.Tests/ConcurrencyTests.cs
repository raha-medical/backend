using System.Text.Json.Nodes;
using Xunit;

namespace AlRahma.Api.Tests;

/// <summary>المرحلة 1ج: الأقفال (H2، M3) والاسترجاع بطريقة الدفع (M2). كل حالة تُظهر الخطأ لو أُزيل القفل.</summary>
[Collection(ApiCollection.Name)]
public class ConcurrencyTests(TestApp app)
{
    private static Guid Id(JsonNode? n, string key) => n![key]!.GetValue<Guid>();
    private static string S(JsonNode? n, string key) => n![key]!.GetValue<string>();

    private async Task<(Guid Patient, Guid Ticket, Guid Item)> PaidVisitAsync(QueueTests q, ClinicSetup s, string method = "cash")
    {
        var patient = await q.PatientAsync(s.Reception);
        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.True(checkIn.Status is 200 or 201, checkIn.Raw);
        var ticket = Id(checkIn.Body, "ticket_id");
        var item = await app.ScalarAsync<Guid>($"SELECT invoice_item_id FROM invoice_items WHERE ticket_id = '{ticket}'");
        object tender = method == "pos"
            ? new { method = "pos", amount = 25000, pos_terminal = "qi_1", pos_reference = $"TX-{Guid.NewGuid():N}"[..12] }
            : new { method = "cash", amount = 25000 };
        var paid = await Api.PostAsync(s.Reception, "/api/v1/payments", new { patient_id = patient, invoice_item_ids = new[] { item }, tenders = new[] { tender } }, Guid.NewGuid());
        Assert.True(paid.Status == 201, paid.Raw);
        return (patient, ticket, item);
    }

    [SkippableFact]
    public async Task Two_patients_started_at_once_only_one_enters_the_room()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid())).Status);
        var a = await PaidVisitAsync(q, s);
        var b = await PaidVisitAsync(q, s);

        var results = await Task.WhenAll(
            Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{a.Ticket}/start"),
            Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{b.Ticket}/start"));
        Assert.Equal(1, results.Count(r => r.Status == 200));
        Assert.Equal("DOCTOR_BUSY", results.Single(r => r.Status != 200).Code);
        Assert.Equal(1, await app.ScalarAsync<int>($"SELECT count(*)::int FROM queue_tickets WHERE doctor_id = '{s.DoctorId}' AND status = 'in_room'"));
    }

    [SkippableFact]
    public async Task Double_click_on_start_is_applied_once()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid())).Status);
        var a = await PaidVisitAsync(q, s);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{a.Ticket}/start")));
        Assert.Equal(1, results.Count(r => r.Status == 200));
    }

    [SkippableFact]
    public async Task Payment_racing_shift_close_is_either_counted_or_rejected()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        var shift = await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 10000 }, Guid.NewGuid());
        var shiftId = Id(shift.Body, "shift_id");
        var patient = await q.PatientAsync(s.Reception);
        var checkIn = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        var item = await app.ScalarAsync<Guid>($"SELECT invoice_item_id FROM invoice_items WHERE ticket_id = '{Id(checkIn.Body, "ticket_id")}'");

        var pay = Api.PostAsync(s.Reception, "/api/v1/payments",
            new { patient_id = patient, invoice_item_ids = new[] { item }, tenders = new[] { new { method = "cash", amount = 25000 } } }, Guid.NewGuid());
        var close = Api.PostAsync(s.Reception, $"/api/v1/shifts/{shiftId}/close",
            new { declared = new { cash = 35000, pos_slips_count = 0, pos_total = 0 } }, Guid.NewGuid());
        var (p, c) = (await pay, await close);
        Assert.True(c.Status == 200, c.Raw);

        // الثابت: المتوقع في الدرج = الفكة + كل نقد دخل هذه الوردية، سواء سبق الدفع الإغلاق أو رُفض بعده
        var expected = await app.ScalarAsync<long>($"SELECT expected_cash FROM shifts WHERE shift_id = '{shiftId}'");
        var cashIn = await app.ScalarAsync<long>($"""
            SELECT COALESCE(sum(t.amount), 0)::bigint FROM payment_tenders t JOIN payments p ON p.payment_id = t.payment_id
             WHERE p.shift_id = '{shiftId}' AND t.method = 'cash'
            """);
        Assert.Equal(10000 + cashIn, expected);
        if (p.Status == 201) Assert.Equal(25000, cashIn);
        else Assert.Equal("NO_OPEN_SHIFT", p.Code);
    }

    [SkippableFact]
    public async Task Cash_refund_of_a_card_payment_needs_the_accountant()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var q = new QueueTests(app);
        var s = await q.SetupAsync();
        Assert.Equal(201, (await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid())).Status);
        var byCard = await PaidVisitAsync(q, s, "pos");

        // تحت حد الاستقبال (50,000) لكن بطريقة مختلفة عن الدفع: ينتظر الموافقة
        var cash = await Api.PostAsync(s.Reception, $"/api/v1/invoice-items/{byCard.Item}/refunds", new { amount = 1000, method = "cash", reason = "تجربة" }, Guid.NewGuid());
        Assert.Equal(201, cash.Status);
        Assert.Equal("pending_approval", S(cash.Body, "status"));
        // بنفس الطريقة: فوري كما كان
        var pos = await Api.PostAsync(s.Reception, $"/api/v1/invoice-items/{byCard.Item}/refunds", new { amount = 1000, method = "pos", reason = "تجربة" }, Guid.NewGuid());
        Assert.Equal("completed", S(pos.Body, "status"));
        // رصيد للمريض لا يخرج من الدرج: فوري
        var credit = await Api.PostAsync(s.Reception, $"/api/v1/invoice-items/{byCard.Item}/refunds", new { amount = 1000, method = "patient_credit", reason = "تجربة" }, Guid.NewGuid());
        Assert.Equal("completed", S(credit.Body, "status"));
    }
}
