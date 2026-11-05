using System.Globalization;
using AlRahma.Application.Common;
using AlRahma.Domain.Entities;

namespace AlRahma.Application.Branches;

/// <summary>فترة دوام: "09:00" إلى "14:00" بتوقيت بغداد.</summary>
public record WorkingShiftDto(string Open, string Close);
/// <summary>يوم في جدول الدوام. is_closed = لا دوام. shifts: فترة أو فترتان.</summary>
public record WorkingDayDto(string Weekday, string WeekdayAr, bool IsClosed, IReadOnlyList<WorkingShiftDto> Shifts);

public record WorkingShiftInput(string? Open, string? Close);
public record WorkingDayInput(string? Weekday, bool? IsClosed, List<WorkingShiftInput>? Shifts);

/// <summary>أوقات الدوام المهيكلة للفرع (الترحيل 010): التحقق، النص المختصر، وهل الفرع في وقت الدوام الآن.</summary>
public static class BranchHours
{
    /// <summary>ترتيب الأسبوع في العراق (يبدأ بالسبت). القيمة = weekday في القاعدة (0 = الأحد).</summary>
    public static readonly (string Code, string NameAr, short Value)[] Week =
    [
        ("sat", "السبت", 6), ("sun", "الأحد", 0), ("mon", "الاثنين", 1), ("tue", "الثلاثاء", 2),
        ("wed", "الأربعاء", 3), ("thu", "الخميس", 4), ("fri", "الجمعة", 5),
    ];

    public const int MaxShiftsPerDay = 2;

    /// <summary>يتحقق من الجدول ويحوله لصفوف. الأيام غير المذكورة مغلقة. قائمة فارغة = إلغاء الجدول المهيكل.</summary>
    public static List<BranchWorkingHour> Parse(Guid branchId, List<WorkingDayInput> days)
    {
        var errors = new Dictionary<string, string[]>();
        var rows = new List<BranchWorkingHour>();
        var seen = new HashSet<string>();
        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            var key = $"working_hours_schedule[{i}]";
            var code = d.Weekday?.Trim().ToLowerInvariant();
            var day = Week.FirstOrDefault(w => w.Code == code);
            if (day.Code is null) { errors[$"{key}.weekday"] = ["sat | sun | mon | tue | wed | thu | fri"]; continue; }
            if (!seen.Add(day.Code)) { errors[$"{key}.weekday"] = ["اليوم مكرر"]; continue; }
            var shifts = d.Shifts ?? [];
            if (d.IsClosed == true)
            {
                if (shifts.Count > 0) errors[$"{key}.shifts"] = ["اليوم المغلق بلا فترات"];
                continue;
            }
            if (shifts.Count is 0 or > MaxShiftsPerDay) { errors[$"{key}.shifts"] = ["فترة أو فترتان لليوم، أو is_closed: true"]; continue; }
            TimeOnly? prevClose = null;
            for (var j = 0; j < shifts.Count; j++)
            {
                var skey = $"{key}.shifts[{j}]";
                if (!TryTime(shifts[j].Open, out var open) || !TryTime(shifts[j].Close, out var close))
                {
                    errors[skey] = ["الوقت بصيغة HH:mm (24 ساعة)، مثل 09:00 و 21:30"];
                    continue;
                }
                if (close <= open) { errors[skey] = ["وقت الإغلاق بعد وقت الفتح في نفس اليوم (للدوام الكامل 00:00 - 23:59)"]; continue; }
                if (prevClose is { } pc && open < pc) { errors[skey] = ["الفترة الثانية تبدأ بعد نهاية الأولى"]; continue; }
                prevClose = close;
                rows.Add(new BranchWorkingHour { BranchId = branchId, Weekday = day.Value, ShiftNo = (short)(j + 1), OpensAt = open, ClosesAt = close });
            }
        }
        if (errors.Count > 0) throw AppException.Validation(errors);
        return rows;
    }

    private static bool TryTime(string? s, out TimeOnly t) =>
        TimeOnly.TryParseExact(s?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

    private static string Fmt(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>الأسبوع كاملاً (7 أيام بترتيب السبت ← الجمعة)، أو null إذا لم يُضبط جدول مهيكل.</summary>
    public static List<WorkingDayDto>? Schedule(IReadOnlyCollection<BranchWorkingHour> rows)
    {
        if (rows.Count == 0) return null;
        return Week.Select(w =>
        {
            var shifts = rows.Where(r => r.Weekday == w.Value).OrderBy(r => r.ShiftNo)
                .Select(r => new WorkingShiftDto(Fmt(r.OpensAt), Fmt(r.ClosesAt))).ToList();
            return new WorkingDayDto(w.Code, w.NameAr, shifts.Count == 0, shifts);
        }).ToList();
    }

    /// <summary>نص مختصر للعرض: "السبت - الخميس: 09:00-14:00 و 16:00-21:00، الجمعة: مغلق".</summary>
    public static string? Summary(List<WorkingDayDto>? schedule)
    {
        if (schedule is null) return null;
        static string Label(WorkingDayDto d) => d.IsClosed ? "مغلق" : string.Join(" و ", d.Shifts.Select(s => $"{s.Open}-{s.Close}"));
        var parts = new List<string>();
        var i = 0;
        while (i < schedule.Count)
        {
            var j = i;
            while (j + 1 < schedule.Count && Label(schedule[j + 1]) == Label(schedule[i])) j++;
            var days = i == j ? schedule[i].WeekdayAr : $"{schedule[i].WeekdayAr} - {schedule[j].WeekdayAr}";
            parts.Add($"{days}: {Label(schedule[i])}");
            i = j + 1;
        }
        var text = string.Join("، ", parts);
        return text.Length <= 255 ? text : text[..255];
    }

    /// <summary>فترات اليوم (بتوقيت بغداد).</summary>
    public static List<WorkingShiftDto> Today(IReadOnlyCollection<BranchWorkingHour> rows, DateOnly today) =>
        rows.Where(r => r.Weekday == (short)today.DayOfWeek).OrderBy(r => r.ShiftNo)
            .Select(r => new WorkingShiftDto(Fmt(r.OpensAt), Fmt(r.ClosesAt))).ToList();

    /// <summary>هل الوقت الحالي (بغداد) داخل إحدى فترات اليوم؟ null = لا يوجد جدول مهيكل.</summary>
    public static bool? IsWithinHours(IReadOnlyCollection<BranchWorkingHour> rows, DateTimeOffset baghdadNow)
    {
        if (rows.Count == 0) return null;
        var day = (short)baghdadNow.DayOfWeek;
        var time = TimeOnly.FromDateTime(baghdadNow.DateTime);
        return rows.Any(r => r.Weekday == day && time >= r.OpensAt && time < r.ClosesAt);
    }
}
