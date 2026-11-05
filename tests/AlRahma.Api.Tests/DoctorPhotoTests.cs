using Xunit;

namespace AlRahma.Api.Tests;

[Collection(ApiCollection.Name)]
public class DoctorPhotoTests(TestApp app)
{
    private async Task<(Guid DoctorId, string DoctorToken)> CreateDoctorAsync()
    {
        var (userId, token) = await app.CreateStaffAsync("doctor",
            doctorProfile: new { specialty = "باطنية", clinic_id = app.ClinicInternal });
        var doctorId = await app.ScalarAsync<Guid>($"SELECT doctor_id FROM doctors WHERE user_id = '{userId}'");
        return (doctorId, token);
    }

    private async Task<Guid> UploadPhotoAsync(HttpClient c, Guid doctorId, long size = 150_000)
    {
        var up = await Api.PostAsync(c, "/api/v1/files/upload-url", new
        {
            file_name = "dr.jpg", content_type = "image/jpeg", size_bytes = size, purpose = "doctor_photo", doctor_id = doctorId,
        });
        Assert.Equal(201, up.Status);
        Assert.Equal("PUT", up.Body!["method"]!.GetValue<string>());
        app.Storage.Upload(up.Body!["upload_url"]!.GetValue<string>(), size);
        var fileId = up.Body!["file_id"]!.GetValue<Guid>();
        var done = await Api.PostAsync(c, $"/api/v1/files/{fileId}/complete", new { });
        Assert.Equal(200, done.Status);
        Assert.Equal("stored", done.Body!["status"]!.GetValue<string>());
        return fileId;
    }

    [SkippableFact]
    public async Task Admin_uploads_sets_replaces_and_deletes_photo()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var (doctorId, _) = await CreateDoctorAsync();

        var before = await Api.GetAsync(admin, $"/api/v1/doctors/{doctorId}");
        Assert.Null(before.Body!["photo_url"]);

        var first = await UploadPhotoAsync(admin, doctorId);
        var set = await Api.SendAsync(admin, HttpMethod.Put, $"/api/v1/doctors/{doctorId}/photo", new { file_id = first }, Guid.NewGuid());
        Assert.Equal(200, set.Status);
        var url = set.Body!["photo_url"]!.GetValue<string>();
        Assert.Contains($"doctors/{doctorId:N}/", url);

        var list = await Api.GetAsync(admin, "/api/v1/doctors");
        var row = list.Body!["items"]!.AsArray().First(d => d!["doctor_id"]!.GetValue<Guid>() == doctorId);
        Assert.NotNull(row!["photo_url"]);

        // استبدال الصورة يحذف القديمة من التخزين
        var oldKey = await app.ScalarAsync<string>($"SELECT photo_object_key FROM doctors WHERE doctor_id = '{doctorId}'");
        var second = await UploadPhotoAsync(admin, doctorId);
        Assert.Equal(200, (await Api.SendAsync(admin, HttpMethod.Put, $"/api/v1/doctors/{doctorId}/photo", new { file_id = second }, Guid.NewGuid())).Status);
        Assert.False(app.Storage.Objects.ContainsKey(oldKey));
        Assert.Equal(2, await app.ScalarAsync<long>($"SELECT count(*) FROM audit_logs WHERE action = 'DOCTOR_PHOTO_CHANGED' AND entity_id = '{doctorId}'"));

        var del = await Api.SendAsync(admin, HttpMethod.Delete, $"/api/v1/doctors/{doctorId}/photo");
        Assert.Equal(204, del.Status);
        Assert.Null((await Api.GetAsync(admin, $"/api/v1/doctors/{doctorId}")).Body!["photo_url"]);
    }

    [SkippableFact]
    public async Task Doctor_changes_own_photo_only()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var (doctorId, token) = await CreateDoctorAsync();
        var (otherDoctor, _) = await CreateDoctorAsync();
        var me = app.Client(token);

        var file = await UploadPhotoAsync(me, doctorId);
        Assert.Equal(200, (await Api.SendAsync(me, HttpMethod.Put, $"/api/v1/doctors/{doctorId}/photo", new { file_id = file }, Guid.NewGuid())).Status);

        var other = await Api.PostAsync(me, "/api/v1/files/upload-url", new
        {
            file_name = "x.jpg", content_type = "image/jpeg", size_bytes = 1000, purpose = "doctor_photo", doctor_id = otherDoctor,
        });
        Assert.Equal(403, other.Status);

        // ملف طبيب لا يُربط بطبيب آخر
        var admin = app.Client(await app.AdminTokenAsync());
        var wrong = await Api.SendAsync(admin, HttpMethod.Put, $"/api/v1/doctors/{otherDoctor}/photo", new { file_id = file }, Guid.NewGuid());
        Assert.Equal(422, wrong.Status);
        Assert.Equal("INVALID_FILE_PURPOSE", wrong.Code);

        var (_, receptionist) = await app.CreateStaffAsync("receptionist");
        Assert.Equal(403, (await Api.SendAsync(app.Client(receptionist), HttpMethod.Delete, $"/api/v1/doctors/{doctorId}/photo")).Status);
    }

    [SkippableFact]
    public async Task Upload_rules_type_size_and_unfinished_upload()
    {
        Skip.If(TestApp.ServerUrl is null, "TEST_DATABASE_URL not set");
        var admin = app.Client(await app.AdminTokenAsync());
        var (doctorId, _) = await CreateDoctorAsync();

        var pdf = await Api.PostAsync(admin, "/api/v1/files/upload-url", new
        {
            file_name = "cv.pdf", content_type = "application/pdf", size_bytes = 1000, purpose = "doctor_photo", doctor_id = doctorId,
        });
        Assert.Equal(400, pdf.Status);

        var big = await Api.PostAsync(admin, "/api/v1/files/upload-url", new
        {
            file_name = "big.jpg", content_type = "image/jpeg", size_bytes = 3_000_000, purpose = "doctor_photo", doctor_id = doctorId,
        });
        Assert.Equal(400, big.Status);

        var up = await Api.PostAsync(admin, "/api/v1/files/upload-url", new
        {
            file_name = "dr.png", content_type = "image/png", size_bytes = 1000, purpose = "doctor_photo", doctor_id = doctorId,
        });
        var fileId = up.Body!["file_id"]!.GetValue<Guid>();

        // لم يُرفع بعد
        var notUploaded = await Api.PostAsync(admin, $"/api/v1/files/{fileId}/complete", new { });
        Assert.Equal(422, notUploaded.Status);
        Assert.Equal("FILE_NOT_UPLOADED", notUploaded.Code);
        var notStored = await Api.SendAsync(admin, HttpMethod.Put, $"/api/v1/doctors/{doctorId}/photo", new { file_id = fileId }, Guid.NewGuid());
        Assert.Equal("FILE_NOT_STORED", notStored.Code);

        // رُفع ملف أكبر من المعلن والمسموح
        app.Storage.Upload(up.Body!["upload_url"]!.GetValue<string>(), 5_000_000);
        var tooBig = await Api.PostAsync(admin, $"/api/v1/files/{fileId}/complete", new { });
        Assert.Equal("FILE_TOO_LARGE", tooBig.Code);

        Assert.Equal(404, (await Api.PostAsync(admin, $"/api/v1/files/{Guid.NewGuid()}/complete", new { })).Status);
    }
}
