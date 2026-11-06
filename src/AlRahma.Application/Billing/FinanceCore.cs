using System.Security.Cryptography;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlRahma.Application.Billing;

public record ReceiptRefDto(Guid ReceiptId, string ReceiptNumber, string Type, DateTimeOffset IssuedAt, string QrCode, string PrintPayloadUrl);

/// <summary>أدوات مشتركة للمال: الوردية المفتوحة، الإيصالات، رصيد المريض، ونوع القسم لكل بند.</summary>
public class FinanceCore(IAppDbContext db, ICurrentUser current, IClock clock, NumberSequences numbers, IOptions<ReceiptOptions> receiptOptions)
{
    public ReceiptOptions Options => receiptOptions.Value;

    /// <summary>القسم الذي يُنفذ البند (لترتيب السلة حسب نقطة التحصيل).</summary>
    public static string DepartmentOf(string itemType) => itemType switch
    {
        "lab" => "lab",
        "radiology" => "radiology",
        "pharmacy" => "pharmacy",
        _ => "clinic",
    };

    /// <summary>
    /// الحساب اليومي المفتوح للموظف الحالي في الفرع الحالي، أو 409 NO_OPEN_SHIFT.
    /// حساب من يوم سابق (بتوقيت بغداد) لا يقبل أي تحصيل جديد: 409 SHIFT_FROM_PREVIOUS_DAY (قرار المالك، 4.2-ب).
    /// </summary>
    public async Task<Shift> RequireOpenShiftAsync(CancellationToken ct)
    {
        var shift = await OpenShiftOfAsync(current.UserId, ct)
                    ?? throw new AppException(409, "NO_OPEN_SHIFT", "لا يوجد حساب يومي مفتوح", "افتح الحساب اليومي قبل التحصيل (POST /shifts/open)");
        if (IsFromPreviousDay(shift))
            throw new AppException(409, "SHIFT_FROM_PREVIOUS_DAY", "الحساب اليومي من يوم سابق", "أغلق حساب الأمس وافتح حساب اليوم قبل التحصيل")
                { Extensions = new Dictionary<string, object?> { ["shift_id"] = shift.ShiftId, ["opened_at"] = shift.OpenedAt } };
        return shift;
    }

    /// <summary>فُتح قبل بداية اليوم الحالي بتوقيت بغداد.</summary>
    public bool IsFromPreviousDay(Shift shift) => shift.OpenedAt < StartOfToday();

    public DateTimeOffset StartOfToday() => ClockExtensions.ToUtc(clock.BaghdadToday(), TimeOnly.MinValue);

    public async Task<Shift?> OpenShiftOfAsync(Guid userId, CancellationToken ct) =>
        await db.Shifts.FirstOrDefaultAsync(s => s.UserId == userId && s.BranchId == current.BranchId && s.Status == ShiftStatuses.Open, ct);

    /// <summary>
    /// H2: داخل معاملة، يقفل سجل الوردية ويتأكد أنها ما زالت مفتوحة. الإغلاق يقفل نفس السجل،
    /// فإما تدخل العملية قبل الإغلاق وتُحسب في المطابقة، أو تُرفض بعده بـ NO_OPEN_SHIFT.
    /// </summary>
    public async Task LockOpenShiftAsync(Guid shiftId, CancellationToken ct)
    {
        var status = (await db.QueryAsync<string>($"SELECT status AS \"Value\" FROM shifts WHERE shift_id = {shiftId} FOR UPDATE", ct)).SingleOrDefault();
        if (status != ShiftStatuses.Open)
            throw new AppException(409, "NO_OPEN_SHIFT", "أُغلق الحساب اليومي أثناء العملية", "افتح حساباً يومياً جديداً وأعد المحاولة");
    }

