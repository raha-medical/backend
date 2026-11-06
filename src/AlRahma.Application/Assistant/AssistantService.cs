using System.Diagnostics;
using System.Text.Json;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.PatientApp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlRahma.Application.Assistant;

/// <summary>
/// محادثة المساعد لحساب مريض. لا تُحفظ الرسائل: السجل يأتي من التطبيق مع كل طلب ويُعامل كنص غير موثوق.
/// الطوارئ تُفحص قبل كل شيء (لا تُحسب من الحد اليومي، ولا تُرسل لأي مزود). التدقيق لأسماء الأدوات فقط بلا محتوى.
/// التشغيل والحد اليومي من الإعدادات العامة (assistant.enabled و assistant.daily_limit)، واليوم بتوقيت بغداد.
/// الإحصاءات اليومية (assistant_daily_stats) أرقام فقط، والسجل يحمل نوع الخطأ ورمز HTTP فقط.
/// </summary>
public class AssistantService(IAppDbContext db, IClock clock, IAuditLogger audit, IOptions<AssistantOptions> options, IAssistantProvider provider,
    AssistantTools tools, PatientPortalService portal, SettingsReader settings, ILogger<AssistantService> log)
{
    public const int MaxHistory = 20;
    public const int MaxTextLength = 2000;
    public const int MaxAudioBytes = 5 * 1024 * 1024;
    public const int MaxToolRounds = 5;
    public const int DefaultDailyLimit = 30;
    public const int MaxStatsDays = 92;
    public static readonly string[] AudioTypes = ["audio/webm", "audio/ogg", "audio/mpeg", "audio/mp4", "audio/wav"];

    private AssistantOptions Opt => options.Value;

    private async Task<bool> EnabledAsync(CancellationToken ct) =>
        Opt.Enabled && await settings.GetLongAsync(SettingsReader.AssistantEnabled, null, 1, ct) == 1;

    private async Task<int> DailyLimitAsync(CancellationToken ct) =>
        (int)Math.Clamp(await settings.GetLongAsync(SettingsReader.AssistantDailyLimit, null, DefaultDailyLimit, ct), 1, 10_000);

    public async Task<AssistantStatusDto> StatusAsync(CancellationToken ct)
    {
        var limit = await DailyLimitAsync(ct);
        return new(await EnabledAsync(ct), provider.Name, provider.IsSandbox, provider.SupportsAudio, limit,
            Math.Max(0, limit - await UsedTodayAsync(ct)), AssistantSafety.Disclaimer);
    }

    public async Task<AssistantChatResponse> ChatAsync(AssistantChatRequest req, CancellationToken ct)
    {
        if (!await EnabledAsync(ct)) throw new AppException(503, "ASSISTANT_DISABLED", AssistantSafety.DisabledMessage);
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
        var limit = await DailyLimitAsync(ct);

        // الطوارئ أولاً: رد ثابت من السيرفر، لا يُستهلك من الحد ولا يخرج لأي مزود
        if (AssistantSafety.IsEmergency(message))
        {
            await StatAsync(new Delta { Messages = 1, Emergencies = 1 }, ct);
            return Build(AssistantSafety.EmergencyMessage, AssistantKinds.Emergency, [], limit, Math.Max(0, limit - await UsedTodayAsync(ct)));
        }

        var used = await CountMessageAsync(limit, ct);
        var remaining = Math.Max(0, limit - used);
        if (audio is not null && !provider.SupportsAudio && string.IsNullOrEmpty(message))
        {
            await UncountAsync(ct);
            return Build("الوضع الحالي ما يفهم الصوت، اكتب رسالتك رجاءً.", AssistantKinds.AudioNotSupported, [], limit, remaining + 1);
        }

        var contents = history.TakeLast(MaxHistory).Where(h => !string.IsNullOrWhiteSpace(h.Text))
            .Select(h => new AssistantContent(h.Role == "assistant" ? "model" : "user", h.Text!.Trim())).ToList();
        contents.Add(new AssistantContent("user", message, audio, audio is null ? null : req.Audio!.MimeType));

        var usedTools = new List<string>();
        var system = AssistantSafety.SystemPrompt(clock.BaghdadToday());
        var watch = Stopwatch.StartNew();
        string? text = null;
        var kind = AssistantKinds.Answer;
        for (var round = 0; round <= MaxToolRounds; round++)
        {
            var reply = await GenerateAsync(new AssistantProviderRequest(system, AssistantTools.Definitions, contents), ct);
            if (reply.ToolCalls.Count == 0)
            {
                (text, kind) = string.IsNullOrWhiteSpace(reply.Text)
                    ? ("ما گدرت أجاوب هسه، حاول مرة ثانية.", reply.Kind)
                    : AssistantSafety.Classify(reply.Text);
                if (kind == AssistantKinds.Answer && reply.Kind != AssistantKinds.Answer) kind = reply.Kind;
                break;
            }
            if (round == MaxToolRounds) break;

            var results = new List<AssistantToolResult>();
            foreach (var call in reply.ToolCalls)
            {
                var (ok, json) = await tools.ExecuteAsync(call.Name, call.Args, req.PatientId, ct);
                results.Add(new AssistantToolResult(call.Name, json));
                if (ok && !usedTools.Contains(call.Name)) usedTools.Add(call.Name);
                // اسم الأداة والنتيجة فقط، بلا نص الرسالة ولا البيانات المعادة
                audit.Add("ASSISTANT_TOOL_CALL", "assistant", portal.AccountId, after: new { tool = Trim(call.Name), ok, patient_id = req.PatientId });
            }
            await db.SaveChangesAsync(ct);
            contents.Add(new AssistantContent("model", Calls: reply.ToolCalls));
            contents.Add(new AssistantContent("tool", Results: results));
        }
        text ??= "ما گدرت أكمل طلبك، حاول تسأل بطريقة أبسط.";

        var proposal = kind == AssistantKinds.Answer ? tools.Proposal : null;
        // طوارئ التقطها النموذج: لا تُحسب من الحد
        if (kind == AssistantKinds.Emergency) { await UncountAsync(ct); remaining++; }
        await StatAsync(new Delta
        {
            Messages = 1, Emergencies = kind == AssistantKinds.Emergency ? 1 : 0, Refusals = AssistantKinds.IsRefusal(kind) ? 1 : 0,
            Proposals = proposal is null ? 0 : 1, LatencyMs = (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds), LatencyCount = 1,
        }, ct);
        var actions = kind == AssistantKinds.Answer ? tools.Actions : [];
        return Build(text, kind, usedTools, limit, remaining, proposal, actions);
    }

    private static string Trim(string name) => name.Length <= 60 ? name : name[..60];

    private async Task<AssistantProviderReply> GenerateAsync(AssistantProviderRequest request, CancellationToken ct)
    {
        try { return await provider.GenerateAsync(request, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // النوع ورمز HTTP فقط: نص الخطأ قد يحمل بيانات من الطلب
            var (type, status) = e is AssistantProviderException p ? (p.ErrorType, p.HttpStatus) : ("other", (int?)null);
            log.LogWarning("Assistant provider {Provider} failed: {ErrorType} {HttpStatus}", provider.Name, type, status);
            await UncountAsync(ct);
            await StatAsync(new Delta
            {
                RateLimited = type == "rate_limited" ? 1 : 0, Server = type == "server" ? 1 : 0, Auth = type == "auth" ? 1 : 0,
                Timeout = type == "timeout" ? 1 : 0, Network = type == "network" ? 1 : 0, Other = type is "other" ? 1 : 0,
            }, ct);
            throw new AppException(503, "ASSISTANT_UNAVAILABLE", AssistantSafety.OutageMessage);
        }
    }

    private AssistantChatResponse Build(string reply, string kind, IReadOnlyList<string> toolsUsed, int limit, int remaining,
        AssistantProposalDto? proposal = null, IReadOnlyList<AssistantActionDto>? actions = null) =>
        new(reply, kind, provider.Name, provider.IsSandbox, toolsUsed, limit, Math.Clamp(remaining, 0, limit), AssistantSafety.Disclaimer, proposal, actions ?? []);

    private static byte[]? DecodeAudio(AssistantAudioInput? a)
    {
        if (a is null) return null;
        new Validator().Check(a.MimeType is not null && AudioTypes.Contains(a.MimeType), "audio.mime_type", string.Join(" | ", AudioTypes)).ThrowIfInvalid();
        if ((a.Base64?.Length ?? 0) > MaxAudioBytes / 3 * 4 + 8)
            throw new AppException(413, "ASSISTANT_AUDIO_TOO_LARGE", "التسجيل الصوتي طويل", "5MB كحد أقصى");
        try { return Convert.FromBase64String(a.Base64 ?? ""); }
        catch (FormatException) { throw AppException.Validation(new Dictionary<string, string[]> { ["audio.base64"] = ["ترميز غير صالح"] }); }
    }

    // ------------------------------------------------------------------ الحد اليومي (بتوقيت بغداد)

    /// <summary>بداية اليوم الحالي بتوقيت بغداد (UTC). assistant_usage.window_start يحملها منذ الترحيل 026.</summary>
    private DateTimeOffset DayStart => ClockExtensions.ToUtc(clock.BaghdadToday(), TimeOnly.MinValue);

    private Task<int> UsedTodayAsync(CancellationToken ct)
    {
        var account = portal.AccountId;
        var window = DayStart;
        return db.ScalarAsync<int>($"""
            SELECT COALESCE((SELECT messages FROM assistant_usage WHERE patient_account_id = {account} AND window_start = {window}), 0) AS "Value"
            """, ct);
    }

    /// <summary>عداد ذري في القاعدة (يعمل مع أكثر من نسخة من السيرفر)، و429 عند تجاوز الحد اليومي.</summary>
    private async Task<int> CountMessageAsync(int limit, CancellationToken ct)
    {
        var account = portal.AccountId;
        var window = DayStart;
        var count = await db.ScalarAsync<int>($"""
            INSERT INTO assistant_usage AS u (patient_account_id, window_start, messages) VALUES ({account}, {window}, 1)
            ON CONFLICT (patient_account_id, window_start) DO UPDATE SET messages = u.messages + 1
            RETURNING messages AS "Value"
            """, ct);
        if (count == 1)
        {
            // أول رسالة لهذا الحساب اليوم: مراجع جديد في إحصاء اليوم، وتنظيف الأيام السابقة والاقتراحات القديمة
            await db.ExecuteAsync($"DELETE FROM assistant_usage WHERE patient_account_id = {account} AND window_start < {window}", ct);
            var old = window.AddDays(-90);
            await db.ExecuteAsync($"DELETE FROM assistant_proposals WHERE created_at < {old}", ct);
            await StatAsync(new Delta { Patients = 1 }, ct);
        }
        if (count > limit)
        {
            await UncountAsync(ct);
            await StatAsync(new Delta { LimitHits = 1 }, ct);
            var retry = (int)Math.Ceiling((window.AddDays(1) - clock.UtcNow).TotalSeconds);
            throw new AppException(429, "RATE_LIMITED", AssistantSafety.LimitMessage, $"الحد {limit} رسالة باليوم")
                { Extensions = new Dictionary<string, object?> { ["retry_after_seconds"] = retry, ["daily_limit"] = limit } };
        }
        return count;
    }

    /// <summary>يرجع رسالة للعداد: رد طوارئ، أو تعطل المزود، أو رفض بسبب الحد.</summary>
    private Task UncountAsync(CancellationToken ct)
    {
        var account = portal.AccountId;
        var window = DayStart;
        return db.ExecuteAsync($"UPDATE assistant_usage SET messages = GREATEST(messages - 1, 0) WHERE patient_account_id = {account} AND window_start = {window}", ct);
    }

    // ------------------------------------------------------------------ الإحصاءات اليومية (أرقام فقط)

    private sealed record Delta
    {
        public int Messages { get; init; }
        public int Patients { get; init; }
        public int Emergencies { get; init; }
        public int Refusals { get; init; }
        public int LimitHits { get; init; }
        public int Proposals { get; init; }
        public int RateLimited { get; init; }
        public int Server { get; init; }
        public int Auth { get; init; }
        public int Timeout { get; init; }
        public int Network { get; init; }
        public int Other { get; init; }
        public int LatencyMs { get; init; }
        public int LatencyCount { get; init; }
    }

    /// <summary>عداد ذري لليوم. فشله لا يوقف المحادثة.</summary>
    private async Task StatAsync(Delta d, CancellationToken ct)
    {
        var day = clock.BaghdadToday();
        var now = clock.UtcNow;
        try
        {
            await db.ExecuteAsync($"""
                INSERT INTO assistant_daily_stats AS s (stat_date, messages, patients, emergencies, refusals, limit_hits, proposals,
                    errors_rate_limited, errors_server, errors_auth, errors_timeout, errors_network, errors_other,
                    latency_ms_total, latency_count, latency_ms_max, updated_at)
                VALUES ({day}, {d.Messages}, {d.Patients}, {d.Emergencies}, {d.Refusals}, {d.LimitHits}, {d.Proposals},
                    {d.RateLimited}, {d.Server}, {d.Auth}, {d.Timeout}, {d.Network}, {d.Other},
                    {(long)d.LatencyMs}, {d.LatencyCount}, {d.LatencyMs}, {now})
                ON CONFLICT (stat_date) DO UPDATE SET
                    messages = s.messages + EXCLUDED.messages, patients = s.patients + EXCLUDED.patients,
                    emergencies = s.emergencies + EXCLUDED.emergencies, refusals = s.refusals + EXCLUDED.refusals,
                    limit_hits = s.limit_hits + EXCLUDED.limit_hits, proposals = s.proposals + EXCLUDED.proposals,
                    errors_rate_limited = s.errors_rate_limited + EXCLUDED.errors_rate_limited, errors_server = s.errors_server + EXCLUDED.errors_server,
                    errors_auth = s.errors_auth + EXCLUDED.errors_auth, errors_timeout = s.errors_timeout + EXCLUDED.errors_timeout,
                    errors_network = s.errors_network + EXCLUDED.errors_network, errors_other = s.errors_other + EXCLUDED.errors_other,
                    latency_ms_total = s.latency_ms_total + EXCLUDED.latency_ms_total, latency_count = s.latency_count + EXCLUDED.latency_count,
                    latency_ms_max = GREATEST(s.latency_ms_max, EXCLUDED.latency_ms_max), updated_at = EXCLUDED.updated_at
                """, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Assistant stats update failed: {Error}", e.GetType().Name);
        }
    }

    private sealed record StatRow(DateOnly stat_date, int messages, int patients, int emergencies, int refusals, int limit_hits, int proposals,
        int confirmed, int errors_rate_limited, int errors_server, int errors_auth, int errors_timeout, int errors_network, int errors_other,
        long latency_ms_total, int latency_count, int latency_ms_max);

    /// <summary>لوحة الإدارة: أرقام يومية فقط. confirmed_bookings = حجوزات أُنشئت فعلاً بـ booking_id اقتراح من المساعد.</summary>
    public async Task<AssistantStatsDto> StatsAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var end = to ?? clock.BaghdadToday();
        var start = from ?? end.AddDays(-13);
        if (end < start || end.DayNumber - start.DayNumber >= MaxStatsDays)
            throw AppException.Validation(new Dictionary<string, string[]> { ["from"] = [$"الفترة {MaxStatsDays} يوماً كحد أقصى"] });
        var json = await db.ScalarAsync<string>($"""
            WITH confirmed AS (
                SELECT (p.created_at AT TIME ZONE 'Asia/Baghdad')::date AS d, count(*)::int AS n
                FROM assistant_proposals p JOIN bookings b ON b.booking_id = p.booking_id
                WHERE (p.created_at AT TIME ZONE 'Asia/Baghdad')::date BETWEEN {start} AND {end}
                GROUP BY 1
            ), days AS (
                SELECT stat_date FROM assistant_daily_stats WHERE stat_date BETWEEN {start} AND {end}
                UNION SELECT d FROM confirmed
            )
            SELECT COALESCE(json_agg(json_build_object(
                'stat_date', x.stat_date, 'messages', COALESCE(s.messages, 0), 'patients', COALESCE(s.patients, 0),
                'emergencies', COALESCE(s.emergencies, 0), 'refusals', COALESCE(s.refusals, 0), 'limit_hits', COALESCE(s.limit_hits, 0),
                'proposals', COALESCE(s.proposals, 0), 'confirmed', COALESCE(c.n, 0),
                'errors_rate_limited', COALESCE(s.errors_rate_limited, 0), 'errors_server', COALESCE(s.errors_server, 0),
                'errors_auth', COALESCE(s.errors_auth, 0), 'errors_timeout', COALESCE(s.errors_timeout, 0),
                'errors_network', COALESCE(s.errors_network, 0), 'errors_other', COALESCE(s.errors_other, 0),
                'latency_ms_total', COALESCE(s.latency_ms_total, 0), 'latency_count', COALESCE(s.latency_count, 0),
                'latency_ms_max', COALESCE(s.latency_ms_max, 0)) ORDER BY x.stat_date DESC), '[]')::text AS "Value"
            FROM days x LEFT JOIN assistant_daily_stats s ON s.stat_date = x.stat_date LEFT JOIN confirmed c ON c.d = x.stat_date
            """, ct);
        var rows = JsonSerializer.Deserialize<List<StatRow>>(json) ?? [];
        var days = rows.Select(ToDto).ToList();
        var total = rows.Aggregate(new StatRow(end, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), (a, r) => new StatRow(end,
            a.messages + r.messages, a.patients + r.patients, a.emergencies + r.emergencies, a.refusals + r.refusals, a.limit_hits + r.limit_hits,
            a.proposals + r.proposals, a.confirmed + r.confirmed, a.errors_rate_limited + r.errors_rate_limited, a.errors_server + r.errors_server,
            a.errors_auth + r.errors_auth, a.errors_timeout + r.errors_timeout, a.errors_network + r.errors_network, a.errors_other + r.errors_other,
            a.latency_ms_total + r.latency_ms_total, a.latency_count + r.latency_count, Math.Max(a.latency_ms_max, r.latency_ms_max)));
        return new AssistantStatsDto(start, end, await EnabledAsync(ct), await DailyLimitAsync(ct), provider.Name,
            provider.IsSandbox ? null : Opt.Model, ToDto(total) with { Date = end }, days);
    }

    private static AssistantDayStatsDto ToDto(StatRow r) => new(r.stat_date, r.messages, r.patients, r.emergencies, r.refusals, r.limit_hits,
        r.proposals, r.confirmed, new AssistantErrorCountsDto(r.errors_rate_limited, r.errors_server, r.errors_auth, r.errors_timeout, r.errors_network, r.errors_other),
        r.latency_count == 0 ? null : (int)(r.latency_ms_total / r.latency_count), r.latency_ms_max);
}
