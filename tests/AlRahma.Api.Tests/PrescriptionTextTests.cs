using System.Text.Json.Nodes;
using Xunit;
namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class PrescriptionTextTests(TestApp app)
{
    private static Guid Id(JsonNode? n, string k) => n![k]!.GetValue<Guid>();
    [SkippableFact]
    public async Task Text_is_preserved_without_parsing_and_pharmacist_bills_multiple_drugs_before_dispense()
    {
        Skip.If(TestApp.ServerUrl is null);
        var setup = new QueueTests(app);
        var s = await setup.SetupAsync();
        var patient = await setup.PatientAsync(s.Reception, "مراجع وصفة نصية");
        var checkin = await Api.PostAsync(s.Reception, "/api/v1/queue/check-in", new { patient_id = patient, doctor_id = s.DoctorId }, Guid.NewGuid());
        Assert.True(checkin.Status == 201, checkin.Raw);
        var ticket = Id(checkin.Body, "ticket_id");
        await Api.PostAsync(s.Reception, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid());
        var visitItem = await app.ScalarAsync<Guid>($"SELECT invoice_item_id FROM invoice_items WHERE ticket_id = '{ticket}'");
        var paidVisit = await Api.PostAsync(s.Reception, "/api/v1/payments", new { patient_id = patient, invoice_item_ids = new[] { visitItem }, tenders = new[] { new { method = "cash", amount = checkin.Body!["amount_due"]!.GetValue<long>() } } }, Guid.NewGuid());
        Assert.True(paidVisit.Status == 201, paidVisit.Raw);
        await Api.PostAsync(s.Reception, $"/api/v1/queue/tickets/{ticket}/call");
        Assert.Equal(200, (await Api.PostAsync(s.Doctor, $"/api/v1/queue/tickets/{ticket}/start")).Status);
        const string text = "علاج أول 500mg\nالتفاصيل والتكرار والمدة كما كتبها الطبيب\n\nعلاج ثانٍ — تعليمات إضافية <test>";
        var created = await Api.PostAsync(s.Doctor, "/api/v1/consultations", new { ticket_id = ticket, prescription_text = text }, Guid.NewGuid());
        Assert.True(created.Status == 201, created.Raw);
        var consult = Id(created.Body, "consultation_id");
        Assert.Equal(text, created.Body!["prescription"]!["text"]!.GetValue<string>());
        Assert.Empty(created.Body["prescription"]!["lines"]!.AsArray());
        Assert.Equal(400, (await Api.PatchAsync(s.Doctor, $"/api/v1/consultations/{consult}", new { prescription_text = new string('x', 10001) })).Status);
        Assert.Equal(200, (await Api.PatchAsync(s.Doctor, $"/api/v1/consultations/{consult}", new { prescription_text = "" })).Status);
        var restored = await Api.PatchAsync(s.Doctor, $"/api/v1/consultations/{consult}", new { prescription_text = text });
        Assert.True(restored.Status == 200, restored.Raw);
        var rx = Id(restored.Body!["prescription"], "prescription_id");
        var code = restored.Body["prescription"]!["prescription_code"]!.GetValue<string>();
        var (_, pharmToken) = await app.CreateStaffAsync("pharmacist");
        var pharm = app.Client(pharmToken);
        var read = await Api.GetAsync(pharm, $"/api/v1/pharmacy/prescriptions/{code}");
        Assert.Equal(text, read.Body!["text"]!.GetValue<string>());
        Assert.Empty(read.Body["items"]!.AsArray());
        Assert.Equal(422, (await Api.PostAsync(pharm, $"/api/v1/pharmacy/prescriptions/{rx}/dispense", new { lines = Array.Empty<object>() }, Guid.NewGuid())).Status);
        var drug1 = await Api.PostAsync(pharm, "/api/v1/pharmacy/drugs", new { name = "علاج صرف أول", price = 1000 }, Guid.NewGuid());
        var drug2 = await Api.PostAsync(pharm, "/api/v1/pharmacy/drugs", new { name = "علاج صرف ثان", price = 2000 }, Guid.NewGuid());
        var bill = await Api.PostAsync(pharm, $"/api/v1/pharmacy/prescriptions/{rx}/bill", new { lines = new[] {
            new { line_no = 1, available = true, wanted = true, service_id = Id(drug1.Body,"service_id"), quantity = 2 },
            new { line_no = 2, available = true, wanted = true, service_id = Id(drug2.Body,"service_id"), quantity = 3 }
        } }, Guid.NewGuid());
        Assert.True(bill.Status == 200, bill.Raw);
        Assert.Equal(8000, bill.Body!["total_due"]!.GetValue<long>());
        Assert.Equal(2, bill.Body["lines"]!.AsArray().Count);
        Assert.Equal(409, (await Api.PatchAsync(s.Doctor, $"/api/v1/consultations/{consult}", new { prescription_text = "نص آخر" })).Status);
        var dispense = new { lines = new[] { new { line_no = 1, dispensed = true }, new { line_no = 2, dispensed = true } } };
        Assert.Equal(402, (await Api.PostAsync(pharm, $"/api/v1/pharmacy/prescriptions/{rx}/dispense", dispense, Guid.NewGuid())).Status);
        await Api.PostAsync(pharm, "/api/v1/shifts/open", new { opening_float = 0 }, Guid.NewGuid());
        var itemIds = bill.Body["lines"]!.AsArray().Select(l => Id(l,"invoice_item_id")).ToArray();
        Assert.Equal(201, (await Api.PostAsync(pharm, "/api/v1/payments", new { patient_id = patient, invoice_item_ids = itemIds, tenders = new[] { new { method = "cash", amount = 8000 } } }, Guid.NewGuid())).Status);
        var done = await Api.PostAsync(pharm, $"/api/v1/pharmacy/prescriptions/{rx}/dispense", dispense, Guid.NewGuid());
        Assert.True(done.Status == 200, done.Raw);
        Assert.Equal(text, done.Body!["text"]!.GetValue<string>());
        Assert.Equal(2, done.Body["items"]!.AsArray().Count);
        Assert.Equal(409, (await Api.PostAsync(pharm, $"/api/v1/pharmacy/prescriptions/{rx}/dispense", dispense, Guid.NewGuid())).Status);
        var history = await Api.GetAsync(s.Doctor, $"/api/v1/patients/{patient}/history");
        Assert.Contains(history.Body!["items"]!.AsArray(), row => row!["prescription"]?["text"]?.GetValue<string>() == text);
    }
}
