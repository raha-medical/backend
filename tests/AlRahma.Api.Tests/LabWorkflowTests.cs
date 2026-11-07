using System.Text.Json.Nodes;
using Xunit;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class LabWorkflowTests(TestApp app)
{
    private static Guid Id(JsonNode? body, string field) => body![field]!.GetValue<Guid>();

    [SkippableFact]
    public async Task Lab_catalog_is_limited_to_lab_branch_and_write_permission()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (_, token) = await app.CreateStaffAsync("lab_tech");
        var lab = app.Client(token);
        var admin = app.Client(await app.AdminTokenAsync());
        var created = await Api.PostAsync(lab, "/api/v1/lab/services", new { name = "تحليل اختبار المختبر", price = 12000 }, Guid.NewGuid());
        Assert.True(created.Status == 201, created.Raw);
        var id = Id(created.Body, "service_id");
        Assert.Equal("lab", created.Body!["type"]!.GetValue<string>());
        var updated = await Api.PatchAsync(lab, $"/api/v1/lab/services/{id}", new { name = "تحليل محدّث", price = 15000, reason = "اختبار السعر" });
        Assert.True(updated.Status == 200, updated.Raw);
        Assert.Equal(15000, updated.Body!["price"]!.GetValue<long>());
        Assert.Equal(1, await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE entity_id = '{id}' AND action = 'PRICE_CHANGED'"));
        var list = await Api.GetAsync(lab, "/api/v1/lab/services");
        Assert.All(list.Body!["items"]!.AsArray(), row => Assert.Equal("lab", row!["type"]!.GetValue<string>()));
        Assert.Equal(400, (await Api.PostAsync(lab, "/api/v1/lab/services", new { name = "سعر خاطئ", price = -1 }, Guid.NewGuid())).Status);
        var rad = await Api.PostAsync(admin, "/api/v1/services", new { name = "أشعة اختبار", type = "radiology", modality = "xray", price = 20000 }, Guid.NewGuid());
        Assert.Equal(201, rad.Status);
        Assert.Equal(403, (await Api.PatchAsync(lab, $"/api/v1/lab/services/{Id(rad.Body, "service_id")}", new { price = 1 })).Status);
        var foreign = Guid.NewGuid();
        await app.ExecAsync($"INSERT INTO services(service_id, branch_id, name, type, price) VALUES ('{foreign}', '{app.Branch2}', 'تحليل فرع آخر', 'lab', 1000)");
        Assert.Equal(404, (await Api.PatchAsync(lab, $"/api/v1/lab/services/{foreign}", new { price = 1 })).Status);
        Assert.Equal(403, (await Api.PatchAsync(lab, $"/api/v1/services/{id}", new { price = 1 })).Status);
        var (_, viewerToken) = await app.CreateStaffAsync("branch_manager");
        var viewer = app.Client(viewerToken);
        Assert.Equal(200, (await Api.GetAsync(viewer, "/api/v1/lab/services")).Status);
        Assert.Equal(403, (await Api.PostAsync(viewer, "/api/v1/lab/services", new { name = "ممنوع", price = 1 }, Guid.NewGuid())).Status);
    }

    [SkippableFact]
    public async Task Direct_patient_orders_are_idempotent_and_source_filters_work_before_paging()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (_, token) = await app.CreateStaffAsync("lab_tech");
        var lab = app.Client(token);
        var created = await Api.PostAsync(lab, "/api/v1/lab/services", new { name = "تحليل مسار مباشر", price = 9000 }, Guid.NewGuid());
        var service = Id(created.Body, "service_id");
        var patientName = "مراجع مختبر " + Guid.NewGuid().ToString("N")[..8];
        var request = new { new_patient = new { full_name = patientName, phone_number = "07790001234" }, items = new[] { new { service_id = service } } };
        var key = Guid.NewGuid();
        var first = await Api.PostAsync(lab, "/api/v1/lab/orders/direct", request, key);
        Assert.True(first.Status == 201, first.Raw);
        var repeated = await Api.PostAsync(lab, "/api/v1/lab/orders/direct", request, key);
        Assert.Equal(Id(first.Body, "patient_id"), Id(repeated.Body, "patient_id"));
        var order = Id(first.Body!["items"]![0], "order_item_id");
        Assert.Equal(order, Id(repeated.Body!["items"]![0], "order_item_id"));
        var second = await Api.PostAsync(lab, "/api/v1/lab/orders/direct", request, Guid.NewGuid());
        Assert.Equal(201, second.Status);
        var secondOrder = Id(second.Body!["items"]![0], "order_item_id");
        var (doctorUser, _) = await app.CreateStaffAsync("doctor", doctorProfile: new { specialty = "باطنية", clinic_id = app.ClinicInternal });
        var doctor = await app.ScalarAsync<Guid>($"SELECT doctor_id FROM doctors WHERE user_id = '{doctorUser}'");
        await app.ExecAsync($"UPDATE medical_orders SET ordered_by_doctor_id = '{doctor}' WHERE order_item_id = '{secondOrder}'");
        foreach (var source in new[] { "doctor", "direct" })
        {
            var incoming = await Api.GetAsync(lab, $"/api/v1/referrals?service=lab&status=referred&source={source}&q={Uri.EscapeDataString(patientName)}&page_size=1");
            Assert.True(incoming.Status == 200, incoming.Raw);
            Assert.Equal(1, incoming.Body!["total"]!.GetValue<int>());
            var row = incoming.Body["items"]![0]!;
            Assert.Equal(source, row["source"]!.GetValue<string>());
            Assert.NotNull(row["phone_number"]);
            Assert.Equal(200, (await Api.PostAsync(lab, $"/api/v1/referrals/{Id(row, "referral_id")}/activate")).Status);
            var work = await Api.GetAsync(lab, $"/api/v1/lab/worklist?source={source}&q={Uri.EscapeDataString(patientName)}&page_size=1");
            Assert.Equal(1, work.Body!["total"]!.GetValue<int>());
            Assert.Equal(source, work.Body["items"]![0]!["source"]!.GetValue<string>());
        }
        Assert.Equal(400, (await Api.GetAsync(lab, "/api/v1/lab/worklist?source=invalid")).Status);
        Assert.Equal(400, (await Api.GetAsync(lab, "/api/v1/referrals?service=lab&source=invalid")).Status);
        var badName = "مراجع مرفوض " + Guid.NewGuid().ToString("N")[..8];
        var rejected = await Api.PostAsync(lab, "/api/v1/lab/orders/direct", new { new_patient = new { full_name = badName, phone_number = "07790001234" }, items = new[] { new { service_id = Guid.NewGuid() } } }, Guid.NewGuid());
        Assert.Equal(400, rejected.Status);
        Assert.Equal(0, await app.ScalarAsync<long>($"SELECT count(*) FROM patients WHERE full_name = '{badName}'"));
        Assert.Equal(400, (await Api.PostAsync(lab, "/api/v1/lab/orders/direct", new { new_patient = new { full_name = patientName, phone_number = "bad" }, items = new[] { new { service_id = service } } }, Guid.NewGuid())).Status);
    }
}
