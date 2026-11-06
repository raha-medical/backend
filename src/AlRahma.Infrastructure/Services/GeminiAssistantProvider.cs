using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlRahma.Application.Assistant;
using Microsoft.Extensions.Options;

namespace AlRahma.Infrastructure.Services;

/// <summary>
/// مزود Gemini عبر REST generateContent مع استدعاء الدوال. المفتاح في الترويسة x-goog-api-key لا في الرابط، فلا يظهر في السجلات،
/// ولا يعود أي نص من رد الخطأ إلى العميل. الأخطاء مصنفة (AssistantProviderException) للإحصاءات.
/// أجزاء استدعاء الدوال تُعاد حرفياً (RawPart) فتبقى توقيعات التفكير (thoughtSignature) التي تشترطها نماذج Gemini 3.
/// </summary>
public class GeminiAssistantProvider(IOptions<AssistantOptions> options, HttpClient http) : IAssistantProvider
{
    private AssistantOptions Opt => options.Value;
    public string Name => "gemini";
    public bool IsSandbox => false;
    public bool SupportsAudio => true;

    /// <summary>429 (حد الطلبات في الدقيقة): محاولة ثانية واحدة بعد انتظار قصير، ثم الخطأ المصنف.</summary>
    public async Task<AssistantProviderReply> GenerateAsync(AssistantProviderRequest request, CancellationToken ct)
    {
        try { return await SendOnceAsync(request, ct); }
        catch (AssistantProviderException e) when (e.ErrorType == "rate_limited")
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(Opt.RateLimitRetrySeconds, 0, 10)), ct);
            return await SendOnceAsync(request, ct);
        }
    }

    private async Task<AssistantProviderReply> SendOnceAsync(AssistantProviderRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Opt.BaseUrl.TrimEnd('/')}/models/{Uri.EscapeDataString(Opt.Model)}:generateContent")
        {
            Content = JsonContent.Create(BuildBody(request)),
        };
        message.Headers.Add("x-goog-api-key", Opt.ApiKey);
        HttpResponseMessage response;
        try { response = await http.SendAsync(message, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AssistantProviderException("timeout", null); }
        catch (HttpRequestException) { throw new AssistantProviderException("network", null); }
        using (response)
        {
            // النوع ورمز HTTP فقط: نص رد الخطأ لا يُقرأ ولا يُسجل ولا يعود للعميل
            if (!response.IsSuccessStatusCode)
                throw new AssistantProviderException(AssistantProviderException.Classify((int)response.StatusCode), (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(ct);
            try { return ParseReply(body); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw new AssistantProviderException("other", (int)response.StatusCode);
            }
        }
    }

    public JsonObject BuildBody(AssistantProviderRequest request) => BuildBody(request, Opt.ThinkingLevel);

    /// <summary>
    /// toolConfig بوضع AUTO (النموذج يختار بين الرد والأداة)، وthinkingLevel (minimal افتراضياً للسرعة).
    /// temperature تبقى افتراضية: توصية Google لنماذج Gemini 3.
    /// </summary>
    public static JsonObject BuildBody(AssistantProviderRequest request, string? thinkingLevel)
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
        var generation = new JsonObject { ["maxOutputTokens"] = 2048 };
        if (!string.IsNullOrWhiteSpace(thinkingLevel)) generation["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = thinkingLevel.Trim() };
        return new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.SystemPrompt }) },
            ["contents"] = contents,
            ["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations }),
            ["toolConfig"] = new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = "AUTO" } },
            ["generationConfig"] = generation,
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
            else if (part.TryGetProperty("text", out var t) && !(part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True))
                text += t.GetString();
        }
        return new AssistantProviderReply(text, calls);
    }
}
