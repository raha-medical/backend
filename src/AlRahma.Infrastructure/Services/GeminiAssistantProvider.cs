using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlRahma.Application.Assistant;
using AlRahma.Application.Common;
using Microsoft.Extensions.Options;

namespace AlRahma.Infrastructure.Services;

/// <summary>
/// مزود Gemini عبر REST generateContent مع استدعاء الدوال. المفتاح في الترويسة x-goog-api-key لا في الرابط، فلا يظهر في السجلات،
/// ولا يعود أي نص من رد الخطأ إلى العميل.
/// </summary>
public class GeminiAssistantProvider(IOptions<AssistantOptions> options, HttpClient http) : IAssistantProvider
{
    private AssistantOptions Opt => options.Value;
    public string Name => "gemini";
    public bool IsSandbox => false;
    public bool SupportsAudio => true;

    public async Task<AssistantProviderReply> GenerateAsync(AssistantProviderRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Opt.BaseUrl.TrimEnd('/')}/models/{Uri.EscapeDataString(Opt.Model)}:generateContent")
        {
            Content = JsonContent.Create(BuildBody(request)),
        };
        message.Headers.Add("x-goog-api-key", Opt.ApiKey);
        using var response = await http.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
            throw new AppException(503, "ASSISTANT_UNAVAILABLE", "المساعد غير متاح حالياً", "حاول بعد قليل");
        var body = await response.Content.ReadAsStringAsync(ct);
        return ParseReply(body);
    }

    public static JsonObject BuildBody(AssistantProviderRequest request)
    {
        var contents = new JsonArray();
        foreach (var c in request.Contents)
        {
            var parts = new JsonArray();
            if (!string.IsNullOrEmpty(c.Text)) parts.Add(new JsonObject { ["text"] = c.Text });
            if (c.Audio is not null)
                parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = c.AudioMimeType, ["data"] = Convert.ToBase64String(c.Audio) } });
            foreach (var call in c.Calls ?? [])
                parts.Add(call.RawPart is not null ? JsonNode.Parse(call.RawPart)! : new JsonObject
                {
                    ["functionCall"] = new JsonObject { ["name"] = call.Name, ["args"] = call.Args is { } a ? JsonNode.Parse(a.GetRawText()) : new JsonObject() },
                });
            foreach (var r in c.Results ?? [])
                parts.Add(new JsonObject { ["functionResponse"] = new JsonObject { ["name"] = r.Name, ["response"] = JsonNode.Parse(r.Json) } });
            if (parts.Count > 0) contents.Add(new JsonObject { ["role"] = c.Role == "model" ? "model" : "user", ["parts"] = parts });
        }
        var declarations = new JsonArray();
        foreach (var t in request.Tools)
            declarations.Add(new JsonObject
            {
                ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonSerializer.SerializeToNode(t.Parameters),
            });
        return new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.SystemPrompt }) },
            ["contents"] = contents,
            ["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations }),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0.2, ["maxOutputTokens"] = 1024 },
        };
    }

    public static AssistantProviderReply ParseReply(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0
            || !candidates[0].TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
            return new AssistantProviderReply("ما أگدر أجاوب على هذا السؤال. لأي استفسار طبي راجع طبيبك.", [], AssistantKinds.OutOfScope);
        var calls = new List<AssistantToolCall>();
        var text = "";
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("functionCall", out var fn))
                calls.Add(new AssistantToolCall(fn.GetProperty("name").GetString() ?? "", fn.TryGetProperty("args", out var a) ? a.Clone() : null, part.GetRawText()));
            else if (part.TryGetProperty("text", out var t)) text += t.GetString();
        }
        return new AssistantProviderReply(text, calls);
    }
}
