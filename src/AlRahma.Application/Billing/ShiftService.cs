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
public record ShiftDto(Guid ShiftId, string Status, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, long OpeningFloat, Guid? DeviceId, int ReceiptsCount,
    Guid? ClinicId = null, string? ClinicName = null, string? Department = null);
public record DeclaredInput(long? Cash, short? PosSlipsCount, long? PosTotal);
public record CloseShiftRequest(DeclaredInput? Declared, Dictionary<string, int>? CashDenominations, string? Note);
public record ReconciliationLine(string Method, long Expected, long? Declared, long? Variance);
/// <summary>cash_expenses: مصاريف صُرفت نقداً من هذا الدرج (سالبة).</summary>
public record CashBreakdown(long OpeningFloat, long CashReceived, long CashRefunds, long ExpectedInDrawer, long CashExpenses = 0, int ExpensesCount = 0);
public record ReconciliationDto(Guid ShiftId, Guid UserId, string CashierName, string Status, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt,
    long OpeningFloat, IReadOnlyList<ReconciliationLine> Lines, CashBreakdown CashBreakdown, int ReceiptsCount, int RefundsCount,
    int ReprintsCount, int? DeclaredPosSlipsCount, int PosSlipsCount, Dictionary<string, int>? CashDenominations, string? CloseNote,
    string? AuditStatus, long? VarianceTotal, string? ReviewDecision, string? ReviewNote, DateTimeOffset? ReviewedAt,
    Guid? ClinicId = null, string? ClinicName = null, string? Department = null);
public record ReviewShiftRequest(string? Decision, string? Note);
public record ShiftListItem(Guid ShiftId, Guid UserId, string CashierName, string Status, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt,
    string? AuditStatus, long? VarianceTotal, Guid? ClinicId = null, string? ClinicName = null, string? Department = null);
public record ShiftListQuery(string? Status, Guid? CashierId, DateOnly? From, DateOnly? To, int Page = 1, int PageSize = 20, Guid? ClinicId = null);

