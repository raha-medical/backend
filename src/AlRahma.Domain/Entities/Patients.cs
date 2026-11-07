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
    /// <summary>بعد إلغاء الطبيب جدوله (5.1-ب): credit | rebooked | refunded، و null = لم يختر المراجع بعد (الرصيد قائم).</summary>
    public string? CancelResolution { get; set; }
    public Guid? RebookedBookingId { get; set; }

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
    /// <summary>بانتظار المختبر / الأشعة / السونار / الصيدلية: خارج الترتيب، ويعود بالنتائج بلا فاتورة جديدة (ترحيل 018).</summary>
    public const string AwaitingResults = "awaiting_results";

    /// <summary>تذاكر ما زالت في الطابور (لم تنتهِ). awaiting_results ليست منها: خارج الترتيب.</summary>
    public static readonly string[] Active = [Waiting, Arrived, Called, InRoom, Skipped];
}

public static class TicketPriorities
{
    public const string Normal = "normal";
    public const string Emergency = "emergency";
    public const string LateReinserted = "late_reinserted";
    /// <summary>عائد بالنتائج: بعد الطوارئ وقبل المحجوزين والمباشرين.</summary>
    public const string Returning = "returning";
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
    /// <summary>من أكّد الحضور بعد الاستدعاء (الترحيل 029): patient («تم، أنا قادم» في التطبيق) | staff (زر «حضر» في الاستقبال).</summary>
    public string? CallAckBy { get; set; }
    /// <summary>الموظف الذي ضغط «حضر».</summary>
    public Guid? CallAckStaffId { get; set; }
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
    /// <summary>بداية «بانتظار قسم» (ترحيل 018): أنهى الطبيب المعاينة وللزيارة تحويلات مختبر/أشعة/سونار لم تكتمل.</summary>
    public DateTimeOffset? AwaitingSince { get; set; }
    /// <summary>عاد المراجع بالنتائج إلى الطبيب (أولوية returning) بلا فاتورة جديدة.</summary>
    public DateTimeOffset? ReturnedAt { get; set; }
    /// <summary>أُغلقت الوجبة وهو بانتظار قسم: عودته بالنتائج مجانية حتى هذا الوقت (في أي وجبة للطبيب نفسه).</summary>
    public DateTimeOffset? FreeReturnUntil { get; set; }
    /// <summary>تذكرة «عودة بالنتائج» في وجبة أخرى: التذكرة الأصلية.</summary>
    public Guid? OriginalTicketId { get; set; }
    /// <summary>الموظف الذي أكّد الحضور (الترحيل 020). NULL للتأكيد الذاتي بالـ QR.</summary>
    public Guid? CheckedInBy { get; set; }
}
