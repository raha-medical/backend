namespace AlRahma.Domain.Entities;

/// <summary>معاينة الطبيب لمريض داخل غرفته (معاينة واحدة لكل تذكرة).</summary>
public class Consultation
{
    public Guid ConsultationId { get; set; }
    public Guid TicketId { get; set; }
    public Guid PatientId { get; set; }
    public Guid DoctorId { get; set; }
    public string? Diagnosis { get; set; }
    public string? Icd10Code { get; set; }
    public string? ClinicalNotes { get; set; }
    /// <summary>للأطباء فقط: لا تظهر للمريض ولا للصيدلية ولا للمختبر.</summary>
    public string? PrivateNotes { get; set; }
    public short? FollowUpAfterDays { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class ConsultationAddendum
{
    public Guid AddendumId { get; set; }
    public Guid ConsultationId { get; set; }
    public string Text { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class PrescriptionStatuses
{
    public const string NotDispensed = "not_dispensed";
    public const string Dispensed = "dispensed";
    public const string Expired = "expired";
}

public class Prescription
{
    public Guid PrescriptionId { get; set; }
    public Guid ConsultationId { get; set; }
    public Guid PatientId { get; set; }
    public Guid DoctorId { get; set; }
    public string PrescriptionCode { get; set; } = "";
    public string Status { get; set; } = PrescriptionStatuses.NotDispensed;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DispensedAt { get; set; }
    public Guid? DispensedBy { get; set; }
    public Guid? DispensedBranchId { get; set; }

    public List<PrescriptionLine> Lines { get; set; } = [];
}

public class PrescriptionLine
{
    public Guid PrescriptionId { get; set; }
    public short LineNo { get; set; }
    public string DrugName { get; set; } = "";
    public string? Dose { get; set; }
    public string? Frequency { get; set; }
    public short? DurationDays { get; set; }
    public string? Instructions { get; set; }
    public bool? Dispensed { get; set; }
    public string? SubstituteDrugName { get; set; }
    public string? NotDispensedReason { get; set; }
}

public static class OrderStatuses
{
    public const string Ordered = "ordered";
    public const string Paid = "paid";
    public const string Deferred = "deferred";
    public const string SampleCollected = "sample_collected";
    public const string InProgress = "in_progress";
    public const string Resulted = "resulted";
    public const string Scheduled = "scheduled";
    public const string Performed = "performed";
    public const string Reported = "reported";
    public const string Released = "released";
    public const string Cancelled = "cancelled";
}

/// <summary>دورة التحويل (المرحلة 5): مستقلة عن حالة الطلب والدفع.</summary>
public static class ReferralStatuses
{
    public const string Referred = "referred";
    public const string Activated = "activated";
    public const string Completed = "completed";
    public const string Expired = "expired";
    public const string External = "external";
    public const string Cancelled = "cancelled";
}

public static class ReferralServices
{
    public const string Pharmacy = "pharmacy";
    public const string Lab = "lab";
    public const string Radiology = "radiology";
    public const string Ultrasound = "ultrasound";
}

public static class ReferralMethods
{
    public const string Scan = "scan";
    public const string StaffSearch = "staff_search";
    public const string PatientApp = "patient_app";
    public const string Cashier = "cashier";
    public const string Work = "work";
    public const string Migration = "migration";
}

/// <summary>تحويل مراجع لخدمة: طلب مختبر أو أشعة أو سونار، أو وصفة للصيدلية.</summary>
public class Referral
{
    public Guid ReferralId { get; set; }
    public Guid BranchId { get; set; }
    public Guid PatientId { get; set; }
    public string Service { get; set; } = "";
    public Guid? OrderItemId { get; set; }
    public Guid? PrescriptionId { get; set; }
    public string Status { get; set; } = ReferralStatuses.Referred;
    public string? ActivationMethod { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public Guid? ActivatedBy { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class OrderDepartments
{
    public const string Lab = "lab";
    public const string Radiology = "radiology";
}

/// <summary>طلب تحليل أو أشعة. يرتبط ببند في سلة المريض، ولا يظهر للقسم حتى يُدفع أو يُستثنى.</summary>
public class MedicalOrder
{
    public Guid OrderItemId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? ConsultationId { get; set; }
    public Guid PatientId { get; set; }
    public Guid InvoiceItemId { get; set; }
    public Guid ServiceId { get; set; }
    public string Department { get; set; } = "";
    public Guid? OrderedByDoctorId { get; set; }
    public string? ReferringDoctorName { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = OrderStatuses.Ordered;
    public string? SampleBarcode { get; set; }
    public DateTimeOffset? SampleCollectedAt { get; set; }
    public Guid? CollectedBy { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public string? Room { get; set; }
    public DateTimeOffset? PerformedAt { get; set; }
    public bool HasCritical { get; set; }
    /// <summary>علامة حرجة يضعها الفني يدوياً، إضافة إلى القيم المرجعية.</summary>
    public bool CriticalFlagged { get; set; }
    public DateTimeOffset? CriticalNotifiedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public Guid? ReleasedBy { get; set; }
    public DateTimeOffset? AmendedAt { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class LabTestParameter
{
    public Guid ParameterId { get; set; }
    public Guid ServiceId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Unit { get; set; }
    public decimal? RefLow { get; set; }
    public decimal? RefHigh { get; set; }
    public decimal? CriticalLow { get; set; }
    public decimal? CriticalHigh { get; set; }
    public short SortOrder { get; set; }
}

public class LabResult
{
    public Guid OrderItemId { get; set; }
    public string? TextResult { get; set; }
    public string? Comment { get; set; }
    public Guid EnteredBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<LabResultValue> Values { get; set; } = [];
}

public static class ResultFlags
{
    public const string Normal = "normal";
    public const string Low = "low";
    public const string High = "high";
    public const string CriticalLow = "critical_low";
    public const string CriticalHigh = "critical_high";
}

public class LabResultValue
{
    public Guid OrderItemId { get; set; }
    public Guid ParameterId { get; set; }
    public decimal Value { get; set; }
    public string Flag { get; set; } = ResultFlags.Normal;
}

public class RadiologyReport
{
    public Guid OrderItemId { get; set; }
    public string Findings { get; set; } = "";
    public string? Impression { get; set; }
    public Guid ReportedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
