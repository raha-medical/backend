using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.PatientApp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlRahma.Application.Assistant;

/// <summary>
/// محادثة المساعد لحساب مريض. لا تُحفظ الرسائل: السجل يأتي من التطبيق مع كل طلب ويُعامل كنص غير موثوق.
/// الطوارئ تُفحص قبل كل شيء (لا يمنعها حد الاستخدام، ولا تُرسل لأي مزود). التدقيق لأسماء الأدوات فقط بلا محتوى.
/// </summary>
public class AssistantService(IAppDbContext db, IClock clock, IAuditLogger audit, IOptions<AssistantOptions> options, IAssistantProvider provider,
    AssistantTools tools, PatientPortalService portal, ILogger<AssistantService> log)
{
    public const int MaxHistory = 20;
    public const int MaxTextLength = 2000;
    public const int MaxAudioBytes = 5 * 1024 * 1024;
    public const int MaxToolRounds = 4;
    public static readonly string[] AudioTypes = ["audio/webm", "audio/ogg", "audio/mpeg", "audio/mp4", "audio/wav"];

    private AssistantOptions Opt => options.Value;

    public async Task<AssistantStatusDto> StatusAsync(CancellationToken ct) =>
        new(Opt.Enabled, provider.Name, provider.IsSandbox, provider.SupportsAudio, Opt.MaxMessagesPerHour,
            Math.Max(0, Opt.MaxMessagesPerHour - await UsedThisHourAsync(ct)));

    public async Task<AssistantChatResponse> ChatAsync(AssistantChatRequest req, CancellationToken ct)
    {
        if (!Opt.Enabled) throw new AppException(503, "ASSISTANT_UNAVAILABLE", "المساعد غير متاح حالياً");
        var message = req.Message?.Trim();
        var history = req.History ?? [];
        new Validator()
            .Check(!string.IsNullOrEmpty(message) || req.Audio is not null, "message", "اكتب رسالتك")
            .Check(message is null || message.Length <= MaxTextLength, "message", $"{MaxTextLength} حرف كحد أقصى")
            .Check(history.All(h => h.Role is "user" or "assistant"), "history", "role: user | assistant")
            .Check(history.All(h => (h.Text?.Length ?? 0) <= MaxTextLength), "history", $"{MaxTextLength} حرف كحد أقصى للرسالة")
            .ThrowIfInvalid();
        var audio = DecodeAudio(req.Audio);
        if (req.PatientId is { } pid) await portal.EnsureLinkedAsync(pid, ct);

        // الطوارئ أولاً: رد ثابت من السيرفر، لا يُستهلك من الحد ولا يخرج لأي مزود
        if (AssistantSafety.IsEmergency(message))
            return Build(AssistantSafety.EmergencyMessage, AssistantKinds.Emergency, [], await RemainingAsync(ct));

        var used = await CountMessageAsync(ct);
        var remaining = Math.Max(0, Opt.MaxMessagesPerHour - used);
        if (audio is not null && !provider.SupportsAudio && string.IsNullOrEmpty(message))
            return Build("الوضع الحالي ما يفهم الصوت، اكتب رسالتك رجاءً.", AssistantKinds.AudioNotSupported, [], remaining);

        var contents = history.TakeLast(MaxHistory).Where(h => !string.IsNullOrWhiteSpace(h.Text))
            .Select(h => new AssistantContent(h.Role == "assistant" ? "model" : "user", h.Text!.Trim())).ToList();
        contents.Add(new AssistantContent("user", message, audio, audio is null ? null : req.Audio!.MimeType));

        var used_tools = new List<string>();
        var system = AssistantSafety.SystemPrompt(clock.BaghdadToday());
        for (var round = 0; round <= MaxToolRounds; round++)
        {
            var reply = await GenerateAsync(new AssistantProviderRequest(system, AssistantTools.Definitions, contents), ct);
            if (reply.ToolCalls.Count == 0)
                return Build(string.IsNullOrWhiteSpace(reply.Text) ? "ما گدرت أجاوب هسه، حاول مرة ثانية." : reply.Text.Trim(), reply.Kind, used_tools, remaining);
            if (round == MaxToolRounds) break;

            var results = new List<AssistantToolResult>();
            foreach (var call in reply.ToolCalls)
            {
                var (ok, json) = await tools.ExecuteAsync(call.Name, call.Args, req.PatientId, ct);
                results.Add(new AssistantToolResult(call.Name, json));
                if (ok && !used_tools.Contains(call.Name)) used_tools.Add(call.Name);
                // اسم الأداة والنتيجة فقط، بلا نص الرسالة ولا البيانات المعادة
                audit.Add("ASSISTANT_TOOL_CALL", "assistant", portal.AccountId, after: new { tool = Trim(call.Name), ok, patient_id = req.PatientId });
            }
            await db.SaveChangesAsync(ct);
            contents.Add(new AssistantContent("model", Calls: reply.ToolCalls));
            contents.Add(new AssistantContent("tool", Results: results));
        }
        return Build("ما گدرت أكمل طلبك، حاول تسأل بطريقة أبسط.", AssistantKinds.Answer, used_tools, remaining);
    }

    private static string Trim(string name) => name.Length <= 60 ? name : name[..60];

    private async Task<AssistantProviderReply> GenerateAsync(AssistantProviderRequest request, CancellationToken ct)
    {
        try { return await provider.GenerateAsync(request, ct); }
        catch (Exception e) when (e is not AppException && !ct.IsCancellationRequested)
        {
            // النوع فقط: نص الخطأ قد يحمل بيانات من الطلب
            log.LogWarning("Assistant provider {Provider} failed: {Error}", provider.Name, e.GetType().Name);
            throw new AppException(503, "ASSISTANT_UNAVAILABLE", "المساعد غير متاح حالياً", "حاول بعد قليل");
        }
    }

    private AssistantChatResponse Build(string reply, string kind, IReadOnlyList<string> toolsUsed, int remaining) =>
        new(reply, kind, provider.Name, provider.IsSandbox, toolsUsed, remaining, AssistantSafety.Disclaimer);

    private static byte[]? DecodeAudio(AssistantAudioInput? a)
    {
        if (a is null) return null;
        new Validator().Check(a.MimeType is not null && AudioTypes.Contains(a.MimeType), "audio.mime_type", string.Join(" | ", AudioTypes)).ThrowIfInvalid();
        if ((a.Base64?.Length ?? 0) > MaxAudioBytes / 3 * 4 + 8)
            throw new AppException(413, "ASSISTANT_AUDIO_TOO_LARGE", "التسجيل الصوتي طويل", "5MB كحد أقصى");
        try { return Convert.FromBase64String(a.Base64 ?? ""); }
        catch (FormatException) { throw AppException.Validation(new Dictionary<string, string[]> { ["audio.base64"] = ["ترميز غير صالح"] }); }
    }

    private DateTimeOffset WindowStart
    {
        get { var n = clock.UtcNow; return new DateTimeOffset(n.Year, n.Month, n.Day, n.Hour, 0, 0, TimeSpan.Zero); }
    }

    private async Task<int> RemainingAsync(CancellationToken ct) => Math.Max(0, Opt.MaxMessagesPerHour - await UsedThisHourAsync(ct));

    private Task<int> UsedThisHourAsync(CancellationToken ct)
    {
        var account = portal.AccountId;
        var window = WindowStart;
        return db.ScalarAsync<int>($"""
            SELECT COALESCE((SELECT messages FROM assistant_usage WHERE patient_account_id = {account} AND window_start = {window}), 0) AS "Value"
            """, ct);
    }

    /// <summary>عداد ذري في القاعدة (يعمل مع أكثر من نسخة من السيرفر)، و429 عند تجاوز الحد.</summary>
    private async Task<int> CountMessageAsync(CancellationToken ct)
    {
        var account = portal.AccountId;
        var window = WindowStart;
        var count = await db.ScalarAsync<int>($"""
            INSERT INTO assistant_usage AS u (patient_account_id, window_start, messages) VALUES ({account}, {window}, 1)
            ON CONFLICT (patient_account_id, window_start) DO UPDATE SET messages = u.messages + 1
            RETURNING messages AS "Value"
            """, ct);
        if (count == 1) await db.ExecuteAsync($"DELETE FROM assistant_usage WHERE patient_account_id = {account} AND window_start < {window}", ct);
        if (count > Opt.MaxMessagesPerHour)
        {
            var retry = (int)Math.Ceiling((window.AddHours(1) - clock.UtcNow).TotalSeconds);
            throw new AppException(429, "RATE_LIMITED", "وصلت حد رسائل المساعد لهذه الساعة", "حاول بعد قليل")
                { Extensions = new Dictionary<string, object?> { ["retry_after_seconds"] = retry } };
        }
        return count;
    }
}
