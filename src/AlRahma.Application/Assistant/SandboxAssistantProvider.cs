using System.Text;
using System.Text.Json;

namespace AlRahma.Application.Assistant;

/// <summary>
/// مزود تجريبي بقواعد بسيطة، بلا ذكاء اصطناعي ولا اتصال خارجي، لاختبار المسار كاملاً بدون مفتاح.
/// كل رد يبدأ بعبارة (وضع تجريبي)، و is_sandbox = true في الاستجابة.
/// </summary>
public class SandboxAssistantProvider : IAssistantProvider
{
    public const string Label = "(وضع تجريبي) ";
    public string Name => "sandbox";
    public bool IsSandbox => true;
    public bool SupportsAudio => false;

    public Task<AssistantProviderReply> GenerateAsync(AssistantProviderRequest request, CancellationToken ct)
    {
        var lastUser = request.Contents.LastOrDefault(c => c.Role == "user");
        var results = request.Contents.SkipWhile(c => !ReferenceEquals(c, lastUser)).Skip(1)
            .Where(c => c.Role == "tool").SelectMany(c => c.Results ?? []).ToList();
        var text = AssistantSafety.Normalize(lastUser?.Text);
        return Task.FromResult(results.Count == 0 ? FirstStep(text) : NextStep(text, results));
    }

    private static AssistantProviderReply Say(string text, string kind = AssistantKinds.Answer) => new(Label + text, [], kind);
    private static AssistantProviderReply Call(string tool, object? args = null) =>
        new(null, [new AssistantToolCall(tool, JsonSerializer.SerializeToElement(args ?? new { }))]);

    private static bool Has(string text, params string[] terms) => terms.Any(text.Contains);

    private static AssistantProviderReply FirstStep(string t)
    {
        // بيانات المريض نفسه تسبق فحص النصيحة: «شنو علاجي» سؤال عن وصفته لا طلب علاج
        if (Has(t, "علاجي", "وصفتي", "وصفاتي", "ادويتي", "دوائي", "دواي")) return Call("get_my_prescriptions");
        if (Has(t, "شنو علاج", "علاج ل", "دواء ل", "دوا ل", "شكد جرعه", "جرعه", "شنو عندي", "شنو مرضي", "شخص", "تشخيص", "اعالج", "شلون اتعالج"))
            return Say(AssistantSafety.AdviceRefusal, AssistantKinds.MedicalAdviceRefused);
        if (Has(t, "وصفه", "وصفات", "دواء", "ادويه")) return Call("get_my_prescriptions");
        if (Has(t, "تحليل", "تحاليل", "فحص", "نتيجه", "نتايج", "اشعه", "سونار", "مفراس", "رنين")) return Call("get_my_lab_imaging_status");
        if (Has(t, "موعد", "مواعيد", "حجز", "حجوزات", "دوري", "طابور", "زيارتي")) return Call("get_my_appointments", new { scope = "upcoming" });
        if (Has(t, "فرع", "فروع", "عنوان", "وين", "دوام", "موقع", "رقم", "تلفون", "هاتف")) return Call("get_branch_info");
        if (Has(t, "عايله", "عائله", "اهلي", "افراد")) return Call("list_family_members");
        return Say("أگدر أساعدك بمواعيدك، وصفاتك، تحاليلك وأشعتك، ومعلومات الفروع.", AssistantKinds.OutOfScope);
    }

    private static AssistantProviderReply NextStep(string text, List<AssistantToolResult> results)
    {
        var last = results[^1];
        using var doc = JsonDocument.Parse(last.Json);
        var data = doc.RootElement.GetProperty("untrusted_data");
        if (data.TryGetProperty("error", out _)) return Say("ما لگيت هذي المعلومات بحسابك.");
        return last.Name switch
        {
            "get_my_appointments" => Say(Appointments(data)),
            "get_my_prescriptions" => Say(Prescriptions(data)),
            "get_my_lab_imaging_status" => Orders(text, data),
            "get_order_result" => Say(Result(data)),
            "get_branch_info" => Say(Branches(data)),
            "list_family_members" => Say("أفراد حسابك: " + string.Join("، ", Items(data).Select(i => i.GetProperty("name").GetString()))),
            _ => Say("تم."),
        };
    }

    private static IEnumerable<JsonElement> Items(JsonElement data) => data.GetProperty("items").EnumerateArray();
    private static string S(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Appointments(JsonElement data)
    {
        var list = Items(data).ToList();
        if (list.Count == 0) return "ما عندك مواعيد قادمة.";
        return string.Join(" ", list.Take(5).Select(a =>
            $"{S(a, "patient_name")}: موعد مع {S(a, "doctor_name")} يوم {S(a, "date_ar")} الساعة {S(a, "time_ar")}، رقمك {S(a, "ticket_number")}."));
    }

    private static string Prescriptions(JsonElement data)
    {
        var list = Items(data).ToList();
        if (list.Count == 0) return "ما عندك وصفات مسجلة.";
        return string.Join(" ", list.Take(3).Select(p =>
            $"وصفة {S(p, "prescription_code")} من {S(p, "doctor_name")} ({S(p, "date_ar")}): " +
            string.Join("، ", p.GetProperty("lines").EnumerateArray().Select(l => S(l, "drug_name"))) + "."));
    }

    private static AssistantProviderReply Orders(string text, JsonElement data)
    {
        var list = Items(data).ToList();
        if (list.Count == 0) return Say("ما عندك تحاليل أو أشعة مطلوبة.");
        // سؤال عن النتيجة: أول نتيجة جاهزة تمر عبر أداة get_order_result وشرط الدفع فيها
        var ready = list.FirstOrDefault(o => o.GetProperty("result_ready").GetBoolean());
        if (Has(text, "نتيجه", "نتايج") && ready.ValueKind == JsonValueKind.Object)
            return Call("get_order_result", new { order_item_id = S(ready, "order_item_id") });
        return Say(string.Join(" ", list.Take(5).Select(o => $"{S(o, "service_name")}: {S(o, "status_ar")}.")));
    }

    private static string Result(JsonElement r)
    {
        if (r.TryGetProperty("locked", out var locked) && locked.GetBoolean())
            return $"نتيجة {S(r, "service_name")} جاهزة، وتنفتح بعد دفع {r.GetProperty("amount_due").GetInt64():N0} د.ع.";
        if (r.TryGetProperty("ready", out var ready) && !ready.GetBoolean()) return "النتيجة بعد ما جاهزة.";
        var sb = new StringBuilder($"نتيجة {S(r, "service_name")}: ");
        if (r.TryGetProperty("values", out var values))
            sb.Append(string.Join("، ", values.EnumerateArray().Select(v => $"{S(v, "name")} {v.GetProperty("value").GetDecimal()} {S(v, "unit")}".Trim())));
        sb.Append(S(r, "text_result")).Append(S(r, "impression"));
        return sb.Append(". ناقشها ويا طبيبك.").ToString();
    }

    private static string Branches(JsonElement data)
    {
        var list = Items(data).ToList();
        if (list.Count == 0) return "ما لگيت هذا الفرع.";
        return string.Join(" ", list.Take(5).Select(b => $"{S(b, "name")}: {S(b, "address")}، {S(b, "status_ar")}."));
    }
}
