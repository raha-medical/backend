using AlRahma.Application.Abstractions;

namespace AlRahma.Application.Common;

/// <summary>صيغة العرض الموحدة (1.4): بتوقيت بغداد دائماً، أشهر عربية، ووقت 12 ساعة بلاحقة ص/م.</summary>
public static class ArabicFormat
{
    private static readonly string[] Months =
    [
        "كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول",
    ];

    private static readonly IReadOnlyDictionary<string, string> Methods = new Dictionary<string, string>
    {
        ["cash"] = "نقدي", ["pos"] = "بطاقة", ["online"] = "دفع إلكتروني", ["patient_credit"] = "رصيد المريض", ["online_reversal"] = "إرجاع إلكتروني",
    };

    public static DateTimeOffset Local(DateTimeOffset t) => t.ToOffset(ClockExtensions.BaghdadOffset);

    /// <summary>تاريخ بغداد بصيغة yyyy-MM-dd.</summary>
    public static string IsoDate(DateTimeOffset t) => Local(t).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>5 تشرين الثاني 2026</summary>
    public static string Date(DateOnly d) => $"{d.Day} {Months[d.Month - 1]} {d.Year}";

    public static string Date(DateTimeOffset t) { var l = Local(t); return $"{l.Day} {Months[l.Month - 1]} {l.Year}"; }

    /// <summary>2:34 ص</summary>
    public static string Time(DateTimeOffset t)
    {
        var l = Local(t);
        var hour = l.Hour % 12 == 0 ? 12 : l.Hour % 12;
        return $"{hour}:{l.Minute:D2} {(l.Hour < 12 ? "ص" : "م")}";
    }

    public static string PaymentMethod(string method) => Methods.GetValueOrDefault(method, method);
}
