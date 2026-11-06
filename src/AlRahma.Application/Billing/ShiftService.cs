using System.Text.Json;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Billing;

/// <summary>`device_code` بديل عن `device_id` (الواجهة تعرف رمز الجهاز من إعدادها).</summary>
public record OpenShiftRequest(Guid? ShiftId, long? OpeningFloat, Guid? DeviceId, Guid? ClinicId, string? DeviceCode = null);
/// <summary>clinic_id/clinic_name/department: نقطة التحصيل (عيادة، مختبر، أشعة، صيدلية)، أو null لدرج عام.</summary>
/// <summary>summary: ما يراه صاحب الحساب اليومي (قرار المالك). auto_closed: أُغلق تلقائياً وينتظر إقراره بالمبلغ المعدود.</summary>
public record ShiftDto(Guid ShiftId, string Status, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, long OpeningFloat, Guid? DeviceId, int ReceiptsCount,
    Guid? ClinicId = null, string? ClinicName = null, string? Department = null, ShiftSummaryDto? Summary = null, bool AutoClosed = false);
public record UnprintedReceiptDto(Guid ReceiptId, string ReceiptNumber, string Type, long Total, DateTimeOffset IssuedAt, string? Reason);
/// <summary>
/// الحساب اليومي كما يراه صاحبه أثناء اليوم وعند الإغلاق:
/// cash_received النقد المستلم (وصولاته النقدية)، cash_refunds المسترجع نقداً، cash_in_hand ما في ذمته ويسلّمه
/// (الفكة + المستلم − المسترجع − المصاريف من الصندوق)، electronic_for_my_check_ins المدفوع إلكترونياً لمن أكّد حضورهم (مفصول عن النقد)،
/// unpaid_count من أكّد حضورهم ولم يدفعوا، unprinted_receipts وصولات لم تُسجَّل طباعتها.
/// </summary>
public record ShiftSummaryDto(long OpeningFloat, long CashReceived, long CashRefunds, long CashExpenses, long CashInHand,
    long ElectronicForMyCheckIns, int ElectronicCount, int RefundsCount, long RefundsTotal, int CheckedInCount, int UnpaidCount,
    IReadOnlyList<UnprintedReceiptDto> UnprintedReceipts, bool FromPreviousDay)
{
    /// <summary>نقد الدولار مفصول (7.2): المستلم والمسترجع وما في الذمة بالدولار، والفكة المعادة بالدينار (مطروحة من cash_received).</summary>
    public long UsdReceived { get; init; }
    public long UsdRefunds { get; init; }
    public long UsdInHand { get; init; }
    public long ChangeGivenIqd { get; init; }
}
/// <summary>usd: الدولارات المعدودة (مطلوب إذا قُبض دولار في الحساب).</summary>
public record DeclaredInput(long? Cash, short? PosSlipsCount, long? PosTotal, long? Usd = null);
/// <summary>unprinted_reasons: سبب لكل وصل غير مطبوع { receipt_id: "السبب" } (أو أعد طباعته قبل الإغلاق).</summary>
public record CloseShiftRequest(DeclaredInput? Declared, Dictionary<string, int>? CashDenominations, string? Note,
    Dictionary<Guid, string>? UnprintedReasons = null);
public record StaffLiabilityDto(Guid LiabilityId, Guid ShiftId, Guid UserId, string CashierName, long Amount, string Kind, string Status,
    string? Note, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt);
public record ReconciliationLine(string Method, long Expected, long? Declared, long? Variance);
/// <summary>cash_expenses: مصاريف صُرفت نقداً من هذا الدرج (سالبة).</summary>
public record CashBreakdown(long OpeningFloat, long CashReceived, long CashRefunds, long ExpectedInDrawer, long CashExpenses = 0, int ExpensesCount = 0);
public record ReconciliationDto(Guid ShiftId, Guid UserId, string CashierName, string Status, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt,
    long OpeningFloat, IReadOnlyList<ReconciliationLine> Lines, CashBreakdown CashBreakdown, int ReceiptsCount, int RefundsCount,
    int ReprintsCount, int? DeclaredPosSlipsCount, int PosSlipsCount, Dictionary<string, int>? CashDenominations, string? CloseNote,
    string? AuditStatus, long? VarianceTotal, string? ReviewDecision, string? ReviewNote, DateTimeOffset? ReviewedAt,
    Guid? ClinicId = null, string? ClinicName = null, string? Department = null,
    IReadOnlyList<UnprintedReceiptDto>? UnprintedReceipts = null, bool AutoClosed = false, long ElectronicForCheckIns = 0, int UnpaidCount = 0);
public record ReviewShiftRequest(string? Decision, string? Note);
public record ShiftListItem(Guid ShiftId, Guid UserId, string CashierName, string Status, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt,
    string? AuditStatus, long? VarianceTotal, Guid? ClinicId = null, string? ClinicName = null, string? Department = null);
public record ShiftListQuery(string? Status, Guid? CashierId, DateOnly? From, DateOnly? To, int Page = 1, int PageSize = 20, Guid? ClinicId = null);

