using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using AlRahma.Application.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace AlRahma.Api.Tests;

/// <summary>
/// ينشئ قاعدة بيانات اختبار جديدة من database/schema_v5.sql على خادم PostgreSQL محدد في
/// المتغير TEST_DATABASE_URL (مثال: Host=localhost;Port=5432;Username=postgres;Password=postgres)،
/// ويشغل الـ API عليها. إذا لم يُحدد المتغير تُتخطى الاختبارات.
/// </summary>
public sealed class TestApp : IAsyncLifetime
{
    public const string AdminUsername = "admin";
    public const string AdminTempPassword = "Bootstrap-Pass-2026";
    public const string AdminPassword = "Admin-Strong-Pass-2026";

    public static string? ServerUrl => Environment.GetEnvironmentVariable("TEST_DATABASE_URL");

    private readonly string _dbName = $"alrahma_test_{Guid.NewGuid():N}";
    private WebApplicationFactory<Program>? _factory;

    public string ConnectionString { get; private set; } = "";
    public Guid Branch1 { get; private set; }
    public Guid Branch2 { get; private set; }
    public Guid ClinicInternal { get; private set; }
    public Guid ClinicPediatrics { get; private set; }
    public Guid ClinicBranch2 { get; private set; }
    public Guid LabBranch1 { get; private set; }
    public FakeObjectStorage Storage { get; } = new();

    public async Task InitializeAsync()
    {
        if (ServerUrl is null) return;

        await using (var admin = new NpgsqlConnection(ServerUrl))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {_dbName}", admin);
            await create.ExecuteNonQueryAsync();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(ServerUrl) { Database = _dbName, Pooling = false }.ConnectionString;
        var schema = await File.ReadAllTextAsync(FindRepoFile("database/schema_v5.sql"));
        await ExecAsync(schema);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Default", ConnectionString);
            b.UseSetting("Jobs:Enabled", "false");
            b.UseSetting("Otp:ExposeCodeInResponse", "true");
            b.UseSetting("Drawer:KeyFile", ""); // مفتاح مؤقت في الاختبارات
            b.UseSetting("Jwt:SigningKey", "test-signing-key-0123456789-abcdefghijklmnop");
            b.UseSetting("Bootstrap:BranchCode", "B1");
            b.UseSetting("Bootstrap:BranchName", "الفرع الرئيسي");
            b.UseSetting("Bootstrap:AdminUsername", AdminUsername);
            b.UseSetting("Bootstrap:AdminPassword", AdminTempPassword);
            b.ConfigureTestServices(s => s.AddSingleton<IObjectStorage>(Storage));
        });
        _factory.CreateClient().Dispose(); // تشغيل التطبيق (والـ bootstrap)

        Branch1 = await ScalarAsync<Guid>("SELECT branch_id FROM branches WHERE code = 'B1'");
        Branch2 = Guid.NewGuid();
        ClinicInternal = Guid.NewGuid();
        ClinicPediatrics = Guid.NewGuid();
        ClinicBranch2 = Guid.NewGuid();
        LabBranch1 = Guid.NewGuid();
        await ExecAsync($"""
            INSERT INTO branches (branch_id, code, name, city) VALUES ('{Branch2}', 'B2', 'فرع الكرادة', 'بغداد');
            INSERT INTO clinics (clinic_id, branch_id, name, department) VALUES
              ('{ClinicInternal}', '{Branch1}', 'عيادة الباطنية', 'clinic'),
              ('{ClinicPediatrics}', '{Branch1}', 'عيادة الأطفال', 'clinic'),
              ('{LabBranch1}', '{Branch1}', 'المختبر', 'lab'),
              ('{ClinicBranch2}', '{Branch2}', 'عيادة الباطنية', 'clinic');
            """);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        if (ServerUrl is null) return;
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(ServerUrl);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    public TestServer Server => _factory!.Server;
    /// <summary>لاختبارات تحتاج إعدادات أو خدمات مختلفة على نفس القاعدة (WithWebHostBuilder).</summary>
    public WebApplicationFactory<Program> Factory => _factory!;

    public HttpClient Client(string? token = null)
    {
        var c = _factory!.CreateClient();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>دخول المدير (وتغيير كلمة المرور المؤقتة أول مرة) وإرجاع توكن صالح.</summary>
    private string? _adminToken;
    private DateTimeOffset _adminTokenAt;
    private bool _adminPasswordChanged;
    private readonly SemaphoreSlim _adminLock = new(1, 1);

    public async Task<string> AdminTokenAsync()
    {
        await _adminLock.WaitAsync();
        try
        {
            // توكن الوصول 15 دقيقة، والاختبارات كلها أطول من ذلك: دخول جديد قبل انتهائه
            if (_adminToken is not null && DateTimeOffset.UtcNow - _adminTokenAt < TimeSpan.FromMinutes(10)) return _adminToken;
            var c = Client();
            _adminTokenAt = DateTimeOffset.UtcNow;
            var first = _adminPasswordChanged ? null : await Api.PostAsync(c, "/api/v1/auth/login",
                new { username = AdminUsername, password = AdminTempPassword, branch_id = Branch1 });
            _adminPasswordChanged = true;
            if (first?.Status == 200)
            {
                var t = first.Body!["access_token"]!.GetValue<string>();
                var rt = first.Body!["refresh_token"]!.GetValue<string>();
                await Api.PostAsync(Client(t), "/api/v1/auth/change-password",
                    new { current_password = AdminTempPassword, new_password = AdminPassword });
                var refreshed = await Api.PostAsync(c, "/api/v1/auth/refresh", new { refresh_token = rt });
                return _adminToken = refreshed.Body!["access_token"]!.GetValue<string>();
            }
            var login = await Api.PostAsync(c, "/api/v1/auth/login",
                new { username = AdminUsername, password = AdminPassword, branch_id = Branch1 });
            return _adminToken = login.Body!["access_token"]!.GetValue<string>();
        }
        finally
        {
            _adminLock.Release();
        }
    }

    /// <summary>ينشئ موظفاً عبر الـ API ويغير كلمة مروره ويعيد توكنه.</summary>
    public async Task<(Guid UserId, string Token)> CreateStaffAsync(string role, Guid? branch = null, Guid[]? clinics = null, object? doctorProfile = null, string[]? extraPermissions = null)
    {
        var adminClient = Client(await AdminTokenAsync());
        var username = $"{role}_{Guid.NewGuid():N}"[..20];
        const string temp = "Temp-Password-123";
        const string final = "Final-Password-456";
        var res = await Api.PostAsync(adminClient, "/api/v1/users", new
        {
            username,
            full_name = $"موظف {role}",
            role,
            branch_ids = new[] { branch ?? Branch1 },
            clinic_ids = clinics ?? (role == "receptionist" ? [ClinicInternal] : []),
            doctor_profile = doctorProfile,
            extra_permissions = extraPermissions,
            temporary_password = temp,
        }, idempotencyKey: Guid.NewGuid());
        if (res.Status != 201) throw new InvalidOperationException($"create {role} failed: {res.Status} {res.Raw}");
        var userId = res.Body!["user_id"]!.GetValue<Guid>();

        var c = Client();
        var login = await Api.PostAsync(c, "/api/v1/auth/login", new { username, password = temp, branch_id = branch ?? Branch1 });
        await Api.PostAsync(Client(login.Body!["access_token"]!.GetValue<string>()), "/api/v1/auth/change-password",
            new { current_password = temp, new_password = final });
        var again = await Api.PostAsync(c, "/api/v1/auth/login", new { username, password = final, branch_id = branch ?? Branch1 });
        return (userId, again.Body!["access_token"]!.GetValue<string>());
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative))) dir = dir.Parent;
        return dir is null ? throw new FileNotFoundException(relative) : Path.Combine(dir.FullName, relative);
    }
}

