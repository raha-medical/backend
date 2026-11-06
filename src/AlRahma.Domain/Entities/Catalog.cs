namespace AlRahma.Domain.Entities;

public static class ServiceTypes
{
    public const string Consultation = "consultation";
    public const string Procedure = "procedure";
    public const string Lab = "lab";
    public const string Radiology = "radiology";
    public const string Pharmacy = "pharmacy";
    public static readonly string[] All = [Consultation, Procedure, Lab, Radiology, Pharmacy];
    /// <summary>خدمة نظام داخلية (الترحيل 011): رسوم إلغاء الحجز المتأخر. لا تظهر في الكتالوج ولا تُطلب يدوياً.</summary>
    public const string Fee = "fee";
}

public static class InvoiceItemTypes
{
    /// <summary>بند «رسوم إلغاء متأخر» (الترحيل 011).</summary>
    public const string CancellationFee = "cancellation_fee";
}

public static class VisitKinds
{
    public const string NewVisit = "new_visit";
    public const string FollowUp = "follow_up";
    public static readonly string[] All = [NewVisit, FollowUp];
}

public static class Modalities
{
    public static readonly string[] All = ["xray", "ultrasound", "ct", "mri"];
}

public class Service
{
    public Guid ServiceId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? ClinicId { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? VisitKind { get; set; }
    public string? Modality { get; set; }
    public long Price { get; set; }
    public bool IsCommissionable { get; set; }
    /// <summary>دواء يحتاج وصفة: لا يُصرف للمراجع الخارجي إلا بصورة الوصفة أو ملاحظة (الترحيل 022).</summary>
    public bool RequiresPrescription { get; set; }
    /// <summary>IQD | USD (الترحيل 023). سعر خدمة USD بالدولار.</summary>
    public string Currency { get; set; } = "IQD";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class DoctorCommissionRate
{
    public Guid RateId { get; set; }
    public Guid DoctorId { get; set; }
    public decimal NewVisitRate { get; set; }
    public decimal FollowUpRate { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class DoctorScheduleTemplate
{
    public Guid SessionTemplateId { get; set; }
    public Guid DoctorId { get; set; }
    public Guid BranchId { get; set; }
    /// <summary>0 = الأحد ... 6 = السبت</summary>
    public short Weekday { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public short SlotMinutes { get; set; }
    public short OnlineQuotaPercent { get; set; } = 60;
    public bool IsActive { get; set; } = true;
}

public static class SessionStatuses
{
    public const string Open = "open";
    public const string Full = "full";
    public const string Cancelled = "cancelled";
    public const string Closed = "closed";
}

public class DoctorSession
{
    public Guid SessionId { get; set; }
    public Guid DoctorId { get; set; }
    public Guid BranchId { get; set; }
    public Guid ClinicId { get; set; }
    public DateOnly SessionDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public short SlotMinutes { get; set; }
    public short CapacityTotal { get; set; }
    public short OnlineQuota { get; set; }
    public string Status { get; set; } = SessionStatuses.Open;
    public string? CancelReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>«الطبيب وصل»: الأوقات التقديرية تُحسب من وصوله إن تأخر عن بداية الوجبة.</summary>
    public DateTimeOffset? DoctorArrivedAt { get; set; }
    /// <summary>«تمديد اليوم»: النهاية الفعلية بدل EndTime (الجدول الأسبوعي لا يتغير).</summary>
    public TimeOnly? ExtendedEndTime { get; set; }
    /// <summary>«إغلاق التسجيل»: لا مراجعين جدد بدون حجز.</summary>
    public DateTimeOffset? RegistrationClosedAt { get; set; }
    public TimeOnly EffectiveEndTime => ExtendedEndTime ?? EndTime;

    /// <summary>السعة = مدة الدوام بالدقائق ÷ مدة المعاينة (القسم 3-أ من وثيقة المتطلبات).</summary>
    public static short Capacity(TimeOnly start, TimeOnly end, short slotMinutes) =>
        (short)((int)(end - start).TotalMinutes / slotMinutes);

    public static short OnlineQuotaFor(short capacity, short percent) =>
        (short)(capacity * percent / 100);
}
