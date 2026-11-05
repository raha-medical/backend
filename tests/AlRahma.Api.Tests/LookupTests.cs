using Xunit;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class LookupTests(TestApp app)
{
    [SkippableFact]
    public async Task Admin_manages_select_lists()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var (_, recToken) = await app.CreateStaffAsync("receptionist");
        var rec = app.Client(recToken);
        var suffix = Guid.NewGuid().ToString()[..6];

        var categories = await Api.GetAsync(rec, "/api/v1/lookups");
        Assert.Equal(4, categories.Body!["items"]!.AsArray().Count);
        Assert.Contains(categories.Body!["items"]!.AsArray(), c => c!["category"]!.GetValue<string>() == "expense_category");
        var reasons = await Api.GetAsync(rec, "/api/v1/lookups/closure_reason");
        Assert.Contains(reasons.Body!["items"]!.AsArray(), r => r!["code"]!.GetValue<string>() == "breakdown");
        Assert.Equal(400, (await Api.GetAsync(rec, "/api/v1/lookups/colors")).Status);

        // الاختصاصات متاحة لتطبيق المريض بلا تسجيل دخول، وباقي القوائم لا
        Assert.Equal(200, (await Api.GetAsync(app.Client(), "/api/v1/lookups/doctor_specialty")).Status);
        Assert.Equal(401, (await Api.GetAsync(app.Client(), "/api/v1/lookups/closure_reason")).Status);

        Assert.Equal(403, (await Api.PostAsync(rec, "/api/v1/lookups/doctor_job_title", new { name_ar = "x" }, Guid.NewGuid())).Status);
        var created = await Api.PostAsync(admin, "/api/v1/lookups/doctor_specialty", new { code = $"sports_{suffix}", name_ar = $"طب رياضي {suffix}" }, Guid.NewGuid());
        Assert.Equal(201, created.Status);
        Assert.Equal("DUPLICATE_LOOKUP", (await Api.PostAsync(admin, "/api/v1/lookups/doctor_specialty", new { name_ar = $"طب رياضي {suffix}" }, Guid.NewGuid())).Code);

        var other = reasons.Body!["items"]!.AsArray().First(r => r!["code"]!.GetValue<string>() == "other")!;
        Assert.Equal("LOOKUP_IS_SYSTEM", (await Api.PatchAsync(admin, $"/api/v1/lookups/closure_reason/{other["lookup_id"]}", new { is_active = false })).Code);

        // الطبيب: الاختصاص والعنوان الوظيفي من القوائم (بالرمز أو بالاسم)، ويُخزنان بالاسم
        Assert.Equal(400, (await Api.PostAsync(admin, "/api/v1/users", new
        {
            username = $"dr_bad_{suffix}", full_name = "د. اختبار", role = "doctor", branch_ids = new[] { app.Branch1 },
            temporary_password = "Temp#Pass1234", doctor_profile = new { specialty = "تخصص غير موجود", clinic_id = app.ClinicInternal },
        }, Guid.NewGuid())).Status);
        var (doctorUser, _) = await app.CreateStaffAsync("doctor", doctorProfile: new { specialty = $"sports_{suffix}", job_title = "consultant", clinic_id = app.ClinicInternal });
        var user = await Api.GetAsync(admin, $"/api/v1/users/{doctorUser}");
        Assert.Equal($"طب رياضي {suffix}", user.Body!["doctor_profile"]!["specialty"]!.GetValue<string>());
        Assert.Equal("استشاري", user.Body!["doctor_profile"]!["job_title"]!.GetValue<string>());

        // تغيير اسم الاختصاص يُحدّث الأطباء المرتبطين به، والبحث يقبل الرمز
        var renamed = await Api.PatchAsync(admin, $"/api/v1/lookups/doctor_specialty/{created.Body!["lookup_id"]}", new { name_ar = $"الطب الرياضي {suffix}" });
        Assert.Equal(200, renamed.Status);
        Assert.Equal($"الطب الرياضي {suffix}", await app.ScalarAsync<string>($"SELECT specialty FROM doctors WHERE user_id = '{doctorUser}'"));
        var doctors = await Api.GetAsync(admin, $"/api/v1/doctors?specialty=sports_{suffix}");
        Assert.Single(doctors.Body!["items"]!.AsArray());
        Assert.Equal("استشاري", doctors.Body!["items"]![0]!["job_title"]!.GetValue<string>());
    }
}