/// <summary>تخزين وهمي بدل MinIO: الاختبار يحاكي رفع المتصفح بـ Upload.</summary>
public sealed class FakeObjectStorage : IObjectStorage
{
    public ConcurrentDictionary<string, long> Objects { get; } = new();
    public string Bucket => "medical";
    public string PresignUpload(string objectKey, string contentType, TimeSpan expiresIn) => $"http://minio.test/{Bucket}/{objectKey}?X-Amz-Signature=up";
    public string PresignDownload(string objectKey, TimeSpan expiresIn) => $"http://minio.test/{Bucket}/{objectKey}?X-Amz-Signature=down";
    public Task<long?> GetSizeAsync(string objectKey, CancellationToken ct = default) =>
        Task.FromResult(Objects.TryGetValue(objectKey, out var size) ? size : (long?)null);
    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        Objects.TryRemove(objectKey, out _);
        return Task.CompletedTask;
    }

    /// <summary>يحاكي PUT من المتصفح على رابط الرفع.</summary>
    public void Upload(string uploadUrl, long size)
    {
        var path = new Uri(uploadUrl).AbsolutePath.TrimStart('/');
        Objects[path[(Bucket.Length + 1)..]] = size;
    }
}

[CollectionDefinition(Name)]
public class ApiCollection : ICollectionFixture<TestApp>
{
    public const string Name = "api";
}

public record ApiResult(int Status, JsonNode? Body, string Raw, HttpResponseMessage Response)
{
    public string? Code => Body?["code"]?.GetValue<string>();
}

public static class Api
{
    public static async Task<ApiResult> SendAsync(HttpClient c, HttpMethod method, string url, object? body = null, Guid? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        if (idempotencyKey is { } k) req.Headers.Add("Idempotency-Key", k.ToString());
        var res = await c.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();
        JsonNode? node = null;
        if (!string.IsNullOrWhiteSpace(raw)) node = JsonNode.Parse(raw);
        return new ApiResult((int)res.StatusCode, node, raw, res);
    }

    public static Task<ApiResult> GetAsync(HttpClient c, string url) => SendAsync(c, HttpMethod.Get, url);

    public static Task<ApiResult> PostAsync(HttpClient c, string url, object? body = null, Guid? idempotencyKey = null) =>
        SendAsync(c, HttpMethod.Post, url, body ?? new { }, idempotencyKey);

    public static Task<ApiResult> PatchAsync(HttpClient c, string url, object body) => SendAsync(c, HttpMethod.Patch, url, body);
}
