namespace AlRahma.Domain.Entities;

public class Branch
{
    public Guid BranchId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? City { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    /// <summary>رقم واتساب الفرع (الترحيل 021).</summary>
    public string? Whatsapp { get; set; }
    /// <summary>موقع الفرع على الخريطة (لتطبيق المريض).</summary>
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    /// <summary>نص أوقات الدوام للعرض، مثل "السبت - الخميس، 9 صباحاً - 9 مساءً".</summary>
    public string? WorkingHours { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    public List<Clinic> Clinics { get; set; } = [];
    /// <summary>أوقات الدوام المهيكلة (الترحيل 010): فترة أو فترتان لكل يوم، واليوم بلا صفوف مغلق.</summary>
    public List<BranchWorkingHour> Hours { get; set; } = [];
}

/// <summary>فترة دوام للفرع في يوم من الأسبوع. weekday: 0 = الأحد ... 6 = السبت (مثل جداول الأطباء). الأوقات بتوقيت بغداد.</summary>
public class BranchWorkingHour
{
    public Guid BranchId { get; set; }
    public short Weekday { get; set; }
    /// <summary>1 أو 2 (فترة صباحية ومسائية).</summary>
    public short ShiftNo { get; set; }
    public TimeOnly OpensAt { get; set; }
    public TimeOnly ClosesAt { get; set; }
}

/// <summary>
/// إغلاق مؤقت للفرع كاملاً (صيانة، عطلة، افتتاح قريب...). يغلق كل عيادات وأقسام الفرع في الفترة،
/// ويختلف عن الإيقاف الدائم (is_active = false).
/// </summary>
public class BranchClosure
{
    public Guid ClosureId { get; set; }
    public Guid BranchId { get; set; }
    public string Reason { get; set; } = "other";
    public string? Note { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public Guid? EndedBy { get; set; }

    public bool Covers(DateOnly date) => EndedAt is null && StartsOn <= date && (EndsOn is null || EndsOn >= date);
}

public class Clinic
{
    public Guid ClinicId { get; set; }
    public Guid BranchId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>clinic | lab | radiology | pharmacy</summary>
    public string Department { get; set; } = "clinic";
    public short? Floor { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>مدة العودة المجانية بالنتائج لأطباء العيادة؛ null = إعداد الفرع queue.free_return_days (ترحيل 018).</summary>
    public short? FreeReturnDays { get; set; }

    public Branch? Branch { get; set; }
}

public class SystemSetting
{
    public Guid SettingId { get; set; }
    public Guid? BranchId { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "null";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>إغلاق مؤقت لعيادة أو قسم (سفر، صيانة، عطلة...). EndsOn = null حتى إعادة الفتح يدوياً.</summary>
public class ClinicClosure
{
    public Guid ClosureId { get; set; }
    public Guid ClinicId { get; set; }
    public string Reason { get; set; } = "other";
    public string? Note { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public Guid? EndedBy { get; set; }

    public bool Covers(DateOnly date) => EndedAt is null && StartsOn <= date && (EndsOn is null || EndsOn >= date);
}

/// <summary>
/// قوائم الاختيار التي يديرها المدير (أسباب الإغلاق، الاختصاصات، العناوين الوظيفية...).
/// تُضاف القيم وتُعدّل من لوحة الإدارة دون تعديل الكود.
/// </summary>
public class LookupValue
{
    public Guid LookupId { get; set; }
    public string Category { get; set; } = "";
    public string Code { get; set; } = "";
    public string NameAr { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>قيمة يعتمد عليها النظام (مثل سبب "أخرى"): يمكن تعديل اسمها لا إيقافها.</summary>
    public bool IsSystem { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class LookupCategories
{
    public const string ClosureReason = "closure_reason";
    public const string DoctorSpecialty = "doctor_specialty";
    public const string DoctorJobTitle = "doctor_job_title";
    public const string ExpenseCategory = "expense_category";

    public static readonly IReadOnlyDictionary<string, string> Arabic = new Dictionary<string, string>
    {
        [ClosureReason] = "أسباب الإغلاق المؤقت",
        [DoctorSpecialty] = "اختصاصات الأطباء",
        [DoctorJobTitle] = "العناوين الوظيفية للأطباء",
        [ExpenseCategory] = "تصنيفات المصاريف",
    };

    /// <summary>سبب الإغلاق الذي يتطلب كتابة ملاحظة.</summary>
    public const string OtherReason = "other";
}
