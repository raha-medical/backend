namespace AlRahma.Domain.Entities;

public static class ShiftStatuses
{
    public const string Open = "open";
    public const string ClosedPendingReview = "closed_pending_review";
    public const string Reviewed = "reviewed";
}

/// <summary>وردية موظف التحصيل على درج واحد. الإغلاق أعمى: الموظف لا يرى المتوقع.</summary>
public class Shift
{
    public Guid ShiftId { get; set; }
    public Guid BranchId { get; set; }
    public Guid UserId { get; set; }
    public Guid? DeviceId { get; set; }
    /// <summary>نقطة التحصيل: العيادة أو المختبر أو الأشعة أو الصيدلية التي يعمل عليها هذا الدرج.</summary>
    public Guid? ClinicId { get; set; }
    public long OpeningFloat { get; set; }
    public string Status { get; set; } = ShiftStatuses.Open;
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public long? DeclaredCash { get; set; }
    public long? DeclaredPosTotal { get; set; }
    public short? DeclaredPosSlipsCount { get; set; }
    public string? CashDenominations { get; set; }
    public string? CloseNote { get; set; }
    public long? ExpectedCash { get; set; }
    public long? ExpectedPos { get; set; }
    public long? ExpectedOnline { get; set; }
    public long? VarianceCash { get; set; }
    public long? VariancePos { get; set; }
    public string? AuditStatus { get; set; }
    public Guid? ReviewedBy { get; set; }
    public string? ReviewDecision { get; set; }
    public string? ReviewNote { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? CreatedOfflineAt { get; set; }
}

public static class ReceiptTypes
{
    public const string Payment = "payment";
    public const string Refund = "refund";
    public const string Deposit = "deposit";
    public const string Online = "online";
}

public class Receipt
{
    public Guid ReceiptId { get; set; }
    public string ReceiptNumber { get; set; } = "";
    public string VerifyCode { get; set; } = "";
    public Guid BranchId { get; set; }
    public Guid? ShiftId { get; set; }
    public Guid? PatientId { get; set; }
    public string Type { get; set; } = ReceiptTypes.Payment;
    public long Total { get; set; }
    public string Status { get; set; } = "valid";
    public bool IssuedOffline { get; set; }
    public Guid? BlockId { get; set; }
    public short ReprintCount { get; set; }
    public Guid? IssuedBy { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
}

public class Payment
{
    public Guid PaymentId { get; set; }
    public Guid BranchId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? ShiftId { get; set; }
    public Guid? OnlinePaymentId { get; set; }
    public Guid ReceiptId { get; set; }
    public long TotalPaid { get; set; }
    public long DepositUsed { get; set; }
    public bool PriceMismatch { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? CreatedOfflineAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<PaymentTender> Tenders { get; set; } = [];
    public List<PaymentAllocation> Allocations { get; set; } = [];
}

public static class TenderMethods
{
    public const string Cash = "cash";
    public const string Pos = "pos";
    public const string Online = "online";
}

public class PaymentTender
{
    public Guid TenderId { get; set; }
    public Guid PaymentId { get; set; }
    public string Method { get; set; } = TenderMethods.Cash;
    public long Amount { get; set; }
    public string? PosTerminal { get; set; }
    public string? PosReference { get; set; }
}

public class PaymentAllocation
{
    public Guid PaymentId { get; set; }
    public Guid InvoiceItemId { get; set; }
    public long Amount { get; set; }
}

public static class RefundStatuses
{
    public const string PendingApproval = "pending_approval";
    public const string Completed = "completed";
    public const string Rejected = "rejected";
}

public static class RefundMethods
{
    public const string Cash = "cash";
    public const string Pos = "pos";
    public const string OnlineReversal = "online_reversal";
    public const string PatientCredit = "patient_credit";
    public static readonly string[] All = [Cash, Pos, OnlineReversal, PatientCredit];
}

public class Refund
{
    public Guid RefundId { get; set; }
    public Guid InvoiceItemId { get; set; }
    public long Amount { get; set; }
    public string Method { get; set; } = RefundMethods.Cash;
    public string Reason { get; set; } = "";
    public string Status { get; set; } = RefundStatuses.PendingApproval;
    public Guid RequestedBy { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? RejectReason { get; set; }
    public Guid? ShiftId { get; set; }
    public Guid? ReceiptId { get; set; }
    public long DoctorShareAdjustment { get; set; }
    public long ComplexShareAdjustment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class DeferralStatuses
{
    public const string Active = "active";
    public const string Settled = "settled";
    public const string WrittenOff = "written_off";
}

/// <summary>استثناء ودفع آجل: بنود تُنفذ قبل الدفع وتبقى ديناً على المريض حتى تُسدد أو تُعفى.</summary>
public class Deferral
{
    public Guid DeferralId { get; set; }
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public string Reason { get; set; } = "";
    public string? Note { get; set; }
    public long Amount { get; set; }
    public DateOnly? DueDate { get; set; }
    public string Status { get; set; } = DeferralStatuses.Active;
    public Guid GrantedBy { get; set; }
    public Guid? WrittenOffBy { get; set; }
    public string? WriteOffReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }

    public List<DeferralItem> Items { get; set; } = [];
}

public class DeferralItem
{
    public Guid DeferralId { get; set; }
    public Guid InvoiceItemId { get; set; }
}

public static class ExpensePaidFrom
{
    /// <summary>نقد من درج الوردية المفتوحة: ينقص من المتوقع في الدرج.</summary>
    public const string ShiftCash = "shift_cash";
    /// <summary>من صندوق الفرع أو حساب البنك أو الإدارة: لا يمس أدراج الورديات.</summary>
    public const string Treasury = "treasury";
    public static readonly string[] All = [ShiftCash, Treasury];
}

public static class ExpenseStatuses
{
    public const string Recorded = "recorded";
    public const string Void = "void";
}

/// <summary>مصروف فرع (رواتب، إيجار، صيانة، مستلزمات...). التصنيف من قائمة expense_category.</summary>
public class Expense
{
    public Guid ExpenseId { get; set; }
    public string ExpenseNumber { get; set; } = "";
    public Guid BranchId { get; set; }
    public string Category { get; set; } = "";
    public long Amount { get; set; }
    public DateOnly ExpenseDate { get; set; }
    public string Description { get; set; } = "";
    public string? Payee { get; set; }
    public string PaidFrom { get; set; } = ExpensePaidFrom.Treasury;
    public Guid? ShiftId { get; set; }
    public string Status { get; set; } = ExpenseStatuses.Recorded;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? VoidedBy { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
}
