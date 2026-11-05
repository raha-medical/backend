using System.Globalization;
using System.Text.Json;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Branches;

/// <summary>
/// value: القيمة الفعالة. source: branch (قيمة خاصة بالفرع) | global (الإعداد العام) | default (القيمة الافتراضية في النظام).
/// </summary>
public record SettingDto(string Key, string LabelAr, string Unit, long Value, string Source, long GlobalValue, long Min, long Max)
{
    /// <summary>لإعدادات الاختيار فقط (مثل queue.call_next_by): القيمة النصية الفعالة. value يبقى رقم الخيار (1..عدد الخيارات).</summary>
    public string? ValueText { get; init; }
    public string? GlobalValueText { get; init; }
    /// <summary>لإعدادات الاختيار فقط: الخيارات المتاحة مع شرحها بالعربي.</summary>
    public IReadOnlyList<SettingOptionDto>? Options { get; init; }
}
public record SettingOptionDto(string Value, string LabelAr);
/// <summary>القيم أرقام، وإعدادات الاختيار تقبل النص (مثل "doctor") أو رقم الخيار. null في إعدادات الفرع يحذف التخصيص.</summary>
public record UpdateSettingsRequest(Dictionary<string, JsonElement?>? Values);

/// <summary>
/// إعدادات التشغيل القابلة للتعديل: عامة لكل الفروع، ويمكن لكل فرع تخصيص قيمته (تتقدم على العامة).
/// إرسال null لمفتاح في إعدادات الفرع يحذف التخصيص ويعود الفرع للقيمة العامة.
/// </summary>
public class BranchSettingsService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, BranchService branches)
{
    private record Definition(string Key, string LabelAr, string Unit, long Default, long Min, long Max, SettingOptionDto[]? Options = null);

