namespace AlRahma.Domain.Entities;

/// <summary>حساب تطبيق المريض (برقم الهاتف). يضم ملف المريض وأفراد عائلته.</summary>
public class PatientAccount
{
    public Guid PatientAccountId { get; set; }
    public string PhoneNumber { get; set; } = "";
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class PatientRelations
{
    public const string Self = "self";
    public static readonly string[] All = [Self, "child", "spouse", "parent", "other"];
}

public class PatientAccountLink
{
    public Guid PatientAccountId { get; set; }
    public Guid PatientId { get; set; }
    public string Relation { get; set; } = PatientRelations.Self;
}

public class OtpCode
{
    public Guid OtpId { get; set; }
    public string PhoneNumber { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public short Attempts { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class Notification
{
    public Guid NotificationId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? PatientAccountId { get; set; }
    public Guid? BranchId { get; set; }
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string? LinkType { get; set; }
    public Guid? LinkId { get; set; }
    /// <summary>low (هادئ) | normal | high (حرج).</summary>
    public string Priority { get; set; } = "normal";
    public bool IsRead { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class PushDevice
{
    public Guid PushDeviceId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? PatientAccountId { get; set; }
    public string PushToken { get; set; } = "";
    public string Platform { get; set; } = "";
    public string App { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>رسالة صادرة (SMS أو Push). في الوضع التجريبي تُسجل فقط ولا تُرسل.</summary>
public class OutboundMessage
{
    public Guid MessageId { get; set; }
    public string Channel { get; set; } = "";
    public string Recipient { get; set; } = "";
    public Guid? PatientAccountId { get; set; }
    public Guid? UserId { get; set; }
    public string Text { get; set; } = "";
    public string Status { get; set; } = "sandbox";
    public string? Error { get; set; }
    /// <summary>بيانات Push للتطبيق (JSON): النوع والرابط والصوت والأولوية.</summary>
    public string? Data { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class OnlinePaymentStatuses
{
    public const string Pending = "pending";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Refunded = "refunded";
}

public static class OnlinePaymentPurposes
{
    public const string BookingFull = "booking_full";
    public const string BookingDeposit = "booking_deposit";
    public const string UnbanDeposit = "unban_deposit";
    public const string Invoice = "invoice";
    public static readonly string[] All = [BookingFull, BookingDeposit, UnbanDeposit, Invoice];
}

public class OnlinePayment
{
    public Guid OnlinePaymentId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? PatientAccountId { get; set; }
    public string Purpose { get; set; } = "";
    public Guid? BookingId { get; set; }
    public Guid? InvoiceId { get; set; }
    public Guid? InvoiceItemId { get; set; }
    public string Provider { get; set; } = "";
    public long Amount { get; set; }
    public string Status { get; set; } = OnlinePaymentStatuses.Pending;
    public string? ProviderTransactionId { get; set; }
    public string? RedirectUrl { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? ReceiptId { get; set; }
    public string? RawCallback { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public class ReceiptBlock
{
    public Guid BlockId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid BranchId { get; set; }
    public string Prefix { get; set; } = "";
    public long RangeFrom { get; set; }
    public long RangeTo { get; set; }
    public long? LastUsed { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? ReturnedAt { get; set; }
}

public class SyncOperation
{
    public Guid OpId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? UserId { get; set; }
    public string Type { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string Status { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public string? Result { get; set; }
}

public class AgentEvent
{
    public Guid EventId { get; set; }
    public Guid DeviceId { get; set; }
    public long LocalEventId { get; set; }
    public string Type { get; set; } = "";
    public string? ReceiptNumber { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset SyncedAt { get; set; }
}

public class FastForwardOffer
{
    public Guid OfferId { get; set; }
    public Guid SessionId { get; set; }
    /// <summary>NULL في وضع الدعوة.</summary>
    public short? GapMinutes { get; set; }
    /// <summary>gap: أول من يقبل يأخذ الفجوة | invite: دعوة مفتوحة، كل من يؤكد يظهر "في الطريق".</summary>
    public string Mode { get; set; } = "gap";
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? AcceptedBookingId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class FastForwardRecipient
{
    public Guid OfferId { get; set; }
    public Guid BookingId { get; set; }
    public string? Answer { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public short? EtaMinutes { get; set; }
}

/// <summary>سجل النسخ الاحتياطي (pgBackRest). سكربت النسخ على السيرفر يكتب هنا، والـ API يقرأ ويضيف طلبات التشغيل اليدوي فقط.</summary>
public class BackupRun
{
    public Guid RunId { get; set; }
    /// <summary>full | diff | incr | verify | files</summary>
    public string Type { get; set; } = "";
    /// <summary>schedule | manual</summary>
    public string Trigger { get; set; } = "schedule";
    /// <summary>requested | running | succeeded | failed</summary>
    public string Status { get; set; } = "";
    public Guid? RequestedBy { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? BackupLabel { get; set; }
    public long? SizeBytes { get; set; }
    public long? StoredBytes { get; set; }
    public string? Repos { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? AlertedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class BackupStatuses
{
    public const string Requested = "requested";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}
