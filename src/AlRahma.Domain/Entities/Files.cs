namespace AlRahma.Domain.Entities;

/// <summary>ملف في MinIO. يُخزن المفتاح فقط، والروابط تُولد مؤقتة عند الطلب.</summary>
public class StoredFile
{
    public Guid FileId { get; set; }
    public Guid BranchId { get; set; }
    public string Bucket { get; set; } = "";
    public string ObjectKey { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Purpose { get; set; } = "";
    public Guid? OrderItemId { get; set; }
    /// <summary>صورة النتيجة الخارجية: الدور والمريض (الترحيل 021).</summary>
    public Guid? TicketId { get; set; }
    public Guid? PatientId { get; set; }
    public string Status { get; set; } = FileStatuses.Pending;
    public DateTimeOffset? S3SyncedAt { get; set; }
    public Guid? UploadedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class FileStatuses
{
    public const string Pending = "pending";
    public const string Stored = "stored";
}

public static class FilePurposes
{
    public const string Lab = "lab";
    public const string Radiology = "radiology";
    public const string DoctorPhoto = "doctor_photo";
    /// <summary>صورة نتيجة فحص أُجري خارج المجمع، يرفعها الاستقبال على الدور ويراها الطبيب (1.2-ج3).</summary>
    public const string ExternalResult = "external_result";
    /// <summary>صورة وصفة خارجية للصيدلية (6.3-ب).</summary>
    public const string Prescription = "prescription";
}