    private static readonly Definition[] Definitions =
    [
        new(SettingsReader.BanNoShowLimit, "عدد مرات الغياب قبل الحظر", "مرة", 3, 1, 20),
        new(SettingsReader.BanWindowDays, "فترة احتساب الغياب", "يوم", 90, 7, 365),
        new(SettingsReader.UnbanDepositAmount, "مبلغ التأمين لفك الحظر", "دينار", 10000, 0, 1_000_000),
        new(SettingsReader.CancelMinHours, "أقل مدة لإلغاء الحجز قبل موعده (الإلغاء المجاني؛ بعدها يُعد إلغاءً متأخراً)", "ساعة", 2, 0, 72),
        new(SettingsReader.LateCancelFee, "رسوم الإلغاء المتأخر (0 = بلا رسوم)", "دينار", 0, 0, 1_000_000),
        new(SettingsReader.LateCancelCountsNoShow, "الإلغاء المتأخر يُحتسب غياباً (1 نعم، 0 لا)", "", 1, 0, 1),
        new(SettingsReader.MaxReschedules, "أقصى عدد مرات تغيير موعد الحجز الواحد (0 = غير متاح)", "مرة", 2, 0, 10),
        new(SettingsReader.RescheduleMinHours, "أقل مدة قبل الموعد لتغييره", "ساعة", 2, 0, 72),
        new(SettingsReader.CancelPaidTo, "المبلغ المدفوع إلكترونياً عند إلغاء الحجز", "", 1, 1, 2,
            [
                new(SettingsReader.CancelPaidToValues.Credit, "يتحول رصيداً للمريض يُستخدم في زيارته القادمة"),
                new(SettingsReader.CancelPaidToValues.Refund, "يُرجع إلى وسيلة الدفع الإلكتروني"),
            ]),
        new(SettingsReader.SelfCheckInEnabled, "تثبيت الحضور الذاتي بمسح رمز QR شاشة الانتظار (1 مفعل، 0 موقوف)", "", 1, 0, 1),
        new(SettingsReader.CheckInQrScope, "نطاق رمز QR الشاشة: حجوزات عيادة الشاشة فقط، أو أي عيادة في الفرع", "", 1, 1, 2,
            [
                new(SettingsReader.CheckInQrScopeValues.Clinic, "حجوزات عيادة الشاشة فقط"),
                new(SettingsReader.CheckInQrScopeValues.Branch, "أي عيادة في الفرع"),
            ]),
        new(SettingsReader.FollowUpWindowDays, "فترة المراجعة بعد الكشف", "يوم", 14, 0, 90),
        new("refund.cashier_limit", "حد الاسترجاع المسموح لأمين الصندوق", "دينار", 50000, 0, 10_000_000),
        new("prescription.valid_days", "مدة صلاحية الوصفة", "يوم", 30, 1, 365),
        new(SettingsReader.ReferralExpiryDays, "إغلاق التحويل غير المفعّل تلقائياً بعد", "يوم", 7, 1, 90),
        new(SettingsReader.ShowDeferredResults, "نتيجة البند المؤجل تظهر للمريض قبل السداد (1 نعم، 0 بعد السداد فقط)", "", 0, 0, 1),
        new("consultation.edit_window_hours", "مدة تعديل المعاينة بعد إنهائها", "ساعة", 24, 0, 720),
        new(SettingsReader.ReminderMinutes, "تذكير المريض قبل موعده", "دقيقة", 60, 10, 1440),
        new(SettingsReader.ReminderSoonMinutes, "تذكير «موعدك قريب» قبل الموعد المتوقع", "دقيقة", 20, 5, 120),
        new(SettingsReader.DayBeforeHour, "ساعة تذكير «موعدك غداً» في اليوم السابق (0 = إيقاف)", "ساعة", 18, 0, 23),
        new(SettingsReader.MaxOpenBookings, "أقصى عدد حجوزات قائمة للمريض الواحد (0 = بلا حد)", "حجز", 4, 0, 20),
        new(SettingsReader.ArriveEarlyMinutes, "حضور المريض قبل موعده", "دقيقة", 20, 0, 120),
        new(SettingsReader.TurnSoonAhead, "إشعار اقتراب الدور عند بقاء عدد مرضى قبله", "مريض", 3, 1, 20),
        new(SettingsReader.AutoCallNext, "استدعاء المريض التالي تلقائياً عند إنهاء المعاينة (1 نعم، 0 لا)", "", 0, 0, 1),
        new(SettingsReader.CallNextBy,
            "من يستدعي المريض التالي: الطبيب فقط، أو موظف الاستقبال فقط، أو الاثنان (مدير الفرع مسموح له دائماً، والاستدعاء التلقائي يعمل مستقلاً)",
            "", 3, 1, 3,
            [
                new(SettingsReader.CallNextByValues.Doctor, "الطبيب فقط"),
                new(SettingsReader.CallNextByValues.Reception, "موظف الاستقبال فقط"),
                new(SettingsReader.CallNextByValues.Both, "الطبيب وموظف الاستقبال"),
            ]),
        new(SettingsReader.NoShowGraceMinutes, "مهلة المحجوز المتأخر قبل أن يُعامل كمتأخر (يمكن تخصيصها لكل طبيب)", "دقيقة", 20, 0, 180),
        new(SettingsReader.AutoCloseAfterMinutes, "إغلاق التسجيل تلقائياً بعد نهاية الوجبة الفعلية", "دقيقة", 120, 0, 600),
        new(SettingsReader.ExtraMaxPercent, "أقصى عدد إضافي فوق السعة (نسبة من سعة الوجبة، 0 = بلا حد)", "%", 50, 0, 300),
        new(SettingsReader.CallAlertRepeats, "عدد مرات تكرار تنبيه \"تفضل للدخول\" (كل دقيقة) إذا لم يضغط المريض \"أنا قادم\"", "مرة", 3, 0, 10),
    ];