/// <summary>
/// الورديات والإغلاق الأعمى: الموظف يفتح ورديته بالفكة، ويحصّل، ثم يغلقها بإدخال ما عدّه بيده دون رؤية المتوقع.
/// النظام يحسب المتوقع والفرق لحظة الإغلاق، ولا يراه إلا المحاسب ومدير الفرع.
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
            throw new AppException(409, "SHIFT_EXISTS", "يوجد وردية بنفس المعرف");
        }
        if (await db.Shifts.AnyAsync(s => s.UserId == current.UserId && s.Status == ShiftStatuses.Open, ct))
            throw new AppException(409, "SHIFT_ALREADY_OPEN", "لديك وردية مفتوحة", "أغلق الوردية الحالية أولاً");
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
            throw new AppException(409, "SHIFT_ALREADY_OPEN", "لديك وردية مفتوحة");
        }
        return await ToDtoAsync(shift, ct);
    }

    /// <summary>الوردية المفتوحة للموظف، بلا مبالغ متوقعة (مبدأ الإغلاق الأعمى). 404 إن لم توجد.</summary>
    public async Task<ShiftDto> CurrentAsync(CancellationToken ct)
    {
        var shift = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == current.UserId && s.Status == ShiftStatuses.Open, ct)
                    ?? throw new AppException(404, "NO_OPEN_SHIFT", "لا توجد وردية مفتوحة");
        return await ToDtoAsync(shift, ct);
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
        shift.AuditStatus = variance == 0 ? "MATCHED" : variance < 0 ? "SHORTAGE" : "OVERAGE";
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
            Notifications.NotificationTypes.ShiftLatePayment, "دفعة متأخرة غيّرت نتيجة وردية",
            $"{cashier}: دفعة أوفلاين وصلت بعد الإغلاق{(reopened ? "، وأُعيدت الوردية للمراجعة" : "")}", "shift", shift.ShiftId, ct);
        await realtime.SendAsync(RealtimeGroups.BranchFinance(shift.BranchId), "shift.closed",
            new { shift_id = shift.ShiftId, user_id = shift.UserId, audit_status = shift.AuditStatus, variance_total = variance, late_payment = true });
        return true;
    }

    public async Task<ShiftDto> CloseAsync(Guid shiftId, CloseShiftRequest req, CancellationToken ct)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(s => s.ShiftId == shiftId, ct) ?? throw AppException.NotFound("الوردية غير موجودة");
        if (shift.UserId != current.UserId) throw AppException.Forbidden("يغلق الوردية صاحبها فقط");
        if (shift.Status != ShiftStatuses.Open) return await ToDtoAsync(shift, ct); // تكرار الإغلاق يعيد نفس النتيجة

        var d = req.Declared;
        new Validator()
            .Check(d?.Cash is >= 0, "declared.cash", "أدخل النقد المعدود (صفر إن لم يوجد)")
            .Check(d?.PosTotal is null or >= 0, "declared.pos_total", "قيمة غير صالحة")
            .Check(d?.PosSlipsCount is null or >= 0, "declared.pos_slips_count", "قيمة غير صالحة")
            .Check(req.CashDenominations is null || req.CashDenominations.All(x => long.TryParse(x.Key, out var v) && v > 0 && x.Value >= 0),
                "cash_denominations", "الفئة رقم موجب والعدد صفر أو أكثر")
            .Check(req.Note is null || req.Note.Length <= 1000, "note", "1000 حرف كحد أقصى")
            .ThrowIfInvalid();

        // H2: قفل سجل الوردية؛ أي تحصيل/استرجاع/مصروف جارٍ يُكمل أولاً فيُحسب، وما يأتي بعده يُرفض
        await using var tx = await db.BeginTransactionAsync(ct);
        var lockedStatus = (await db.QueryAsync<string>($"SELECT status AS \"Value\" FROM shifts WHERE shift_id = {shift.ShiftId} FOR UPDATE", ct)).Single();
        if (lockedStatus != ShiftStatuses.Open) // أُغلقت بطلب متزامن
            return await ToDtoAsync(await db.Shifts.AsNoTracking().FirstAsync(s => s.ShiftId == shift.ShiftId, ct), ct);

        var totals = await TotalsAsync(shift, ct);
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
        shift.AuditStatus = variance == 0 ? "MATCHED" : variance < 0 ? "SHORTAGE" : "OVERAGE";
        shift.Status = ShiftStatuses.ClosedPendingReview;
        shift.ClosedAt = clock.UtcNow;
        audit.Add("SHIFT_CLOSED", "shift", shift.ShiftId, after: new
        {
            shift.DeclaredCash, shift.DeclaredPosTotal, shift.ExpectedCash, shift.ExpectedPos, shift.AuditStatus, variance,
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
                shift.BranchId, Notifications.NotificationTypes.ShiftClosedWithVariance, variance < 0 ? "وردية مغلقة بعجز" : "وردية مغلقة بزيادة",
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
            new("online", 0, null, null),
        };
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
            throw new AppException(409, "SHIFT_NOT_CLOSED", "الوردية ما زالت مفتوحة");
        if (shift.Status == ShiftStatuses.Reviewed)
            throw new AppException(409, "SHIFT_ALREADY_REVIEWED", "رُوجعت الوردية مسبقاً");
        if (shift.UserId == current.UserId)
            throw AppException.Forbidden("لا يراجع الموظف ورديته بنفسه");

        shift.Status = ShiftStatuses.Reviewed;
        shift.ReviewDecision = req.Decision;
        shift.ReviewNote = req.Note?.Trim();
        shift.ReviewedBy = current.UserId;
        shift.ReviewedAt = clock.UtcNow;
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

    private async Task<Shift> LoadForReviewAsync(Guid shiftId, CancellationToken ct)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(s => s.ShiftId == shiftId, ct) ?? throw AppException.NotFound("الوردية غير موجودة");
        if (current.Role != Roles.Admin && shift.BranchId != current.BranchId) throw AppException.NotFound("الوردية غير موجودة");
        return shift;
    }

    private record Totals(long CashReceived, long CashRefunds, long PosReceived, long PosRefunds, int PosSlips, long OpeningFloat, long CashExpenses,
        int ExpensesCount)
    {
        public long ExpectedCash => OpeningFloat + CashReceived - CashRefunds - CashExpenses;
        public long ExpectedPos => PosReceived - PosRefunds;
    }

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
        return new Totals(T(TenderMethods.Cash), R(RefundMethods.Cash), T(TenderMethods.Pos), R(RefundMethods.Pos),
            tenders.FirstOrDefault(x => x.Method == TenderMethods.Pos)?.Count ?? 0, shift.OpeningFloat, expenses?.Sum ?? 0, expenses?.Count ?? 0);
    }

    private async Task<ShiftDto> ToDtoAsync(Shift s, CancellationToken ct)
    {
        var receipts = await db.Receipts.CountAsync(r => r.ShiftId == s.ShiftId, ct);
        var point = s.ClinicId is { } cid ? await db.Clinics.AsNoTracking().FirstOrDefaultAsync(c => c.ClinicId == cid, ct) : null;
        return new ShiftDto(s.ShiftId, s.Status, s.OpenedAt, s.ClosedAt, s.OpeningFloat, s.DeviceId, receipts, s.ClinicId, point?.Name, point?.Department);
    }
}
