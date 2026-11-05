using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Queue;
using AlRahma.Application.Reports;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Billing;

public record CreateDeferralRequest(Guid? DeferralId, Guid? PatientId, List<Guid>? InvoiceItemIds, string? Reason, string? Note, DateOnly? DueDate);
public record WriteOffRequest(string? Reason);
public record DeferralItemDto(Guid InvoiceItemId, string ServiceName, string Department, long Price, string Status);
public record DeferralDto(Guid DeferralId, Guid PatientId, string PatientName, string? PhoneNumber, Guid BranchId, string BranchName, string Reason,
    string? Note, long Amount, long Paid, long Remaining, DateOnly? DueDate, bool IsOverdue, string Status, Guid GrantedById, string GrantedBy,
    DateTimeOffset CreatedAt, DateTimeOffset? SettledAt, string? WriteOffReason, IReadOnlyList<DeferralItemDto>? Items = null);
public record DeferralListResult(IReadOnlyList<DeferralDto> Items, int Page, int PageSize, int Total, long TotalOutstanding, int OverdueCount);
public record DeferralListQuery(string? Status, Guid? PatientId, Guid? BranchId, bool? Overdue, int Page = 1, int PageSize = 20);

/// <summary>
/// الاستثناء والدفع الآجل (القسم 14): بنود تُنفذ قبل الدفع للحالات الطارئة أو الإنسانية، وتبقى ديناً على المريض.
/// التسديد بـ POST /payments على نفس البنود، والآجل يصبح settled تلقائياً عند تسديد كل بنوده.
/// الإعفاء (write-off) للمدير العام: البنود المتبقية لا تُحصّل بعده.
/// </summary>
public class DeferralService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, ReportScope scope, QueueNotifier notifier,
    Clinical.MedicalOrderSync orders)
{
    public static readonly string[] Reasons = ["emergency", "humanitarian", "other"];
    private static readonly string[] PaidStates = [InvoiceItemStatuses.Paid, InvoiceItemStatuses.PartiallyRefunded, InvoiceItemStatuses.Refunded];

    public async Task<DeferralDto> CreateAsync(CreateDeferralRequest req, CancellationToken ct)
    {
        var ids = (req.InvoiceItemIds ?? []).Distinct().ToList();
        var today = clock.BaghdadToday();
        new Validator()
            .Check(req.PatientId is not null, "patient_id", "المريض مطلوب")
            .Check(ids.Count > 0, "invoice_item_ids", "اختر بنداً واحداً على الأقل")
            .Check(req.Reason is not null && Reasons.Contains(req.Reason), "reason", string.Join(" | ", Reasons))
            .Check(req.Reason != "other" || !string.IsNullOrWhiteSpace(req.Note), "note", "اكتب سبب الاستثناء")
            .Check(req.DueDate is null || req.DueDate >= today, "due_date", "تاريخ السداد اليوم أو بعده")
            .ThrowIfInvalid();
        var id = req.DeferralId ?? UuidV7.New();
        if (await db.Deferrals.AnyAsync(d => d.DeferralId == id, ct)) throw new AppException(409, "DEFERRAL_EXISTS", "يوجد استثناء بنفس المعرف");
        var patient = await db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.PatientId == req.PatientId, ct)
                      ?? throw new AppException(404, "PATIENT_NOT_FOUND", "المريض غير موجود");

        await using var tx = await db.BeginTransactionAsync(ct);
        var idArray = ids.ToArray();
        await db.ExecuteAsync($"SELECT 1 FROM invoice_items WHERE invoice_item_id = ANY({idArray}) FOR UPDATE", ct);
        var invoices = await db.Invoices.Include(i => i.Items)
            .Where(i => i.PatientId == patient.PatientId && i.BranchId == current.BranchId && i.Items.Any(x => ids.Contains(x.InvoiceItemId)))
            .ToListAsync(ct);
        var items = invoices.SelectMany(i => i.Items).Where(x => ids.Contains(x.InvoiceItemId)).ToList();
        var missing = ids.Except(items.Select(i => i.InvoiceItemId)).ToList();
        if (missing.Count > 0)
            throw new AppException(404, "INVOICE_ITEM_NOT_FOUND", "بند غير موجود في سلة هذا المريض")
                { Extensions = new Dictionary<string, object?> { ["invoice_item_ids"] = missing } };
        var notUnpaid = items.Where(i => i.Status != InvoiceItemStatuses.Unpaid).Select(i => i.InvoiceItemId).ToList();
        if (notUnpaid.Count > 0)
            throw new AppException(409, "ITEM_NOT_PAYABLE", "بند مدفوع أو مؤجل أو ملغى مسبقاً", "الاستثناء للبنود غير المدفوعة فقط")
                { Extensions = new Dictionary<string, object?> { ["invoice_item_ids"] = notUnpaid } };

        var deferral = new Deferral
        {
            DeferralId = id,
            PatientId = patient.PatientId,
            BranchId = current.BranchId,
            Reason = req.Reason!,
            Note = req.Note?.Trim(),
            Amount = items.Sum(i => i.NetPrice),
            DueDate = req.DueDate,
            GrantedBy = current.UserId,
            CreatedAt = clock.UtcNow,
        };
        if (deferral.Amount <= 0) throw AppException.Validation(new Dictionary<string, string[]> { ["invoice_item_ids"] = ["البنود بدون مبلغ"] });
        deferral.Items.AddRange(items.Select(i => new DeferralItem { DeferralId = id, InvoiceItemId = i.InvoiceItemId }));
        db.Deferrals.Add(deferral);
        foreach (var i in items) i.Status = InvoiceItemStatuses.Deferred;
        foreach (var inv in invoices) FinanceCore.RefreshInvoice(inv);
        audit.Add("DEFERRAL_GRANTED", "deferral", id, after: new
        {
            patient_id = patient.PatientId, deferral.Amount, deferral.Reason, due_date = deferral.DueDate,
            items = items.Select(i => new { i.InvoiceItemId, i.ServiceName, i.NetPrice }),
        }, reason: deferral.Note);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        await orders.SyncAsync(idArray, ct, inPerson: true); // التحليل أو الأشعة المستثناة تدخل قائمة عمل القسم

        // الكشفية المؤجلة تفتح باب الدخول للطبيب مثل المدفوعة
        var ticketIds = items.Where(i => i.TicketId != null).Select(i => i.TicketId!.Value).ToList();
        if (ticketIds.Count > 0)
            foreach (var s in await db.QueueTickets.Where(t => ticketIds.Contains(t.TicketId)).Select(t => t.SessionId).Distinct().ToListAsync(ct))
                await notifier.SessionChangedAsync(s, ct);
        return await GetAsync(id, ct);
    }

    public async Task<DeferralDto> GetAsync(Guid deferralId, CancellationToken ct)
    {
        var d = await db.Deferrals.AsNoTracking().FirstOrDefaultAsync(x => x.DeferralId == deferralId, ct)
                ?? throw new AppException(404, "DEFERRAL_NOT_FOUND", "الاستثناء غير موجود");
        await EnsureVisibleAsync(d.BranchId, ct);
        var items = await (
            from di in db.DeferralItems.AsNoTracking()
            join i in db.InvoiceItems on di.InvoiceItemId equals i.InvoiceItemId
            where di.DeferralId == deferralId
            orderby i.CreatedAt
            select new DeferralItemDto(i.InvoiceItemId, i.ServiceName, i.ItemType, i.NetPrice, i.Status)).ToListAsync(ct);
        items = items.Select(i => i with { Department = FinanceCore.DepartmentOf(i.Department) }).ToList();
        return (await ToDtosAsync([d], ct)).Single() with { Items = items };
    }

    public async Task<DeferralListResult> ListAsync(DeferralListQuery q, CancellationToken ct)
    {
        new Validator()
            .Check(q.Status is null or DeferralStatuses.Active or DeferralStatuses.Settled or DeferralStatuses.WrittenOff, "status", "active | settled | written_off")
            .ThrowIfInvalid();
        var page = new PageQuery(q.Page, q.PageSize);
        var branches = (await scope.BranchesAsync(q.BranchId, ct)).ToList();
        var today = clock.BaghdadToday();
        var query = db.Deferrals.AsNoTracking().Where(d => branches.Contains(d.BranchId));
        if (q.Status is not null) query = query.Where(d => d.Status == q.Status);
        if (q.PatientId is { } pid) query = query.Where(d => d.PatientId == pid);
        if (q.Overdue == true) query = query.Where(d => d.Status == DeferralStatuses.Active && d.DueDate != null && d.DueDate < today);

        var total = await query.CountAsync(ct);
        var activeIds = query.Where(d => d.Status == DeferralStatuses.Active).Select(d => d.DeferralId);
        var outstanding = await (
            from di in db.DeferralItems
            join i in db.InvoiceItems on di.InvoiceItemId equals i.InvoiceItemId
            where activeIds.Contains(di.DeferralId) && i.Status == InvoiceItemStatuses.Deferred
            select (long?)i.NetPrice).SumAsync(ct) ?? 0;
        var overdue = await query.CountAsync(d => d.Status == DeferralStatuses.Active && d.DueDate != null && d.DueDate < today, ct);
        var rows = await query.OrderByDescending(d => d.CreatedAt).Skip((page.SafePage - 1) * page.SafePageSize).Take(page.SafePageSize).ToListAsync(ct);
        return new DeferralListResult(await ToDtosAsync(rows, ct), page.SafePage, page.SafePageSize, total, outstanding, overdue);
    }

    public async Task<DeferralDto> WriteOffAsync(Guid deferralId, WriteOffRequest req, CancellationToken ct)
    {
        if (current.Role != Roles.Admin) throw AppException.Forbidden("الإعفاء النهائي للمدير العام فقط");
        new Validator().Check(!string.IsNullOrWhiteSpace(req.Reason), "reason", "سبب الإعفاء مطلوب").ThrowIfInvalid();
        var d = await db.Deferrals.FirstOrDefaultAsync(x => x.DeferralId == deferralId, ct)
                ?? throw new AppException(404, "DEFERRAL_NOT_FOUND", "الاستثناء غير موجود");
        if (d.Status != DeferralStatuses.Active)
            throw new AppException(409, "DEFERRAL_NOT_ACTIVE", "الاستثناء مسدد أو معفى مسبقاً");
        var remaining = await RemainingAsync(deferralId, ct);
        d.Status = DeferralStatuses.WrittenOff;
        d.WrittenOffBy = current.UserId;
        d.WriteOffReason = req.Reason!.Trim();
        d.SettledAt = clock.UtcNow;
        audit.Add("DEFERRAL_WRITTEN_OFF", "deferral", d.DeferralId, before: new { status = DeferralStatuses.Active },
            after: new { d.Status, written_off_amount = remaining }, reason: d.WriteOffReason);
        await db.SaveChangesAsync(ct);
        return await GetAsync(deferralId, ct);
    }

    /// <summary>بعد تحصيل بنود: كل آجل سُددت كل بنوده يصبح settled. يُستدعى داخل معاملة التحصيل بعد الحفظ.</summary>
    public static Task<int> SettlePaidAsync(IAppDbContext db, Guid[] itemIds, DateTimeOffset now, CancellationToken ct) =>
        db.ExecuteAsync($"""
            UPDATE deferrals d SET status = 'settled', settled_at = {now}
             WHERE d.status = 'active'
               AND d.deferral_id IN (SELECT deferral_id FROM deferral_items WHERE invoice_item_id = ANY({itemIds}))
               AND NOT EXISTS (SELECT 1 FROM deferral_items di JOIN invoice_items i ON i.invoice_item_id = di.invoice_item_id
                                WHERE di.deferral_id = d.deferral_id AND i.status IN ('unpaid', 'deferred'))
            """, ct);

    /// <summary>بنود من آجل معفى: لا تُحصّل.</summary>
    public static IQueryable<Guid> WrittenOffItemIds(IAppDbContext db) =>
        from di in db.DeferralItems
        join d in db.Deferrals on di.DeferralId equals d.DeferralId
        where d.Status == DeferralStatuses.WrittenOff
        select di.InvoiceItemId;

    private async Task<long> RemainingAsync(Guid deferralId, CancellationToken ct) =>
        await (from di in db.DeferralItems
               join i in db.InvoiceItems on di.InvoiceItemId equals i.InvoiceItemId
               where di.DeferralId == deferralId && i.Status == InvoiceItemStatuses.Deferred
               select (long?)i.NetPrice).SumAsync(ct) ?? 0;

    private async Task EnsureVisibleAsync(Guid branchId, CancellationToken ct)
    {
        if (current.Role == Roles.Admin || branchId == current.BranchId) return;
        if (!await db.UserBranches.AnyAsync(ub => ub.UserId == current.UserId && ub.BranchId == branchId, ct))
            throw new AppException(404, "DEFERRAL_NOT_FOUND", "الاستثناء غير موجود");
    }

    private async Task<List<DeferralDto>> ToDtosAsync(List<Deferral> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.DeferralId).ToList();
        var itemStates = await (
            from di in db.DeferralItems.AsNoTracking()
            join i in db.InvoiceItems on di.InvoiceItemId equals i.InvoiceItemId
            where ids.Contains(di.DeferralId)
            select new { di.DeferralId, i.Status, i.NetPrice }).ToListAsync(ct);
        var patientIds = rows.Select(r => r.PatientId).Distinct().ToList();
        var patients = await db.Patients.AsNoTracking().Where(p => patientIds.Contains(p.PatientId))
            .Select(p => new { p.PatientId, p.FullName, p.PhoneNumber }).ToDictionaryAsync(p => p.PatientId, ct);
        var userIds = rows.Select(r => r.GrantedBy).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.UserId)).ToDictionaryAsync(u => u.UserId, u => u.FullName, ct);
        var branchIds = rows.Select(r => r.BranchId).Distinct().ToList();
        var branches = await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.BranchId)).ToDictionaryAsync(b => b.BranchId, b => b.Name, ct);
        var today = clock.BaghdadToday();
        return rows.Select(d =>
        {
            var mine = itemStates.Where(x => x.DeferralId == d.DeferralId).ToList();
            var paid = mine.Where(x => PaidStates.Contains(x.Status)).Sum(x => x.NetPrice);
            var remaining = d.Status == DeferralStatuses.Active ? mine.Where(x => x.Status == InvoiceItemStatuses.Deferred).Sum(x => x.NetPrice) : 0;
            var p = patients.GetValueOrDefault(d.PatientId);
            return new DeferralDto(d.DeferralId, d.PatientId, p?.FullName ?? "", p?.PhoneNumber, d.BranchId, branches.GetValueOrDefault(d.BranchId, ""),
                d.Reason, d.Note, d.Amount, paid, remaining, d.DueDate,
                d.Status == DeferralStatuses.Active && d.DueDate is { } due && due < today, d.Status, d.GrantedBy, users.GetValueOrDefault(d.GrantedBy, ""),
                d.CreatedAt, d.SettledAt, d.WriteOffReason);
        }).ToList();
    }
}