    public async Task<IReadOnlyList<SettingDto>> GetAsync(Guid? branchId, CancellationToken ct)
    {
        if (branchId is { } bid) await EnsureCanReadAsync(bid, ct);
        else EnsureAdmin();
        var keys = Definitions.Select(d => d.Key).ToList();
        var rows = await db.SystemSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key) && (s.BranchId == null || s.BranchId == branchId)).ToListAsync(ct);
        return Definitions.Select(d =>
        {
            var global = Parse(d, rows.FirstOrDefault(r => r.Key == d.Key && r.BranchId == null)?.Value);
            var own = branchId is null ? null : Parse(d, rows.FirstOrDefault(r => r.Key == d.Key && r.BranchId == branchId)?.Value);
            var globalValue = global ?? d.Default;
            var (value, source) = own is { } o ? (o, "branch") : global is { } g ? (g, "global") : (d.Default, "default");
            var dto = new SettingDto(d.Key, d.LabelAr, d.Unit, value, source, globalValue, d.Min, d.Max);
            return d.Options is { } opts
                ? dto with { ValueText = opts[value - 1].Value, GlobalValueText = opts[globalValue - 1].Value, Options = opts }
                : dto;
        }).ToList();
    }

    /// <summary>branchId = null للإعدادات العامة (المدير العام فقط).</summary>
    public async Task<IReadOnlyList<SettingDto>> UpdateAsync(Guid? branchId, UpdateSettingsRequest req, CancellationToken ct)
    {
        if (branchId is { } bid) await branches.EnsureCanManageBranchAsync(bid, ct, Permissions.SettingsEdit);
        else EnsureAdmin();

        var values = req.Values ?? [];
        var errors = new Dictionary<string, string[]>();
        if (values.Count == 0) errors["values"] = ["أرسل إعداداً واحداً على الأقل"];
        var parsed = new Dictionary<string, long?>();
        foreach (var (key, raw) in values)
        {
            var def = Definitions.FirstOrDefault(d => d.Key == key);
            var isNull = raw is null || raw.Value.ValueKind == JsonValueKind.Null;
            long? value = isNull ? null : ParseInput(def, raw!.Value);
            if (def is null) errors[$"values.{key}"] = ["إعداد غير معروف"];
            else if (isNull && branchId is null) errors[$"values.{key}"] = ["القيمة العامة مطلوبة"];
            else if (!isNull && value is null)
                errors[$"values.{key}"] = [def.Options is { } o ? $"القيم المسموحة: {string.Join("، ", o.Select(x => x.Value))}" : "يجب أن تكون رقماً"];
            else if (value is { } v && (v < def.Min || v > def.Max)) errors[$"values.{key}"] = [$"بين {def.Min} و {def.Max}"];
            parsed[key] = value;
        }
        if (errors.Count > 0) throw AppException.Validation(errors);

        var keys = values.Keys.ToList();
        var rows = await db.SystemSettings.Where(s => keys.Contains(s.Key) && s.BranchId == branchId).ToListAsync(ct);
        var before = rows.ToDictionary(r => r.Key, r => r.Value);
        var now = clock.UtcNow;
        foreach (var (key, value) in parsed)
        {
            var def = Definitions.First(d => d.Key == key);
            var row = rows.FirstOrDefault(r => r.Key == key);
            if (value is null)
            {
                if (row is not null) db.SystemSettings.Remove(row);
                continue;
            }
            var json = def.Options is { } options
                ? JsonSerializer.Serialize(options[value.Value - 1].Value)
                : value.Value.ToString(CultureInfo.InvariantCulture);
            if (row is null)
                db.SystemSettings.Add(new SystemSetting { SettingId = UuidV7.New(), BranchId = branchId, Key = key, Value = json, UpdatedAt = now });
            else
            {
                row.Value = json;
                row.UpdatedAt = now;
            }
        }
        audit.Add("SETTINGS_UPDATED", branchId is null ? "settings" : "branch", branchId, before, parsed);
        await db.SaveChangesAsync(ct);
        return await GetAsync(branchId, ct);
    }

    private static long? Parse(Definition d, string? json)
    {
        if (json is null) return null;
        var text = json.Trim('"', ' ');
        if (d.Options is { } opts)
        {
            var i = Array.FindIndex(opts, o => o.Value == text);
            return i >= 0 ? i + 1 : null;
        }
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static long? ParseInput(Definition? d, JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Number) return raw.TryGetInt64(out var n) ? n : null;
        if (raw.ValueKind == JsonValueKind.String && d?.Options is { } opts)
        {
            var i = Array.FindIndex(opts, o => o.Value == raw.GetString());
            return i >= 0 ? i + 1 : null;
        }
        return null;
    }

    private void EnsureAdmin()
    {
        if (current.Role != Roles.Admin) throw AppException.Forbidden("الإعدادات العامة للمدير العام فقط");
    }

    private async Task EnsureCanReadAsync(Guid branchId, CancellationToken ct)
    {
        if (current.Role == Roles.Admin)
        {
            if (!await db.Branches.AnyAsync(b => b.BranchId == branchId, ct)) throw AppException.NotFound("الفرع غير موجود");
            return;
        }
        if (branchId != current.BranchId) throw AppException.NotFound("الفرع غير موجود");
    }
}
