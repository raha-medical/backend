namespace AlRahma.Domain.Entities;

/// <summary>نظام أجر موظف في فرع: percentage | fixed | per_case (الترحيل 015).</summary>
public class StaffPayPlan
{
    public Guid PlanId { get; set; }
    public Guid BranchId { get; set; }
    public Guid UserId { get; set; }
    public string Model { get; set; } = "";
    public decimal? Percent { get; set; }
    public long? FixedMonthly { get; set; }
    public long? PerCaseAmount { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>عملية صرف مستحقات. تُسجل ولا تُعدل.</summary>
public class Payout
{
    public Guid PayoutId { get; set; }
    public Guid BranchId { get; set; }
    public Guid UserId { get; set; }
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    public long Amount { get; set; }
    public string Method { get; set; } = "";
    public string? Note { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class InvoiceStatuses
{
    public const string Unpaid = "unpaid";
    public const string PartiallyPaid = "partially_paid";
    public const string Paid = "paid";
    public const string Deferred = "deferred";
    public const string Void = "void";
}

public class Invoice
{
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public Guid BranchId { get; set; }
    public Guid PatientId { get; set; }
    public string Status { get; set; } = InvoiceStatuses.Unpaid;
    public long GrossAmount { get; set; }
    public long DiscountAmount { get; set; }
    public long NetAmount { get; set; }
    public long PaidAmount { get; set; }
    public string? DiscountReason { get; set; }
    public Guid? DiscountBy { get; set; }
    public string? VoidReason { get; set; }
    public Guid? VoidedBy { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<InvoiceItem> Items { get; set; } = [];
}

public static class InvoiceItemStatuses
{
    public const string Unpaid = "unpaid";
    public const string Paid = "paid";
    public const string Deferred = "deferred";
    public const string PartiallyRefunded = "partially_refunded";
    public const string Refunded = "refunded";
    public const string Void = "void";
}

public class InvoiceItem
{
    public Guid InvoiceItemId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid? DoctorId { get; set; }
    public Guid? TicketId { get; set; }
    public string ItemType { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public short Quantity { get; set; } = 1;
    public long UnitPrice { get; set; }
    public long Discount { get; set; }
    public long NetPrice { get; set; }
    public decimal CommissionRate { get; set; }
    public long DoctorShare { get; set; }
    public long ComplexShare { get; set; }
    public long RefundedAmount { get; set; }
    public string Status { get; set; } = InvoiceItemStatuses.Unpaid;
    public DateTimeOffset? PerformedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
