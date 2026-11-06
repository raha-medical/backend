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
        if (Has(t, "مراجع ثاني", "مريض ثاني", "شخص ثاني", "جاري", "رقم تلفون", "بيانات غيري", "بيانات مريض"))
            return Say(AssistantSafety.OtherPersonRefusal, AssistantKinds.OtherPersonRefused);
        // بيانات المريض نفسه تسبق فحص النصيحة: «شنو علاجي» سؤال عن وصفته لا طلب علاج
        if (Has(t, "علاجي", "وصفتي", "وصفاتي", "ادويتي", "دوائي", "دواي")) return Call("get_my_prescriptions");
        if (Has(t, "شنو علاج", "علاج ل", "دواء ل", "دوا ل", "شكد جرعه", "جرعه", "شنو عندي", "شنو مرضي", "شخص", "تشخيص", "اعالج", "شلون اتعالج"))
            return Say(AssistantSafety.AdviceRefusal, AssistantKinds.MedicalAdviceRefused);
        if (Has(t, "رصيد", "محفظه", "محفظتي")) return Call("get_my_credit");
        if (Has(t, "فاتوره", "فواتير", "دفعت", "باقي علي", "المتبقي")) return Call("get_my_invoices");
        if (Has(t, "استرجاع", "ارجاع", "سياسه", "الغاء", "الغي")) return Call("search_faq", new { query = t });
        if (Has(t, "طرق الدفع", "زين كاش", "كي كارد", "ادفع")) return Call("get_payment_methods");
        if (Has(t, "تخصص", "اختصاص", "اي طبيب", "يا طبيب")) return Call("list_specialties");
        if (Has(t, "احجز", "طبيب", "دكتور")) return Call("find_doctors");
        if (Has(t, "وصفه", "وصفات", "دواء", "ادويه")) return Call("get_my_prescriptions");
        if (Has(t, "تحليل", "تحاليل", "فحص", "نتيجه", "نتايج", "اشعه", "سونار", "مفراس", "رنين")) return Call("get_my_lab_imaging_status");
        if (Has(t, "موعد", "مواعيد", "حجز", "حجوزات", "دوري", "طابور", "زيارتي")) return Call("get_my_appointments", new { scope = "upcoming" });
        if (Has(t, "فرع", "فروع", "عنوان", "وين", "دوام", "موقع", "رقم", "تلفون", "هاتف")) return Call("get_branch_info");
        if (Has(t, "عايله", "عائله", "اهلي", "افراد")) return Call("list_family_members");
        return Say("أگدر أساعدك بالتخصصات والأطباء والحجز، ومواعيدك، ووصفاتك، وتحاليلك، وفواتيرك، ومعلومات الفروع.", AssistantKinds.OutOfScope);
    }

    private static AssistantProviderReply NextStep(string text, List<AssistantToolResult> results)
    {
        var last = results[^1];
        using var doc = JsonDocument.Parse(last.Json);
        var data = doc.RootElement.GetProperty("untrusted_data");
        if (data.TryGetProperty("error", out var error))
            return Say(error.GetString() == "patient_required" ? "لمن الحجز؟ اختار الفرد من القائمة." : "ما لگيت هذي المعلومات بحسابك.");
        return last.Name switch
        {
            "get_my_appointments" => Say(Appointments(data)),
            "get_my_prescriptions" => Say(Prescriptions(data)),
            "get_my_lab_imaging_status" => Say(Orders(data)),
            "get_branch_info" => Say(Branches(data)),
            "list_family_members" => Say("أفراد حسابك: " + string.Join("، ", Items(data).Select(i => S(i, "first_name")))),
            "list_specialties" => Say("التخصصات المتوفرة: " + string.Join("، ", Items(data).Select(i => S(i, "name"))) + "."),
            // «احجز»: أول طبيب ثم أقرب وجبة ثم اقتراح (لا حجز)
            "find_doctors" when Has(text, "احجز") && Items(data).Any() =>
                Call("get_doctor_availability", new { doctor_id = S(Items(data).First(), "doctor_id") }),
            "find_doctors" => Say(Items(data).Any()
                ? "الأطباء: " + string.Join("، ", Items(data).Take(5).Select(d => $"{S(d, "name")} ({S(d, "specialty")})")) + "."
                : "ما لگيت طبيب بهذا الوصف."),
            "get_doctor_availability" when Has(text, "احجز") && data.GetProperty("nearest_available").ValueKind == JsonValueKind.Object =>
                Call("propose_booking", new { session_id = S(data.GetProperty("nearest_available"), "session_id") }),
            "get_doctor_availability" => Say(data.GetProperty("nearest_available") is { ValueKind: JsonValueKind.Object } n
                ? $"أقرب موعد {S(n, "weekday_ar")} {S(n, "date_ar")}."
                : "ما اكو مواعيد متاحة بهالفترة."),
            "propose_booking" => Say($"جهزتلك حجز عند {S(data, "doctor_name")} يوم {S(data, "date_ar")}. اضغط «تأكيد الحجز» حتى يتم."),
            "search_faq" => Say(Items(data).Any() ? S(Items(data).First(), "answer") : "ما لگيت جواب، تگدر ترسل طلب دعم من التطبيق."),
            "get_payment_methods" => Say("تگدر تدفع بالمجمع" + (data.GetProperty("online").GetArrayLength() > 0
                ? "، أو إلكترونياً: " + string.Join("، ", data.GetProperty("online").EnumerateArray().Select(x => x.GetString())) : "") + "."),
            "get_my_invoices" => Say(Items(data).Any()
                ? string.Join(" ", Items(data).Take(3).Select(i => $"فاتورة {S(i, "invoice_number")}: الباقي {i.GetProperty("due").GetInt64():N0} د.ع."))
                : "ما عندك فواتير."),
            "get_my_credit" => Say(string.Join(" ", Items(data).Select(i => $"رصيد {S(i, "patient_first_name")}: {i.GetProperty("balance").GetInt64():N0} د.ع."))),
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
            $"{S(a, "patient_first_name")}: موعد مع {S(a, "doctor_name")} يوم {S(a, "date_ar")} الساعة {S(a, "time_ar")}، رقمك {S(a, "ticket_number")}."));
    }

    private static string Prescriptions(JsonElement data)
    {
        var list = Items(data).ToList();
        if (list.Count == 0) return "ما عندك وصفات مسجلة.";
        return string.Join(" ", list.Take(3).Select(p =>
            $"وصفة {S(p, "prescription_code")} من {S(p, "doctor_name")} ({S(p, "date_ar")}): " +
            string.Join("، ", p.GetProperty("drug_names").EnumerateArray().Select(l => l.GetString())) + "."));
    }

    private static string Orders(JsonElement data)
    {
        var list = Items(data).ToList();
        if (list.Count == 0) return "ما عندك تحاليل أو أشعة مطلوبة.";
        return string.Join(" ", list.Take(5).Select(o =>
            o.TryGetProperty("opens_after_paying", out var due) && due.ValueKind == JsonValueKind.Number
                ? $"{S(o, "service_name")}: جاهزة، وتنفتح بعد دفع {due.GetInt64():N0} د.ع."
                : o.GetProperty("result_ready").GetBoolean() ? $"{S(o, "service_name")}: جاهزة، افتحها من زر النتائج." : $"{S(o, "service_name")}: {S(o, "status_ar")}."));
    }

    private static string Branches(JsonElement data)
    {
        var list = Items(data).ToList();
        if (list.Count == 0) return "ما لگيت هذا الفرع.";
        return string.Join(" ", list.Take(5).Select(b => $"{S(b, "name")}: {S(b, "address")}، {S(b, "status_ar")}."));
    }
}
