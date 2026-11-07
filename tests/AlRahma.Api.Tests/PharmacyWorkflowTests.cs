using System.Text.Json.Nodes;
using Xunit;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class PharmacyWorkflowTests(TestApp app)
{
    private static Guid Id(JsonNode? row, string field) => row![field]!.GetValue<Guid>();

    [SkippableFact]
    public async Task Pharmacist_manages_only_branch_drugs_and_audits_price_changes()
    {
        Skip.If(TestApp.ServerUrl is null);
        var (_, token) = await app.CreateStaffAsync("pharmacist");
        var c = app.Client(token);
        var created = await Api.PostAsync(c, "/api/v1/pharmacy/drugs", new { name = "علاج اختبار الصيدلية", price = 2000, requires_prescription = true }, Guid.NewGuid());
        Assert.True(created.Status == 201, created.Raw);
        var id = Id(created.Body, "service_id");
        Assert.Equal("pharmacy", created.Body!["type"]!.GetValue<string>());
        Assert.True(created.Body["requires_prescription"]!.GetValue<bool>());
        var update = await Api.PatchAsync(c, $"/api/v1/pharmacy/drugs/{id}", new { name = "علاج محدّث", price = 2500, reason = "اختبار السعر" });
        Assert.True(update.Status == 200, update.Raw);
        Assert.True(update.Body!["requires_prescription"]!.GetValue<bool>());
        Assert.Equal(1, await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE entity_id = '{id}' AND action = 'PRICE_CHANGED'"));
        Assert.Equal(400, (await Api.PostAsync(c, "/api/v1/pharmacy/drugs", new { name = "سعر خاطئ", price = -1 }, Guid.NewGuid())).Status);
        var labId = Guid.NewGuid(); var foreign = Guid.NewGuid();
        await app.ExecAsync($"INSERT INTO services(service_id, branch_id, name, type, price) VALUES ('{labId}', '{app.Branch1}', 'تحليل حماية', 'lab', 1000), ('{foreign}', '{app.Branch2}', 'علاج فرع آخر', 'pharmacy', 1000)");
        Assert.Equal(403, (await Api.PatchAsync(c, $"/api/v1/pharmacy/drugs/{labId}", new { price = 1 })).Status);
        Assert.Equal(404, (await Api.PatchAsync(c, $"/api/v1/pharmacy/drugs/{foreign}", new { price = 1 })).Status);
        Assert.Equal(403, (await Api.PatchAsync(c, $"/api/v1/services/{id}", new { price = 1 })).Status);
        var (_, viewerToken) = await app.CreateStaffAsync("branch_manager");
        var viewer = app.Client(viewerToken);
        Assert.Equal(200, (await Api.GetAsync(viewer, "/api/v1/pharmacy/drugs")).Status);
        Assert.Equal(403, (await Api.PostAsync(viewer, "/api/v1/pharmacy/drugs", new { name = "ممنوع", price = 1 }, Guid.NewGuid())).Status);
    }

    [SkippableFact]
    public async Task Direct_sales_keep_quantity_price_restrictions_and_can_be_reopened_only_in_branch()
    {
        Skip.If(TestApp.ServerUrl is null);
        var (_, token) = await app.CreateStaffAsync("pharmacist");
        var c = app.Client(token);
        var created = await Api.PostAsync(c, "/api/v1/pharmacy/drugs", new { name = "دواء مباشر", price = 3000 }, Guid.NewGuid());
        var id = Id(created.Body, "service_id");
        var name = "مراجع صيدلية " + Guid.NewGuid().ToString("N")[..8];
        var req = new { new_patient = new { full_name = name, phone_number = "07790001235" }, items = new[] { new { service_id = id, quantity = 3 } } };
        var key = Guid.NewGuid();
        var sale = await Api.PostAsync(c, "/api/v1/pharmacy/external-sales", req, key);
        Assert.True(sale.Status == 201, sale.Raw);
        var invoice = Id(sale.Body, "invoice_id");
        Assert.Equal(9000, sale.Body!["total_due"]!.GetValue<long>());
        Assert.Equal(3, sale.Body["lines"]![0]!["quantity"]!.GetValue<int>());
        var retry = await Api.PostAsync(c, "/api/v1/pharmacy/external-sales", req, key);
        Assert.Equal(invoice, Id(retry.Body, "invoice_id"));
        var list = await Api.GetAsync(c, $"/api/v1/pharmacy/external-sales?q={Uri.EscapeDataString(name)}&page_size=1&status=billed");
        Assert.True(list.Status == 200, list.Raw);
        Assert.Equal(1, list.Body!["total"]!.GetValue<int>());
        Assert.Equal(invoice, Id(list.Body["items"]![0], "invoice_id"));
        Assert.Equal(9000, list.Body["items"]![0]!["amount"]!.GetValue<long>());
        Assert.Equal(200, (await Api.GetAsync(c, $"/api/v1/pharmacy/external-sales/{invoice}")).Status);
        Assert.Equal(402, (await Api.PostAsync(c, $"/api/v1/pharmacy/external-sales/{invoice}/deliver")).Status);
        var (_, foreignToken) = await app.CreateStaffAsync("pharmacist", app.Branch2);
        Assert.Equal(404, (await Api.GetAsync(app.Client(foreignToken), $"/api/v1/pharmacy/external-sales/{invoice}")).Status);
        Assert.Equal(400, (await Api.GetAsync(c, "/api/v1/pharmacy/external-sales?status=invalid")).Status);
        Assert.Equal(400, (await Api.PostAsync(c, "/api/v1/pharmacy/external-sales", new { patient_id = Id(sale.Body, "patient_id"), items = new[] { new { service_id = id, quantity = 1001 } } }, Guid.NewGuid())).Status);
        await Api.PatchAsync(c, $"/api/v1/pharmacy/drugs/{id}", new { requires_prescription = true });
        var restricted = await Api.PostAsync(c, "/api/v1/pharmacy/external-sales", req, Guid.NewGuid());
        Assert.Equal(422, restricted.Status);
        Assert.Equal("PRESCRIPTION_REQUIRED", restricted.Code);
    }
}
