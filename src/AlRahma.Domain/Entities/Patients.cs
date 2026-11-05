namespace AlRahma.Domain.Entities;

public class Patient
{
    public Guid PatientId { get; set; }
    public string FileNumber { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public DateOnly? BirthDate { get; set; }
    public short? ApproxAge { get; set; }
    public string? Gender { get; set; }
    public Guid? HomeBranchId { get; set; }
    public short NoShowCount { get; set; }
    public bool IsBanned { get; set; }
    public DateTimeOffset? BannedAt { get; set; }
    public string? BanReason { get; set; }
    public Guid? MergedIntoPatientId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? CreatedOfflineAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class PatientNoShow
{
    public Guid NoShowId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? TicketId { get; set; }
    public Guid? BookingId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public class PatientBanEvent
{
    public Guid EventId { get; set; }
    public Guid PatientId { get; set; }
    public string Action { get; set; } = "";
    public string? Method { get; set; }
    public long Amount { get; set; }
    public string? Note { get; set; }
    public Guid? ReceiptId { get; set; }
    public Guid? PerformedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class PatientCreditEntry
{
    public Guid EntryId { get; set; }
    public Guid PatientId { get; set; }
    /// <summary>الفرع الذي نشأت فيه الحركة: أين قُبض المبلغ (إضافة) أو أين استُخدم (خصم). للتسويات بين الفروع.</summary>
    public Guid BranchId { get; set; }
    public long Amount { get; set; }
    public string Kind { get; set; } = "";
    public Guid? PaymentId { get; set; }
    public Guid? RefundId { get; set; }
    public Guid? BookingId { get; set; }
    public Guid? OnlinePaymentId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class BookingStatuses
{
    public const string Booked = "booked";
    public const string CheckedIn = "checked_in";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string NoShow = "no_show";
}

public static class PaymentStatuses
{
    public const string Unpaid = "unpaid";
    public const string DepositPaid = "deposit_paid";
    public const string Paid = "paid";
    public const string Credited = "credited";
    public const string Refunded = "refunded";
}

public static class CheckInMethods
{
    public const string Reception = "reception";
    /// <summary>المريض مسح رمز QR الدوار على شاشة الانتظار من تطبيقه.</summary>
    public const string SelfQr = "self_qr";
}

public class Booking
{
    public Guid BookingId { get; set; }
    public Guid PatientId { get; set; }
    public Guid SessionId { get; set; }
    public short SequenceNumber { get; set; }
    public string VisitKind { get; set; } = "";
    public long Price { get; set; }
    /// <summary>online | phone</summary>
    public string Source { get; set; } = "";
    public string Status { get; set; } = BookingStatuses.Booked;
    public string PaymentStatus { get; set; } = PaymentStatuses.Unpaid;
    public DateTimeOffset? EstimatedTime { get; set; }
    public Guid? BookedByUserId { get; set; }
    public Guid? BookedByAccountId { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public bool CountedAsNoShow { get; set; }
    /// <summary>وقت إرسال تذكير الموعد (مرة واحدة لكل حجز).</summary>
    public DateTimeOffset? ReminderSentAt { get; set; }
    public DateTimeOffset? DayBeforeSentAt { get; set; }
    public DateTimeOffset? SoonSentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // ------------------------------------------------------------------ الترحيل 011 (المرحلة 3)
    /// <summary>عدد مرات تغيير الموعد (يُقارن بـ booking.max_reschedules).</summary>
    public short RescheduleCount { get; set; }
    public DateTimeOffset? LastRescheduledAt { get; set; }
    /// <summary>الوجبة الأصلية قبل أول تغيير للموعد.</summary>
    public Guid? OriginalSessionId { get; set; }
    public DateTimeOffset? CheckedInAt { get; set; }
    /// <summary>reception | self_qr</summary>
    public string? CheckInMethod { get; set; }
    /// <summary>patient | staff | system</summary>
    public string? CancelledBy { get; set; }
    /// <summary>بند «رسوم إلغاء متأخر» إن وُجد.</summary>
    public Guid? CancelFeeItemId { get; set; }
    public bool CancelFeeWaived { get; set; }

    public Patient? Patient { get; set; }
    public DoctorSession? Session { get; set; }
}

public static class TicketStatuses
{
    public const string Waiting = "waiting";
    public const string Arrived = "arrived";
    public const string Called = "called";
    public const string InRoom = "in_room";
    public const string Completed = "completed";
    public const string Skipped = "skipped";
    public const string NoShow = "no_show";
    public const string Cancelled = "cancelled";

    /// <summary>تذاكر ما زالت في الطابور (لم تنتهِ).</summary>
    public static readonly string[] Active = [Waiting, Arrived, Called, InRoom, Skipped];
}

public static class TicketPriorities
{
    public const string Normal = "normal";
    public const string Emergency = "emergency";
    public const string LateReinserted = "late_reinserted";
}

public class QueueTicket
{
    public Guid TicketId { get; set; }
    public Guid BranchId { get; set; }
    public Guid SessionId { get; set; }
    public Guid DoctorId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? BookingId { get; set; }
    public string TicketNumber { get; set; } = "";
    public short SequenceNumber { get; set; }
    public short? QueuePosition { get; set; }
    /// <summary>online | walk_in | phone</summary>
    public string Source { get; set; } = "";
    public string Priority { get; set; } = TicketPriorities.Normal;
    public string Status { get; set; } = TicketStatuses.Waiting;
    public string? EmergencyReason { get; set; }
    public DateTimeOffset? EstimatedTime { get; set; }
    public DateTimeOffset? ArrivedAt { get; set; }
    public DateTimeOffset? CalledAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? CreatedOfflineAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>وقت إشعار "اقترب دورك" (مرة واحدة لكل تذكرة).</summary>
    public DateTimeOffset? TurnSoonNotifiedAt { get; set; }
    /// <summary>ضغط المريض "أنا قادم" على تنبيه الاستدعاء. يُصفّر عند إعادة الاستدعاء.</summary>
    public DateTimeOffset? CallAcknowledgedAt { get; set; }
    /// <summary>عدد تنبيهات الاستدعاء المرسلة (الأول + التكرار).</summary>
    public short CallAlertsSent { get; set; }
    public DateTimeOffset? LastCallAlertAt { get; set; }
    /// <summary>المريض أكد أنه في الطريق (دعوة الحضور المبكر) ووقت وصوله التقريبي بالدقائق.</summary>
    public DateTimeOffset? OnTheWayAt { get; set; }
    public short? OnTheWayEtaMinutes { get; set; }

    public Patient? Patient { get; set; }
    public DoctorSession? Session { get; set; }
    /// <summary>مراجع فوق سعة الوجبة بموافقة الاستقبال (ترحيل 007).</summary>
    public bool IsExtra { get; set; }
    public Guid? ExtraApprovedBy { get; set; }
}