    /// <summary>
    /// C3: وردية التحصيل الأوفلاين كما أرسلها الجهاز، حتى لو أُغلقت قبل المزامنة. يجب أن تكون في الفرع الحالي،
    /// ولنفس الجهاز (أو لنفس الموظف إن فُتحت بلا جهاز). المال قُبض فعلاً في درجها، فيُسجل فيها لا في وردية من يزامن.
    /// </summary>
    public async Task<Shift> OfflineShiftAsync(Guid shiftId, Guid deviceId, CancellationToken ct)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(s => s.ShiftId == shiftId && s.BranchId == current.BranchId, ct)
                    ?? throw new AppException(409, "SHIFT_NOT_FOUND", "الحساب اليومي للعملية الأوفلاين غير موجود في هذا الفرع");
        if (shift.DeviceId != deviceId && shift.UserId != current.UserId)
            throw new AppException(409, "SHIFT_DEVICE_MISMATCH", "الحساب اليومي ليس لهذا الجهاز", "تحصيل أوفلاين يُزامن من جهاز حسابه اليومي فقط");
        return shift;
    }

    /// <summary>قفل سجل الوردية بلا شرط أن تكون مفتوحة (الدفعة الأوفلاين المتأخرة).</summary>
    public Task LockShiftAsync(Guid shiftId, CancellationToken ct) =>
        db.ExecuteAsync($"SELECT 1 FROM shifts WHERE shift_id = {shiftId} FOR UPDATE", ct);

    /// <summary>M1: يقفل سجل المريض قبل حساب رصيده واستخدامه، فلا يُصرف نفس الرصيد من نقطتي تحصيل بنفس اللحظة.</summary>
    public Task LockPatientAsync(Guid patientId, CancellationToken ct) =>
        db.ExecuteAsync($"SELECT 1 FROM patients WHERE patient_id = {patientId} FOR UPDATE", ct);

    public async Task<long> CreditBalanceAsync(Guid patientId, CancellationToken ct) =>
        await db.PatientCredits.Where(c => c.PatientId == patientId).SumAsync(c => (long?)c.Amount, ct) ?? 0;

    /// <summary>إيصال برقم تسلسلي للفرع (الحالي أو المحدد للدفع الإلكتروني). الإلكتروني بالبادئة O.</summary>
    public async Task<Receipt> NewReceiptAsync(string type, long total, Guid? patientId, Guid? shiftId, CancellationToken ct, Guid? branchId = null)
    {
        var branch = branchId ?? current.BranchId;
        var (kind, infix) = type switch
        {
            ReceiptTypes.Refund => ("refund_receipt", "RF"),
            ReceiptTypes.Online => ("online_receipt", "O"),
            _ => ("receipt", "R"),
        };
        var seq = await numbers.NextAsync(branch, kind, "all", ct);
        var code = await numbers.BranchCodeAsync(branch, ct);
        var receipt = new Receipt
        {
            ReceiptId = UuidV7.New(),
            ReceiptNumber = $"{code}-{infix}-{seq:D6}",
            VerifyCode = NewVerifyCode(),
            BranchId = branch,
            ShiftId = shiftId,
            PatientId = patientId,
            Type = type,
            Total = total,
            IssuedBy = current.StaffUserId,
            IssuedAt = clock.UtcNow,
        };
        db.Receipts.Add(receipt);
        return receipt;
    }

    public ReceiptRefDto Ref(Receipt r) =>
        new(r.ReceiptId, r.ReceiptNumber, r.Type, r.IssuedAt, QrUrl(r.VerifyCode), $"/receipts/{r.ReceiptId}/print-payload");

    public string QrUrl(string verifyCode) => Options.VerifyBaseUrl + verifyCode;

    /// <summary>رمز تحقق غير قابل للتخمين بصيغة XXXX-XXXX-XX (بدون حروف ملتبسة).</summary>
    public static string NewVerifyCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> c = stackalloc char[10];
        for (var i = 0; i < c.Length; i++) c[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return $"{c[..4]}-{c[4..8]}-{c[8..]}";
    }

    /// <summary>يعيد حساب حالة الفاتورة والمدفوع منها بعد التحصيل أو الاسترجاع.</summary>
    public static void RefreshInvoice(Invoice invoice)
    {
        var live = invoice.Items.Where(i => i.Status != InvoiceItemStatuses.Void).ToList();
        if (live.Count == 0) { invoice.Status = InvoiceStatuses.Void; return; }
        var paidStates = new[] { InvoiceItemStatuses.Paid, InvoiceItemStatuses.PartiallyRefunded, InvoiceItemStatuses.Refunded };
        invoice.PaidAmount = live.Where(i => paidStates.Contains(i.Status)).Sum(i => i.NetPrice);
        invoice.Status = live.All(i => paidStates.Contains(i.Status)) ? InvoiceStatuses.Paid
            : live.All(i => i.Status == InvoiceItemStatuses.Deferred) ? InvoiceStatuses.Deferred
            : invoice.PaidAmount > 0 ? InvoiceStatuses.PartiallyPaid
            : InvoiceStatuses.Unpaid;
    }
}
