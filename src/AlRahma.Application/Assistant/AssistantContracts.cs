using System.Text.Json;

namespace AlRahma.Application.Assistant;

/// <summary>
/// إعدادات المساعد (Assistant__*). المفتاح سري على السيرفر فقط ولا يصل المتصفح.
/// gemini بلا مفتاح: الإنتاج يرفض التشغيل (Program.cs)، وغيره يعمل بالمزود التجريبي.
/// </summary>
public class AssistantOptions
{
    public const string Section = "Assistant";
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "sandbox";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-2.5-flash";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public int MaxMessagesPerHour { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 30;
    public bool WantsGemini => string.Equals(Provider, "gemini", StringComparison.OrdinalIgnoreCase);
    public bool UseGemini => WantsGemini && !string.IsNullOrWhiteSpace(ApiKey);
}

public record AssistantHistoryItem(string? Role, string? Text);
public record AssistantAudioInput(string? MimeType, string? Base64);
public record AssistantChatRequest(string? Message, List<AssistantHistoryItem>? History = null, Guid? PatientId = null, AssistantAudioInput? Audio = null);
public record AssistantChatResponse(string Reply, string Kind, string Provider, bool IsSandbox, IReadOnlyList<string> ToolsUsed, int RemainingThisHour, string DisclaimerAr);
public record AssistantStatusDto(bool Enabled, string Provider, bool IsSandbox, bool AudioInput, int MaxMessagesPerHour, int RemainingThisHour);

public static class AssistantKinds
{
    public const string Answer = "answer";
    public const string Emergency = "emergency";
    public const string MedicalAdviceRefused = "medical_advice_refused";
    public const string OutOfScope = "out_of_scope";
    public const string AudioNotSupported = "audio_not_supported";
}

/// <summary>Parameters = مخطط JSON لوسائط الأداة.</summary>
public record AssistantToolDefinition(string Name, string Description, object Parameters);
/// <summary>RawPart: جزء الاستدعاء كما أرسله المزود، يُعاد له حرفياً في الجولة التالية.</summary>
public record AssistantToolCall(string Name, JsonElement? Args, string? RawPart = null);
public record AssistantToolResult(string Name, string Json);

/// <summary>Role: user | model | tool. نص، أو صوت، أو استدعاءات أدوات، أو نتائجها.</summary>
public record AssistantContent(string Role, string? Text = null, byte[]? Audio = null, string? AudioMimeType = null,
    IReadOnlyList<AssistantToolCall>? Calls = null, IReadOnlyList<AssistantToolResult>? Results = null);

public record AssistantProviderRequest(string SystemPrompt, IReadOnlyList<AssistantToolDefinition> Tools, IReadOnlyList<AssistantContent> Contents);
public record AssistantProviderReply(string? Text, IReadOnlyList<AssistantToolCall> ToolCalls, string Kind = AssistantKinds.Answer);

/// <summary>مزود النموذج. لا يصل للقاعدة أبداً: كل ما يعرفه ما تعيده الأدوات بعد فحص الحساب في السيرفر.</summary>
public interface IAssistantProvider
{
    string Name { get; }
    bool IsSandbox { get; }
    bool SupportsAudio { get; }
    Task<AssistantProviderReply> GenerateAsync(AssistantProviderRequest request, CancellationToken ct);
}
