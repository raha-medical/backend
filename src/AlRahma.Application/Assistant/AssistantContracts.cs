using System.Text.Json;

namespace AlRahma.Application.Assistant;

/// <summary>
/// إعدادات المساعد (Assistant__*). المفتاح سري على السيرفر فقط ولا يصل المتصفح.
/// ApiKey الفارغ يُقرأ من متغير البيئة GEMINI_API_KEY (DependencyInjection في Infrastructure).
/// gemini بلا مفتاح: الإنتاج يرفض التشغيل (Program.cs)، وغيره يعمل بالمزود التجريبي.
/// Enabled مفتاح طوارئ تقني؛ التشغيل والإيقاف والحد اليومي للمدير من الإعدادات العامة (assistant.enabled و assistant.daily_limit).
/// </summary>
public class AssistantOptions
{
    public const string Section = "Assistant";
    public const string ApiKeyEnvironmentVariable = "GEMINI_API_KEY";
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "sandbox";
    public string ApiKey { get; set; } = "";
    /// <summary>قابل للتغيير بـ Assistant__Model دون تعديل الكود.</summary>
    public string Model { get; set; } = "gemini-3.5-flash-lite";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    /// <summary>مستوى التفكير لنماذج Gemini 3 (minimal | low | medium | high). فارغ = افتراضي النموذج.</summary>
    public string ThinkingLevel { get; set; } = "minimal";
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>انتظار قبل المحاولة الثانية عند 429 من المزود (0 إلى 10 ثوانٍ).</summary>
    public int RateLimitRetrySeconds { get; set; } = 4;
    public bool WantsGemini => string.Equals(Provider, "gemini", StringComparison.OrdinalIgnoreCase);
    public bool UseGemini => WantsGemini && !string.IsNullOrWhiteSpace(ApiKey);
}

public record AssistantHistoryItem(string? Role, string? Text);
public record AssistantAudioInput(string? MimeType, string? Base64);
public record AssistantChatRequest(string? Message, List<AssistantHistoryItem>? History = null, Guid? PatientId = null, AssistantAudioInput? Audio = null);

/// <summary>
/// اقتراح حجز من المساعد. لا يُنشئ أي حجز: التطبيق يعرض زر «تأكيد الحجز» ويرسل POST /api/v1/bookings بنفس
/// booking_id و patient_id و session_id و visit_kind، والمراجع وحده من يؤكد.
/// </summary>
public record AssistantProposalDto(Guid BookingId, Guid PatientId, string PatientFirstName, Guid SessionId, Guid DoctorId, string DoctorName,
    string Specialty, string ClinicName, string BranchName, DateOnly Date, string DateAr, string Start, string End, string? EstimatedTimeAr,
    long? NewVisitPrice, long? FollowUpPrice, string VisitKind);

/// <summary>زر في التطبيق: open_results | open_prescriptions | open_invoices | open_support. القيم الطبية لا تمر عبر المساعد أبداً.</summary>
public record AssistantActionDto(string Type, string LabelAr, Guid? PatientId = null);

public record AssistantChatResponse(string Reply, string Kind, string Provider, bool IsSandbox, IReadOnlyList<string> ToolsUsed,
    int DailyLimit, int RemainingToday, string DisclaimerAr, AssistantProposalDto? Proposal = null, IReadOnlyList<AssistantActionDto>? Actions = null);
public record AssistantStatusDto(bool Enabled, string Provider, bool IsSandbox, bool AudioInput, int DailyLimit, int RemainingToday, string DisclaimerAr);

public static class AssistantKinds
{
    public const string Answer = "answer";
    public const string Emergency = "emergency";
    public const string MedicalAdviceRefused = "medical_advice_refused";
    public const string OtherPersonRefused = "other_person_refused";
    public const string OutOfScope = "out_of_scope";
    public const string AudioNotSupported = "audio_not_supported";
    public static bool IsRefusal(string kind) => kind is MedicalAdviceRefused or OtherPersonRefused or OutOfScope;
}

/// <summary>Parameters = مخطط JSON لوسائط الأداة.</summary>
public record AssistantToolDefinition(string Name, string Description, object Parameters);
/// <summary>RawPart: جزء الاستدعاء كما أرسله المزود (مع توقيع التفكير)، يُعاد له حرفياً في الجولة التالية.</summary>
public record AssistantToolCall(string Name, JsonElement? Args, string? RawPart = null);
public record AssistantToolResult(string Name, string Json);

/// <summary>Role: user | model | tool. نص، أو صوت، أو استدعاءات أدوات، أو نتائجها.</summary>
public record AssistantContent(string Role, string? Text = null, byte[]? Audio = null, string? AudioMimeType = null,
    IReadOnlyList<AssistantToolCall>? Calls = null, IReadOnlyList<AssistantToolResult>? Results = null);

public record AssistantProviderRequest(string SystemPrompt, IReadOnlyList<AssistantToolDefinition> Tools, IReadOnlyList<AssistantContent> Contents);
public record AssistantProviderReply(string? Text, IReadOnlyList<AssistantToolCall> ToolCalls, string Kind = AssistantKinds.Answer);

/// <summary>
/// فشل المزود مصنفاً للإحصاءات والسجل: rate_limited (429) | server (5xx) | auth (400/401/403، مثل مفتاح خاطئ) | timeout | network | other.
/// لا يحمل نص رد المزود أبداً (قد يحتوي بيانات الطلب أو المفتاح).
/// </summary>
public class AssistantProviderException(string errorType, int? httpStatus) : Exception($"assistant provider failed: {errorType}")
{
    public string ErrorType { get; } = errorType;
    public int? HttpStatus { get; } = httpStatus;
    public static string Classify(int status) => status switch
    {
        429 => "rate_limited",
        >= 500 => "server",
        400 or 401 or 403 => "auth",
        _ => "other",
    };
}

/// <summary>مزود النموذج. لا يصل للقاعدة أبداً: كل ما يعرفه ما تعيده الأدوات بعد فحص الحساب في السيرفر.</summary>
public interface IAssistantProvider
{
    string Name { get; }
    bool IsSandbox { get; }
    bool SupportsAudio { get; }
    Task<AssistantProviderReply> GenerateAsync(AssistantProviderRequest request, CancellationToken ct);
}

/// <summary>أرقام يومية فقط (بلا أي نص). errors حسب النوع، و confirmed_bookings = اقتراحات أكدها المراجع فعلاً بنفس booking_id.</summary>
public record AssistantErrorCountsDto(int RateLimited, int Server, int Auth, int Timeout, int Network, int Other);
public record AssistantDayStatsDto(DateOnly Date, int Messages, int Patients, int Emergencies, int Refusals, int LimitHits, int Proposals,
    int ConfirmedBookings, AssistantErrorCountsDto Errors, int? AvgLatencyMs, int MaxLatencyMs);
public record AssistantStatsDto(DateOnly From, DateOnly To, bool Enabled, int DailyLimit, string Provider, string? Model,
    AssistantDayStatsDto Totals, IReadOnlyList<AssistantDayStatsDto> Days);
