using Xunit;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class PatientTests(TestApp app)
{
    private static string Phone() => "0790" + Random.Shared.Next(1000000, 9999999);

    [SkippableFact]
    public async Task Create_normalizes_phone_assigns_file_number_and_warns_on_duplicates()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (_, rec) = await app.CreateStaffAsync("receptionist");
        var c = app.Client(rec);
        var phone = Phone();

        var first = await Api.PostAsync(c, "/api/v1/patients", new
        {
            full_name = "  فاطمة   حسن ", phone_number = "+964" + phone[1..], birth_date = "1991-04-10", gender = "female",
        }, Guid.NewGuid());
        Assert.Equal(201, first.Status);
        Assert.Equal("فاطمة حسن", first.Body!["full_name"]!.GetValue<string>());
        Assert.Equal(phone, first.Body!["phone_number"]!.GetValue<string>());
        Assert.Matches(@"^B1-\d{6}$", first.Body!["file_number"]!.GetValue<string>());
        Assert.Empty(first.Body!["possible_duplicates"]!.AsArray());

        // أوفلاين: المعرف من الواجهة
        var offlineId = Guid.NewGuid();
        var second = await Api.PostAsync(c, "/api/v1/patients", new
        {
            patient_id = offlineId, full_name = "زينب علي", phone_number = phone, approx_age = 8, created_offline_at = DateTimeOffset.UtcNow,
        }, Guid.NewGuid());
        Assert.Equal(201, second.Status);
        Assert.Equal(offlineId, second.Body!["patient_id"]!.GetValue<Guid>());
        Assert.Single(second.Body!["possible_duplicates"]!.AsArray());

        var bad = await Api.PostAsync(c, "/api/v1/patients", new { full_name = "x", phone_number = "12345", gender = "other" }, Guid.NewGuid());
        Assert.Equal(400, bad.Status);
        Assert.NotNull(bad.Body!["errors"]!["phone_number"]);
        Assert.NotNull(bad.Body!["errors"]!["gender"]);

        var byPhone = await Api.GetAsync(c, $"/api/v1/patients/search?phone={phone}");
        Assert.Equal(2, byPhone.Body!["total"]!.GetValue<int>());
        var byName = await Api.GetAsync(c, $"/api/v1/patients/search?q={Uri.EscapeDataString("زينب")}");
        Assert.Contains(byName.Body!["items"]!.AsArray(), i => i!["patient_id"]!.GetValue<Guid>() == offlineId);
        var file = first.Body!["file_number"]!.GetValue<string>();
        var byFile = await Api.GetAsync(c, $"/api/v1/patients/search?file_number={file.ToLower()}");
        Assert.Equal(1, byFile.Body!["total"]!.GetValue<int>());
        Assert.Equal(400, (await Api.GetAsync(c, "/api/v1/patients/search")).Status);
    }

    [SkippableFact]
    public async Task Update_is_audited_and_merge_moves_records()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var phone = Phone();
        var a = (await Api.PostAsync(admin, "/api/v1/patients", new { full_name = "حسين محمد", phone_number = phone }, Guid.NewGuid()))
            .Body!["patient_id"]!.GetValue<Guid>();
        var b = (await Api.PostAsync(admin, "/api/v1/patients", new { full_name = "حسين محمد علي", phone_number = phone, gender = "male" }, Guid.NewGuid()))
            .Body!["patient_id"]!.GetValue<Guid>();

        var patch = await Api.PatchAsync(admin, $"/api/v1/patients/{a}", new { birth_date = "1980-01-01" });
        Assert.Equal(200, patch.Status);
        Assert.Equal(1, await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE action = 'PATIENT_UPDATED' AND entity_id = '{a}'"));

        await app.ExecAsync($"""
            INSERT INTO patient_credit_ledger (patient_id, branch_id, amount, kind) VALUES ('{b}', '{app.Branch1}', 5000, 'refund_credit');
            INSERT INTO patient_no_shows (patient_id) VALUES ('{b}');
            """);

        var (_, rec) = await app.CreateStaffAsync("receptionist");
        Assert.Equal(403, (await Api.PostAsync(app.Client(rec), "/api/v1/patients/merge",
            new { primary_patient_id = a, duplicate_patient_id = b }, Guid.NewGuid())).Status);

        var merge = await Api.PostAsync(admin, "/api/v1/patients/merge", new { primary_patient_id = a, duplicate_patient_id = b }, Guid.NewGuid());
        Assert.Equal(200, merge.Status);
        Assert.Equal(2, merge.Body!["moved_records"]!.GetValue<int>());

        var primary = await Api.GetAsync(admin, $"/api/v1/patients/{a}");
        Assert.Equal(5000, primary.Body!["credit_balance"]!.GetValue<long>());
        Assert.Equal("male", primary.Body!["gender"]!.GetValue<string>());
        var dup = await Api.GetAsync(admin, $"/api/v1/patients/{b}");
        Assert.Equal(a, dup.Body!["merged_into_patient_id"]!.GetValue<Guid>());
        // المدموج لا يظهر في البحث
        var search = await Api.GetAsync(admin, $"/api/v1/patients/search?phone={phone}");
        Assert.Equal(1, search.Body!["total"]!.GetValue<int>());

        var again = await Api.PostAsync(admin, "/api/v1/patients/merge", new { primary_patient_id = a, duplicate_patient_id = b }, Guid.NewGuid());
        Assert.Equal("PATIENT_MERGED", again.Code);
    }
}
