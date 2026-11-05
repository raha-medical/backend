using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlRahma.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace AlRahma.Infrastructure.Services;

/// <summary>
/// إرسال Push عبر Firebase Cloud Messaging (HTTP v1) بحساب خدمة. لا يعمل إلا عند Push__Mode=live مع ProjectId وملف الاعتماد،
/// والملف سر على السيرفر فقط. لم يُجرَّب على Firebase حقيقي بعد لعدم توفر بيانات اعتماد: بناء الرسالة فقط مختبر.
/// </summary>
public class FirebasePushProvider(IOptions<FirebasePushOptions> options, HttpClient http) : IPushProvider
{
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    private string? _token;
    private DateTimeOffset _tokenExpiry;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Name => "firebase";
    public bool IsSandbox => false;

    public static JsonObject BuildMessage(string token, string text, string? dataJson)
    {
        var data = new JsonObject();
        if (!string.IsNullOrEmpty(dataJson) && JsonNode.Parse(dataJson) is JsonObject parsed)
            foreach (var (k, v) in parsed) if (v is not null) data[k] = v.ToString();
        var parts = text.Split(": ", 2);
        return new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["token"] = token,
                ["notification"] = new JsonObject { ["title"] = parts[0], ["body"] = parts.Length > 1 ? parts[1] : "" },
                ["data"] = data,
                ["android"] = new JsonObject { ["priority"] = data["priority"]?.ToString() == "high" ? "HIGH" : "NORMAL" },
            },
        };
    }

    public async Task<PushSendResult> SendAsync(string token, string text, string? dataJson, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(options.Value.ProjectId)}/messages:send")
            {
                Content = JsonContent.Create(BuildMessage(token, text, dataJson)),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct));
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? new PushSendResult(true) : new PushSendResult(false, $"FCM {(int)response.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or CryptographicException or IOException)
        {
            return new PushSendResult(false, e.GetType().Name); // النوع فقط: لا نص قد يحمل بيانات
        }
    }

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_token is not null && _tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(2)) return _token;
            using var creds = JsonDocument.Parse(await File.ReadAllTextAsync(options.Value.CredentialsFile, ct));
            var email = creds.RootElement.GetProperty("client_email").GetString()!;
            var key = creds.RootElement.GetProperty("private_key").GetString()!;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
            var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new { iss = email, scope = Scope, aud = "https://oauth2.googleapis.com/token", iat = now, exp = now + 3600 }));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(key);
            var signature = B64(rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = $"{header}.{claims}.{signature}",
            }), ct);
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _token = body.RootElement.GetProperty("access_token").GetString();
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(body.RootElement.GetProperty("expires_in").GetInt32());
            return _token!;
        }
        finally { _lock.Release(); }
    }
}