/// <summary>
/// الحساب اليومي (كان «الوردية»، والـ API يبقى shifts). قرار المالك: الموظف يرى أثناء اليوم النقد المستلم والإلكتروني لمن أكّد حضورهم
/// والاسترجاعات وعدد غير الدافعين، ويغلق بإدخال المبلغ المعدود. الفرق يُسجَّل باسمه (ذمة الموظف) ويُرحَّل لمحاسب الفرع.
/// لا يرى حسابات غيره. الحساب من يوم سابق يُغلق تلقائياً ولا يقبل تحصيلاً جديداً.
/// </summary>
public class ShiftService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, IRealtime realtime,
    Notifications.NotificationService notifications)
{
    public async Task<ShiftDto> OpenAsync(OpenShiftRequest req, CancellationToken ct)
    {
        new Validator()
            .Check(req.OpeningFloat is null or >= 0, "opening_float", "الفكة صفر أو أكثر")
            .ThrowIfInvalid();
        var id = req.ShiftId ?? UuidV7.New();
        var existing = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.ShiftId == id, ct);
        if (existing is not null)
        {
            if (existing.UserId == current.UserId) return await ToDtoAsync(existing, ct);
            throw new AppException(409, "SHIFT_EXISTS", "يوجد حساب يومي بنفس المعرف");
        }
        if (await db.Shifts.AnyAsync(s => s.UserId == current.UserId && s.Status == ShiftStatuses.Open, ct))
            throw new AppException(409, "SHIFT_ALREADY_OPEN", "لديك حساب يومي مفتوح", "أغلق الحساب اليومي الحالي أولاً");
        if (req.DeviceId is null && !string.IsNullOrWhiteSpace(req.DeviceCode))
        {
            var code = req.DeviceCode.Trim();
            var byCode = await db.Devices.AsNoTracking().Where(d => d.DeviceCode == code && d.BranchId == current.BranchId && d.IsActive)
                .Select(d => (Guid?)d.DeviceId).FirstOrDefaultAsync(ct);
            req = req with { DeviceId = byCode ?? throw AppException.Validation(new Dictionary<string, string[]> { ["device_code"] = ["الجهاز غير موجود في هذا الفرع"] }) };
        }
        if (req.DeviceId is { } deviceId && !await db.Devices.AnyAsync(d => d.DeviceId == deviceId && d.BranchId == current.BranchId && d.IsActive, ct))
            throw AppException.Validation(new Dictionary<string, string[]> { ["device_id"] = ["الجهاز غير موجود في هذا الفرع"] });
        if (req.ClinicId is { } clinicId && !await db.Clinics.AnyAsync(c => c.ClinicId == clinicId && c.BranchId == current.BranchId && c.IsActive, ct))
            throw AppException.Validation(new Dictionary<string, string[]> { ["clinic_id"] = ["نقطة التحصيل غير موجودة في هذا الفرع"] });

        var shift = new Shift
        {
            ShiftId = id,
            BranchId = current.BranchId,
            UserId = current.UserId,
            DeviceId = req.DeviceId,
            ClinicId = req.ClinicId,
            OpeningFloat = req.OpeningFloat ?? 0,
            Status = ShiftStatuses.Open,
            OpenedAt = clock.UtcNow,
        };
        db.Shifts.Add(shift);
        audit.Add("SHIFT_OPENED", "shift", shift.ShiftId, after: new { shift.OpeningFloat, shift.DeviceId, shift.ClinicId });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new AppException(409, "SHIFT_ALREADY_OPEN", "لديك حساب يومي مفتوح");
        }
        return await ToDtoAsync(shift, ct);
    }

    /// <summary>الوردية المفتوحة للموظف، بلا مبالغ متوقعة (مبدأ الإغلاق الأعمى). 404 إن لم توجد.</summary>
    public async Task<ShiftDto> CurrentAsync(CancellationToken ct)
    {
        var shift = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == current.UserId && s.Status == ShiftStatuses.Open, ct)
                    ?? throw new AppException(404, "NO_OPEN_SHIFT", "لا يوجد حساب يومي مفتوح");
        return await ToDtoAsync(shift, ct);
    }

    /// <summary>حسابات الموظف التي أُغلقت تلقائياً ولم يقرّ بمبلغها المعدود بعد (يقرّ بـ POST /shifts/{id}/close).</summary>
    public async Task<IReadOnlyList<ShiftDto>> AwaitingAcknowledgementAsync(CancellationToken ct)
    {
        var list = await db.Shifts.AsNoTracking()
            .Where(s => s.UserId == current.UserId && s.AutoClosed && s.DeclaredCash == null && s.Status == ShiftStatuses.ClosedPendingReview)
            .OrderBy(s => s.OpenedAt).ToListAsync(ct);
        var result = new List<ShiftDto>();
        foreach (var s in list) result.Add(await ToDtoAsync(s, ct));
        return result;
    }

    /// <summary>
    /// C3: دفعة أوفلاين وصلت بعد إغلاق ورديتها. تُعاد حسابات المتوقع والفرق بنفس قاعدة الإغلاق، والوردية المراجَعة
    /// تعود «بانتظار المراجعة»، ويصل المحاسب تنبيه «دفعة متأخرة». الوردية المفتوحة لا تحتاج شيئاً.
    /// </summary>
    public async Task<bool> ApplyLatePaymentAsync(Guid shiftId, Guid paymentId, CancellationToken ct)
    {
        var shift = await db.Shifts.FirstAsync(s => s.ShiftId == shiftId, ct);
        if (shift.Status == ShiftStatuses.Open) return false;
        var before = new { shift.ExpectedCash, shift.ExpectedPos, shift.AuditStatus, shift.Status };
        var totals = await TotalsAsync(shift, ct);
        shift.ExpectedCash = totals.ExpectedCash;
        shift.ExpectedPos = totals.ExpectedPos;
        shift.VarianceCash = (shift.DeclaredCash ?? 0) - totals.ExpectedCash;
        shift.VariancePos = (shift.DeclaredPosTotal ?? 0) - totals.ExpectedPos;
        var variance = shift.VarianceCash.Value + shift.VariancePos.Value;
        shift.ExpectedUsd = totals.ExpectedUsd;
        shift.VarianceUsd = (shift.DeclaredUsd ?? 0) - totals.ExpectedUsd;
        shift.AuditStatus = AuditStatusOf(variance, shift.VarianceUsd.Value);
        var reopened = shift.Status == ShiftStatuses.Reviewed;
        if (reopened)
        {
            shift.Status = ShiftStatuses.ClosedPendingReview;
            shift.ReviewDecision = null;
        }
        audit.Add("SHIFT_LATE_PAYMENT", "shift", shift.ShiftId, before: before,
            after: new { payment_id = paymentId, shift.ExpectedCash, shift.ExpectedPos, shift.AuditStatus, shift.Status, reopened });
        await db.SaveChangesAsync(ct);
        var cashier = await db.Users.Where(u => u.UserId == shift.UserId).Select(u => u.FullName).FirstAsync(ct);
        await notifications.NotifyUsersAsync(await notifications.FinanceUsersAsync(shift.BranchId, ct), shift.BranchId,
            Notifications.NotificationTypes.ShiftLatePayment, "دفعة متأخرة غيّرت نتيجة حساب يومي",
            $"{cashier}: دفعة أوفلاين وصلت بعد الإغلاق{(reopened ? "، وأُعيد الحساب اليومي للمراجعة" : "")}", "shift", shift.ShiftId, ct);
        await realtime.SendAsync(RealtimeGroups.BranchFinance(shift.BranchId), "shift.closed",
            new { shift_id = shift.ShiftId, user_id = shift.UserId, audit_status = shift.AuditStatus, variance_total = variance, late_payment = true });
        return true;
    }

    public async Task<ShiftDto> CloseAsync(Guid shiftId, CloseShiftRequest req, CancellationToken ct)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(s => s.ShiftId == shiftId, ct) ?? throw AppException.NotFound("الحساب اليومي غير موجود");
        if (shift.UserId != current.UserId) throw AppException.Forbidden("يغلق الحساب اليومي صاحبه فقط");
        // الحساب المغلق تلقائياً بلا مبلغ معدود: صاحبه يقرّ بما عدّه بنفس الطلب
        var acknowledging = shift.AutoClosed && shift.DeclaredCash is null && shift.Status == ShiftStatuses.ClosedPendingReview;
        if (shift.Status != ShiftStatuses.Open && !acknowledging) return await ToDtoAsync(shift, ct); // تكرار الإغلاق يعيد نفس النتيجة

        var d = req.Declared;
        new Validator()
            .Check(d?.Cash is >= 0, "declared.cash", "أدخل النقد المعدود (صفر إن لم يوجد)")
            .Check(d?.PosTotal is null or >= 0, "declared.pos_total", "قيمة غير صالحة")
            .Check(d?.PosSlipsCount is null or >= 0, "declared.pos_slips_count", "قيمة غير صالحة")
            .Check(d?.Usd is null or >= 0, "declared.usd", "قيمة غير صالحة")
            .Check(req.CashDenominations is null || req.CashDenominations.All(x => long.TryParse(x.Key, out var v) && v > 0 && x.Value >= 0),
                "cash_denominations", "الفئة رقم موجب والعدد صفر أو أكثر")
            .Check(req.Note is null || req.Note.Length <= 1000, "note", "1000 حرف كحد أقصى")
            .ThrowIfInvalid();

        // H2: قفل سجل الوردية؛ أي تحصيل/استرجاع/مصروف جارٍ يُكمل أولاً فيُحسب، وما يأتي بعده يُرفض
        await using var tx = await db.BeginTransactionAsync(ct);
        var lockedStatus = (await db.QueryAsync<string>($"SELECT status AS \"Value\" FROM shifts WHERE shift_id = {shift.ShiftId} FOR UPDATE", ct)).Single();
        if (lockedStatus != ShiftStatuses.Open && !acknowledging) // أُغلق بطلب متزامن
            return await ToDtoAsync(await db.Shifts.AsNoTracking().FirstAsync(s => s.ShiftId == shift.ShiftId, ct), ct);

        // الوصل غير المطبوع لا يمنع الإغلاق، لكن يُطلب لكل واحد إعادة طباعته أو سبب، ويظهر لمحاسب الفرع
        var unprinted = await db.Receipts.Where(r => r.ShiftId == shift.ShiftId && r.PrintedAt == null && !r.IssuedOffline).ToListAsync(ct);
        string? ReasonFor(Guid id) => req.UnprintedReasons is { } m && m.TryGetValue(id, out var why) && !string.IsNullOrWhiteSpace(why) ? why.Trim() : null;
        var missing = unprinted.Where(r => ReasonFor(r.ReceiptId) is null && string.IsNullOrWhiteSpace(r.UnprintedReason)).ToList();
        if (missing.Count > 0)
            throw new AppException(422, "UNPRINTED_RECEIPTS", "وصولات غير مطبوعة", "أعد طباعة كل وصل أو اكتب سبب عدم طباعته")
            {
                Extensions = new Dictionary<string, object?>
                {
                    ["unprinted_receipts"] = missing.Select(r => new { receipt_id = r.ReceiptId, receipt_number = r.ReceiptNumber, total = r.Total }).ToList(),
                },
            };
        foreach (var r in unprinted)
            if (ReasonFor(r.ReceiptId) is { } why) r.UnprintedReason = why.Length > 500 ? why[..500] : why;

        var totals = await TotalsAsync(shift, ct);
        if (totals.ExpectedUsd != 0 && d!.Usd is null)
            throw AppException.Validation(new Dictionary<string, string[]> { ["declared.usd"] = ["قُبض دولار في هذا الحساب: أدخل الدولارات المعدودة (صفر إن لم يوجد)"] });
        shift.DeclaredUsd = d!.Usd ?? 0;
        shift.ExpectedUsd = totals.ExpectedUsd;
        shift.VarianceUsd = shift.DeclaredUsd - totals.ExpectedUsd;
        shift.DeclaredCash = d!.Cash;
        shift.DeclaredPosTotal = d.PosTotal ?? 0;
        shift.DeclaredPosSlipsCount = d.PosSlipsCount;
        shift.CashDenominations = req.CashDenominations is null ? null : JsonSerializer.Serialize(req.CashDenominations);
        shift.CloseNote = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        shift.ExpectedCash = totals.ExpectedCash;
        shift.ExpectedPos = totals.ExpectedPos;
        shift.ExpectedOnline = 0;
        shift.VarianceCash = shift.DeclaredCash - totals.ExpectedCash;
        shift.VariancePos = shift.DeclaredPosTotal - totals.ExpectedPos;
        var variance = shift.VarianceCash.Value + shift.VariancePos.Value;
        // الدينار والدولار يُطابقان كلٌّ على حدة: فرق الدولار وحده يجعل الحساب عجزاً أو زيادة أيضاً
        shift.AuditStatus = AuditStatusOf(variance, shift.VarianceUsd ?? 0);
        shift.Status = ShiftStatuses.ClosedPendingReview;
        shift.ClosedAt ??= clock.UtcNow;
        await RecordLiabilityAsync(shift, variance, ct);
        audit.Add(acknowledging ? "SHIFT_AUTO_CLOSE_ACKNOWLEDGED" : "SHIFT_CLOSED", "shift", shift.ShiftId, after: new
        {
            shift.DeclaredCash, shift.DeclaredPosTotal, shift.ExpectedCash, shift.ExpectedPos, shift.AuditStatus, variance,
            shift.DeclaredUsd, shift.ExpectedUsd, shift.VarianceUsd,
            unprinted = unprinted.Select(r => new { r.ReceiptNumber, r.UnprintedReason }),
        }, reason: shift.CloseNote);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);

        // تنبيه فوري للمحاسبين ومدير الفرع، وإشعار في الجرس عند وجود فرق
        await realtime.SendAsync(RealtimeGroups.BranchFinance(shift.BranchId), "shift.closed",
            new { shift_id = shift.ShiftId, user_id = shift.UserId, audit_status = shift.AuditStatus, variance_total = variance });
        if (variance != 0)
        {
            var cashier = await db.Users.Where(u => u.UserId == shift.UserId).Select(u => u.FullName).FirstAsync(ct);
            await notifications.NotifyUsersAsync((await notifications.FinanceUsersAsync(shift.BranchId, ct)).Where(u => u != shift.UserId),
                shift.BranchId, Notifications.NotificationTypes.ShiftClosedWithVariance, variance < 0 ? "حساب يومي مغلق بعجز" : "حساب يومي مغلق بزيادة",
                $"{cashier}: {Math.Abs(variance):N0} د.ع", "shift", shift.ShiftId, ct);
        }
        return await ToDtoAsync(shift, ct);
    }

    public async Task<ReconciliationDto> ReconciliationAsync(Guid shiftId, CancellationToken ct)
    {
        var shift = await LoadForReviewAsync(shiftId, ct);
        var totals = await TotalsAsync(shift, ct);
        var cashier = await db.Users.Where(u => u.UserId == shift.UserId).Select(u => u.FullName).FirstAsync(ct);
        var receipts = await db.Receipts.CountAsync(r => r.ShiftId == shift.ShiftId && r.Type != ReceiptTypes.Refund, ct);
        var refunds = await db.Refunds.CountAsync(r => r.ShiftId == shift.ShiftId && r.Status == RefundStatuses.Completed, ct);
        var reprints = await db.Receipts.Where(r => r.ShiftId == shift.ShiftId).SumAsync(r => (int?)r.ReprintCount, ct) ?? 0;
        var open = shift.Status == ShiftStatuses.Open;
        long? Variance(long? declared, long expected) => declared is null ? null : declared - expected;
        var lines = new List<ReconciliationLine>
        {
            new("cash", open ? totals.ExpectedCash : shift.ExpectedCash ?? totals.ExpectedCash, shift.DeclaredCash,
                open ? null : shift.VarianceCash ?? Variance(shift.DeclaredCash, totals.ExpectedCash)),
            new("pos", open ? totals.ExpectedPos : shift.ExpectedPos ?? totals.ExpectedPos, shift.DeclaredPosTotal,
                open ? null : shift.VariancePos ?? Variance(shift.DeclaredPosTotal, totals.ExpectedPos)),
            new("online", await ElectronicForCheckInsAsync(shift, ct), null, null),
        };
        // نقد الدولار سطر مستقل بالدولار (7.2)
        if (totals.UsdReceived > 0 || shift.DeclaredUsd is > 0)
            lines.Add(new("cash_usd", open ? totals.ExpectedUsd : shift.ExpectedUsd ?? totals.ExpectedUsd, shift.DeclaredUsd,
                open ? null : shift.VarianceUsd ?? Variance(shift.DeclaredUsd, totals.ExpectedUsd)));
        var denominations = shift.CashDenominations is null ? null : JsonSerializer.Deserialize<Dictionary<string, int>>(shift.CashDenominations);
        var point = shift.ClinicId is { } pid ? await db.Clinics.AsNoTracking().FirstOrDefaultAsync(c => c.ClinicId == pid, ct) : null;
        return new ReconciliationDto(shift.ShiftId, shift.UserId, cashier, shift.Status, shift.OpenedAt, shift.ClosedAt, shift.OpeningFloat, lines,
            new CashBreakdown(shift.OpeningFloat, totals.CashReceived, -totals.CashRefunds, totals.ExpectedCash, -totals.CashExpenses, totals.ExpensesCount),
            receipts, refunds, reprints, shift.DeclaredPosSlipsCount, totals.PosSlips, denominations, shift.CloseNote,
            shift.AuditStatus, open ? null : (shift.VarianceCash ?? 0) + (shift.VariancePos ?? 0),
            shift.ReviewDecision, shift.ReviewNote, shift.ReviewedAt)
        {
            ClinicId = shift.ClinicId,
            ClinicName = point?.Name,
            Department = point?.Department,
            UnprintedReceipts = await UnprintedAsync(shift, ct),
            AutoClosed = shift.AutoClosed,
            ElectronicForCheckIns = lines[2].Expected,
            UnpaidCount = await UnpaidCountAsync(shift, ct),
        };
    }

    public async Task<ReconciliationDto> ReviewAsync(Guid shiftId, ReviewShiftRequest req, CancellationToken ct)
    {
        var shift = await LoadForReviewAsync(shiftId, ct);
        new Validator()
            .Check(req.Decision is "accepted" or "escalated", "decision", "accepted | escalated")
            .Check(shift.AuditStatus == "MATCHED" || !string.IsNullOrWhiteSpace(req.Note), "note", "اكتب ملاحظة عند وجود فرق")
            .ThrowIfInvalid();
        if (shift.Status == ShiftStatuses.Open)
            throw new AppException(409, "SHIFT_NOT_CLOSED", "الحساب اليومي ما زال مفتوحاً");
        if (shift.Status == ShiftStatuses.Reviewed)
            throw new AppException(409, "SHIFT_ALREADY_REVIEWED", "رُوجع الحساب اليومي مسبقاً");
        if (shift.UserId == current.UserId)
            throw AppException.Forbidden("لا يراجع الموظف حسابه بنفسه");

        shift.Status = ShiftStatuses.Reviewed;
        shift.ReviewDecision = req.Decision;
        shift.ReviewNote = req.Note?.Trim();
        shift.ReviewedBy = current.UserId;
        shift.ReviewedAt = clock.UtcNow;
        if (await db.StaffLiabilities.FirstOrDefaultAsync(l => l.ShiftId == shift.ShiftId, ct) is { } liability)
        {
            liability.Status = req.Decision!;
            liability.Note = shift.ReviewNote;
            liability.ReviewedBy = current.UserId;
            liability.ReviewedAt = shift.ReviewedAt;
            liability.UpdatedAt = clock.UtcNow;
        }
        audit.Add("SHIFT_REVIEWED", "shift", shift.ShiftId, after: new { shift.ReviewDecision, shift.AuditStatus }, reason: shift.ReviewNote);
        await db.SaveChangesAsync(ct);
        return await ReconciliationAsync(shiftId, ct);
    }

    public async Task<PagedResult<ShiftListItem>> ListAsync(ShiftListQuery q, CancellationToken ct)
    {
        var page = new PageQuery(q.Page, q.PageSize);
        var query = db.Shifts.AsNoTracking().Where(s => s.BranchId == current.BranchId);
        if (!string.IsNullOrWhiteSpace(q.Status)) query = query.Where(s => s.Status == q.Status);
        if (q.CashierId is { } uid) query = query.Where(s => s.UserId == uid);
        if (q.ClinicId is { } cid) query = query.Where(s => s.ClinicId == cid);
        if (q.From is { } from) { var f = ClockExtensions.ToUtc(from, TimeOnly.MinValue); query = query.Where(s => s.OpenedAt >= f); }
        if (q.To is { } to) { var t = ClockExtensions.ToUtc(to.AddDays(1), TimeOnly.MinValue); query = query.Where(s => s.OpenedAt < t); }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(s => s.OpenedAt).Skip((page.SafePage - 1) * page.SafePageSize).Take(page.SafePageSize)
            .Join(db.Users, s => s.UserId, u => u.UserId, (s, u) => new { s, u.FullName }).ToListAsync(ct);
        var pointIds = rows.Where(r => r.s.ClinicId != null).Select(r => r.s.ClinicId!.Value).Distinct().ToList();
        var points = await db.Clinics.AsNoTracking().Where(c => pointIds.Contains(c.ClinicId)).ToDictionaryAsync(c => c.ClinicId, ct);
        Clinic? Point(Guid? id) => id is { } x && points.TryGetValue(x, out var c) ? c : null;
        return new PagedResult<ShiftListItem>(rows.Select(r => new ShiftListItem(r.s.ShiftId, r.s.UserId, r.FullName, r.s.Status, r.s.OpenedAt,
            r.s.ClosedAt, r.s.AuditStatus, r.s.Status == ShiftStatuses.Open ? null : (r.s.VarianceCash ?? 0) + (r.s.VariancePos ?? 0),
            r.s.ClinicId, Point(r.s.ClinicId)?.Name, Point(r.s.ClinicId)?.Department)).ToList(),
            page.SafePage, page.SafePageSize, total);
    }

    /// <summary>
    /// نهاية اليوم: كل حساب مفتوح من يوم سابق (بتوقيت بغداد) يُغلق تلقائياً «بانتظار إقرار الموظف»، ويُحسب متوقعه،
    /// ويُبلَّغ المحاسبون وصاحبه. صاحبه يقرّ بالمبلغ المعدود بنفس POST /shifts/{id}/close.
    /// </summary>
    public async Task<int> AutoClosePreviousDayAsync(CancellationToken ct)
    {
        var start = ClockExtensions.ToUtc(clock.BaghdadToday(), TimeOnly.MinValue);
        var ids = await db.Shifts.Where(s => s.Status == ShiftStatuses.Open && s.OpenedAt < start).Select(s => s.ShiftId).ToListAsync(ct);
        var closed = 0;
        foreach (var id in ids)
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var status = (await db.QueryAsync<string>($"SELECT status AS \"Value\" FROM shifts WHERE shift_id = {id} FOR UPDATE", ct)).Single();
            if (status != ShiftStatuses.Open) continue;
            var shift = await db.Shifts.FirstAsync(s => s.ShiftId == id, ct);
            var totals = await TotalsAsync(shift, ct);
            shift.ExpectedCash = totals.ExpectedCash;
            shift.ExpectedPos = totals.ExpectedPos;
            shift.ExpectedUsd = totals.ExpectedUsd;
            shift.ExpectedOnline = 0;
            shift.Status = ShiftStatuses.ClosedPendingReview;
            shift.ClosedAt = clock.UtcNow;
            shift.AutoClosed = true;
            audit.Add("SHIFT_AUTO_CLOSED", "shift", shift.ShiftId, after: new { shift.ExpectedCash, shift.ExpectedPos, shift.OpenedAt });
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            closed++;
            var cashier = await db.Users.Where(u => u.UserId == shift.UserId).Select(u => u.FullName).FirstAsync(ct);
            await notifications.NotifyUsersAsync((await notifications.FinanceUsersAsync(shift.BranchId, ct)).Append(shift.UserId).Distinct(), shift.BranchId,
                Notifications.NotificationTypes.ShiftClosedWithVariance, "حساب يومي أُغلق تلقائياً",
                $"{cashier}: لم يُغلق حساب يوم سابق. بانتظار إقرار الموظف بالمبلغ المعدود", "shift", shift.ShiftId, ct);
        }
        return closed;
    }

    /// <summary>ذمة الموظف للمحاسب: العجز والزيادة بأسماء الموظفين في الفرع الحالي.</summary>
    public async Task<PagedResult<StaffLiabilityDto>> LiabilitiesAsync(Guid? userId, string? status, int page, int pageSize, CancellationToken ct)
    {
        var q = new PageQuery(page, pageSize);
        var query = db.StaffLiabilities.AsNoTracking().Where(l => l.BranchId == current.BranchId);
        if (userId is { } uid) query = query.Where(l => l.UserId == uid);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(l => l.Status == status);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(l => l.CreatedAt).Skip((q.SafePage - 1) * q.SafePageSize).Take(q.SafePageSize)
            .Join(db.Users, l => l.UserId, u => u.UserId, (l, u) => new StaffLiabilityDto(l.LiabilityId, l.ShiftId, l.UserId, u.FullName, l.Amount, l.Kind,
                l.Status, l.Note, l.CreatedAt, l.ReviewedAt)).ToListAsync(ct);
        return new PagedResult<StaffLiabilityDto>(rows, q.SafePage, q.SafePageSize, total);
    }

    /// <summary>الفرق عند الإغلاق يُسجَّل باسم الموظف (يُحدَّث إن أُعيد حسابه)، ويُحذف إن صار صفراً.</summary>
    private async Task RecordLiabilityAsync(Shift shift, long variance, CancellationToken ct)
    {
        var existing = await db.StaffLiabilities.FirstOrDefaultAsync(l => l.ShiftId == shift.ShiftId, ct);
        if (variance == 0)
        {
            if (existing is not null) db.StaffLiabilities.Remove(existing);
            return;
        }
        var now = clock.UtcNow;
        if (existing is null)
        {
            existing = new StaffLiability { LiabilityId = UuidV7.New(), ShiftId = shift.ShiftId, BranchId = shift.BranchId, UserId = shift.UserId, CreatedAt = now };
            db.StaffLiabilities.Add(existing);
        }
        existing.Amount = variance;
        existing.Kind = variance < 0 ? "shortage" : "overage";
        existing.Status = "pending";
        existing.UpdatedAt = now;
    }

    private async Task<List<Guid>> MyCheckInTicketsAsync(Shift s, CancellationToken ct)
    {
        var until = s.ClosedAt ?? DateTimeOffset.MaxValue;
        return await db.QueueTickets.AsNoTracking()
            .Where(t => t.CheckedInBy == s.UserId && t.BranchId == s.BranchId && t.ArrivedAt >= s.OpenedAt && t.ArrivedAt <= until)
            .Select(t => t.TicketId).ToListAsync(ct);
    }

    private async Task<long> ElectronicForCheckInsAsync(Shift s, CancellationToken ct)
    {
        var tickets = await MyCheckInTicketsAsync(s, ct);
        if (tickets.Count == 0) return 0;
        return await (from a in db.PaymentAllocations
                      join p in db.Payments on a.PaymentId equals p.PaymentId
                      join i in db.InvoiceItems on a.InvoiceItemId equals i.InvoiceItemId
                      where p.OnlinePaymentId != null && i.TicketId != null && tickets.Contains(i.TicketId.Value)
                      select (long?)a.Amount).SumAsync(ct) ?? 0;
    }

    private async Task<int> UnpaidCountAsync(Shift s, CancellationToken ct)
    {
        var tickets = await MyCheckInTicketsAsync(s, ct);
        if (tickets.Count == 0) return 0;
        return await db.InvoiceItems.AsNoTracking()
            .Where(i => i.TicketId != null && tickets.Contains(i.TicketId.Value) && i.Status == InvoiceItemStatuses.Unpaid)
            .Select(i => i.TicketId).Distinct().CountAsync(ct);
    }

    private async Task<List<UnprintedReceiptDto>> UnprintedAsync(Shift s, CancellationToken ct) =>
        await db.Receipts.AsNoTracking().Where(r => r.ShiftId == s.ShiftId && r.PrintedAt == null && !r.IssuedOffline)
            .OrderBy(r => r.IssuedAt)
            .Select(r => new UnprintedReceiptDto(r.ReceiptId, r.ReceiptNumber, r.Type, r.Total, r.IssuedAt, r.UnprintedReason)).ToListAsync(ct);

    private async Task<ShiftSummaryDto> SummaryAsync(Shift s, CancellationToken ct)
    {
        var totals = await TotalsAsync(s, ct);
        var refunds = await db.Refunds.Where(r => r.ShiftId == s.ShiftId && r.Status == RefundStatuses.Completed)
            .GroupBy(r => 1).Select(g => new { Count = g.Count(), Sum = g.Sum(r => r.Amount) }).FirstOrDefaultAsync(ct);
        var tickets = await MyCheckInTicketsAsync(s, ct);
        var electronic = tickets.Count == 0 ? new { Sum = 0L, Count = 0 } : await (
                from a in db.PaymentAllocations
                join p in db.Payments on a.PaymentId equals p.PaymentId
                join i in db.InvoiceItems on a.InvoiceItemId equals i.InvoiceItemId
                where p.OnlinePaymentId != null && i.TicketId != null && tickets.Contains(i.TicketId.Value)
                group new { a.Amount, p.PaymentId } by 1 into g
                select new { Sum = g.Sum(x => x.Amount), Count = g.Select(x => x.PaymentId).Distinct().Count() })
            .FirstOrDefaultAsync(ct) ?? new { Sum = 0L, Count = 0 };
        return new ShiftSummaryDto(s.OpeningFloat, totals.CashReceived, totals.CashRefunds, totals.CashExpenses, totals.ExpectedCash,
            electronic.Sum, electronic.Count, refunds?.Count ?? 0, refunds?.Sum ?? 0, tickets.Count, await UnpaidCountAsync(s, ct),
            await UnprintedAsync(s, ct), s.OpenedAt < ClockExtensions.ToUtc(clock.BaghdadToday(), TimeOnly.MinValue))
        {
            UsdReceived = totals.UsdReceived, UsdRefunds = totals.UsdRefunds, UsdInHand = totals.ExpectedUsd, ChangeGivenIqd = totals.ChangeIqd,
        };
    }

    private async Task<Shift> LoadForReviewAsync(Guid shiftId, CancellationToken ct)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(s => s.ShiftId == shiftId, ct) ?? throw AppException.NotFound("الحساب اليومي غير موجود");
        if (current.Role != Roles.Admin && shift.BranchId != current.BranchId) throw AppException.NotFound("الحساب اليومي غير موجود");
        return shift;
    }

    private record Totals(long CashReceived, long CashRefunds, long PosReceived, long PosRefunds, int PosSlips, long OpeningFloat, long CashExpenses,
        int ExpensesCount, long UsdReceived = 0, long UsdRefunds = 0, long ChangeIqd = 0)
    {
        public long ExpectedCash => OpeningFloat + CashReceived - CashRefunds - CashExpenses;
        public long ExpectedPos => PosReceived - PosRefunds;
        /// <summary>الدولارات التي يجب أن تكون في الدرج.</summary>
        public long ExpectedUsd => UsdReceived - UsdRefunds;
    }

    private static string AuditStatusOf(long iqdVariance, long usdVariance) =>
        iqdVariance == 0 && usdVariance == 0 ? "MATCHED"
        : iqdVariance < 0 || (iqdVariance == 0 && usdVariance < 0) ? "SHORTAGE" : "OVERAGE";

    /// <summary>ما يجب أن يكون في الدرج: الفكة + النقد المقبوض − النقد المسترجع − المصاريف من الدرج. وفي POS: المقبوض − المسترجع.</summary>
    private async Task<Totals> TotalsAsync(Shift shift, CancellationToken ct)
    {
        var tenders = await db.Payments.Where(p => p.ShiftId == shift.ShiftId).SelectMany(p => p.Tenders)
            .GroupBy(t => t.Method).Select(g => new { Method = g.Key, Sum = g.Sum(t => t.Amount), Count = g.Count() }).ToListAsync(ct);
        var refunds = await db.Refunds.Where(r => r.ShiftId == shift.ShiftId && r.Status == RefundStatuses.Completed)
            .GroupBy(r => r.Method).Select(g => new { Method = g.Key, Sum = g.Sum(r => r.Amount) }).ToListAsync(ct);
        long T(string m) => tenders.FirstOrDefault(x => x.Method == m)?.Sum ?? 0;
        long R(string m) => refunds.FirstOrDefault(x => x.Method == m)?.Sum ?? 0;
        var expenses = await db.Expenses.Where(x => x.ShiftId == shift.ShiftId && x.Status == ExpenseStatuses.Recorded)
            .GroupBy(x => 1).Select(g => new { Sum = g.Sum(x => x.Amount), Count = g.Count() }).FirstOrDefaultAsync(ct);
        // 7.2: نقد الدولار مفصول عن الدينار، والفكة المعادة بالدينار تُطرح من نقد الدينار المستلم
        var change = await db.Payments.Where(p => p.ShiftId == shift.ShiftId).SumAsync(p => (long?)p.ChangeIqd, ct) ?? 0;
        var usdIn = await db.Payments.Where(p => p.ShiftId == shift.ShiftId).SelectMany(p => p.Tenders)
            .Where(t => t.Method == TenderMethods.CashUsd).SumAsync(t => (long?)t.OriginalAmount, ct) ?? 0;
        var usdOut = await db.Refunds.Where(r => r.ShiftId == shift.ShiftId && r.Status == RefundStatuses.Completed && r.Method == RefundMethods.CashUsd)
            .SumAsync(r => (long?)r.OriginalAmount, ct) ?? 0;
        return new Totals(T(TenderMethods.Cash) - change, R(RefundMethods.Cash), T(TenderMethods.Pos), R(RefundMethods.Pos),
            tenders.FirstOrDefault(x => x.Method == TenderMethods.Pos)?.Count ?? 0, shift.OpeningFloat, expenses?.Sum ?? 0, expenses?.Count ?? 0,
            usdIn, usdOut, change);
    }

    private async Task<ShiftDto> ToDtoAsync(Shift s, CancellationToken ct)
    {
        var receipts = await db.Receipts.CountAsync(r => r.ShiftId == s.ShiftId, ct);
        var point = s.ClinicId is { } cid ? await db.Clinics.AsNoTracking().FirstOrDefaultAsync(c => c.ClinicId == cid, ct) : null;
        // الملخص لصاحب الحساب فقط (ولا يرى حسابات غيره)
        var summary = s.UserId == current.UserId ? await SummaryAsync(s, ct) : null;
        return new ShiftDto(s.ShiftId, s.Status, s.OpenedAt, s.ClosedAt, s.OpeningFloat, s.DeviceId, receipts, s.ClinicId, point?.Name, point?.Department,
            summary, s.AutoClosed);
    }
}
