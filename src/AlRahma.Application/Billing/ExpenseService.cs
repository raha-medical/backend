using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Application.Reports;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Billing;

public record CreateExpenseRequest(Guid? ExpenseId, Guid? BranchId, string? Category, long? Amount, DateOnly? ExpenseDate, string? Description,
    string? Payee, string? PaidFrom);
public record VoidExpenseRequest(string? Reason);
public record ExpenseDto(Guid ExpenseId, string ExpenseNumber, Guid BranchId, string BranchName, string Category, string CategoryName, long Amount,
    DateOnly ExpenseDate, string Description, string? Payee, string PaidFrom, Guid? ShiftId, string Status, Guid CreatedById, string CreatedBy,
    DateTimeOffset CreatedAt, string? VoidReason, DateTimeOffset? VoidedAt, string? DrawerToken = null, bool OpenDrawer = false);
public record ExpenseCategoryTotal(string Category, string CategoryName, long Amount, int Count);
public record ExpenseListResult(IReadOnlyList<ExpenseDto> Items, int Page, int PageSize, int Total, long TotalAmount,
    IReadOnlyList<ExpenseCategoryTotal> ByCategory);
public record ExpenseListQuery(Guid? BranchId, string? Category, string? PaidFrom, DateOnly? From, DateOnly? To, string? Status, int Page = 1, int PageSize = 20);

/// <summary>
/// مصاريف الفروع. shift_cash: نقد من درج الوردية المفتوحة للموظف، فينقص من المتوقع في الدرج عند الإغلاق الأعمى ويفتح الدرج بتوكن.
/// treasury: من صندوق الفرع أو البنك أو الإدارة، لا يمس الأدراج. لا حذف: الإلغاء بسبب ويبقى في السجل.
/// </summary>
public class ExpenseService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock, NumberSequences numbers, FinanceCore finance,
    IDrawerTokenIssuer drawer, ReportScope scope)
{
    public async Task<ExpenseDto> CreateAsync(CreateExpenseRequest req, CancellationToken ct)
    {
        var today = clock.BaghdadToday();
        new Validator()
            .Check(!string.IsNullOrWhiteSpace(req.Category), "category", "التصنيف مطلوب")
            .Check(req.Amount is > 0, "amount", "المبلغ أكبر من صفر")
            .Check(!string.IsNullOrWhiteSpace(req.Description), "description", "وصف المصروف مطلوب")
            .Check(req.PaidFrom is not null && ExpensePaidFrom.All.Contains(req.PaidFrom), "paid_from", string.Join(" | ", ExpensePaidFrom.All))
            .Check(req.ExpenseDate is null || req.ExpenseDate <= today, "expense_date", "لا يُسجل مصروف بتاريخ قادم")
            .Check(req.ExpenseDate is null || req.ExpenseDate > today.AddDays(-ReportScope.MaxDays), "expense_date", "التاريخ قديم جداً")
            .ThrowIfInvalid();
        var category = await db.LookupValues.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Category == LookupCategories.ExpenseCategory && l.Code == req.Category && l.IsActive, ct);
        if (category is null)
            throw AppException.Validation(new Dictionary<string, string[]> { ["category"] = ["تصنيف غير موجود (GET /lookups/expense_category)"] });

        var branchId = req.BranchId ?? current.BranchId;
        if (branchId != current.BranchId)
        {
            if (current.Role != Roles.Admin) throw AppException.Forbidden("تسجّل مصاريف فرعك الحالي فقط");
            if (!await db.Branches.AnyAsync(b => b.BranchId == branchId, ct)) throw AppException.NotFound("الفرع غير موجود");
            if (req.PaidFrom == ExpensePaidFrom.ShiftCash)
                throw AppException.Validation(new Dictionary<string, string[]> { ["paid_from"] = ["الصرف من الدرج في فرعك الحالي فقط"] });
        }
        var id = req.ExpenseId ?? UuidV7.New();
        if (await db.Expenses.AnyAsync(x => x.ExpenseId == id, ct)) throw new AppException(409, "EXPENSE_EXISTS", "يوجد مصروف بنفس المعرف");

        Shift? shift = req.PaidFrom == ExpensePaidFrom.ShiftCash ? await finance.RequireOpenShiftAsync(ct) : null;
        await using var tx = shift is null ? null : await db.BeginTransactionAsync(ct);
        if (shift is not null) await finance.LockOpenShiftAsync(shift.ShiftId, ct);
        var seq = await numbers.NextAsync(branchId, "expense", "all", ct);
        var code = await numbers.BranchCodeAsync(branchId, ct);
        var expense = new Expense
        {
            ExpenseId = id,
            ExpenseNumber = $"{code}-EXP-{seq:D6}",
            BranchId = branchId,
            Category = category.Code,
            Amount = req.Amount!.Value,
            ExpenseDate = req.ExpenseDate ?? today,
            Description = req.Description!.Trim(),
            Payee = string.IsNullOrWhiteSpace(req.Payee) ? null : req.Payee.Trim(),
            PaidFrom = req.PaidFrom!,
            ShiftId = shift?.ShiftId,
            CreatedBy = current.UserId,
            CreatedAt = clock.UtcNow,
        };
        db.Expenses.Add(expense);
        audit.Add("EXPENSE_RECORDED", "expense", id, after: new
        {
            expense.ExpenseNumber, expense.Category, expense.Amount, expense.ExpenseDate, expense.PaidFrom, expense.ShiftId, expense.Payee,
        }, reason: expense.Description);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);

        var dto = (await ToDtosAsync([expense], ct)).Single();
        return shift is null || !drawer.Enabled ? dto : dto with { DrawerToken = drawer.Issue(expense.ExpenseNumber, shift.ShiftId, shift.DeviceId), OpenDrawer = true };
    }

    public async Task<ExpenseDto> GetAsync(Guid expenseId, CancellationToken ct)
    {
        var x = await LoadAsync(expenseId, ct);
        return (await ToDtosAsync([x], ct)).Single();
    }

    public async Task<ExpenseListResult> ListAsync(ExpenseListQuery q, CancellationToken ct)
    {
        new Validator()
            .Check(q.PaidFrom is null || ExpensePaidFrom.All.Contains(q.PaidFrom), "paid_from", string.Join(" | ", ExpensePaidFrom.All))
            .Check(q.Status is null or ExpenseStatuses.Recorded or ExpenseStatuses.Void, "status", "recorded | void")
            .ThrowIfInvalid();
        var period = scope.Period(q.From, q.To);
        var page = new PageQuery(q.Page, q.PageSize);
        var branches = (await scope.BranchesAsync(q.BranchId, ct)).ToList();
        var status = q.Status ?? ExpenseStatuses.Recorded;
        var query = db.Expenses.AsNoTracking()
            .Where(x => branches.Contains(x.BranchId) && x.Status == status && x.ExpenseDate >= period.From && x.ExpenseDate <= period.To);
        if (q.Category is not null) query = query.Where(x => x.Category == q.Category);
        if (q.PaidFrom is not null) query = query.Where(x => x.PaidFrom == q.PaidFrom);

        var groups = await query.GroupBy(x => x.Category).Select(g => new { g.Key, Sum = g.Sum(x => x.Amount), Count = g.Count() }).ToListAsync(ct);
        var names = await CategoryNamesAsync(ct);
        var rows = await query.OrderByDescending(x => x.ExpenseDate).ThenByDescending(x => x.CreatedAt)
            .Skip((page.SafePage - 1) * page.SafePageSize).Take(page.SafePageSize).ToListAsync(ct);
        return new ExpenseListResult(await ToDtosAsync(rows, ct), page.SafePage, page.SafePageSize, groups.Sum(g => g.Count), groups.Sum(g => g.Sum),
            groups.OrderByDescending(g => g.Sum).Select(g => new ExpenseCategoryTotal(g.Key, names.GetValueOrDefault(g.Key, g.Key), g.Sum, g.Count)).ToList());
    }

    public async Task<ExpenseDto> VoidAsync(Guid expenseId, VoidExpenseRequest req, CancellationToken ct)
    {
        new Validator().Check(!string.IsNullOrWhiteSpace(req.Reason), "reason", "سبب الإلغاء مطلوب").ThrowIfInvalid();
        var x = await LoadAsync(expenseId, ct);
        if (x.Status == ExpenseStatuses.Void) return (await ToDtosAsync([x], ct)).Single();
        if (x.ShiftId is { } sid && !await db.Shifts.AnyAsync(s => s.ShiftId == sid && s.Status == ShiftStatuses.Open, ct))
            throw new AppException(409, "SHIFT_CLOSED", "لا يُلغى مصروف من وردية مغلقة", "الوردية أُغلقت وحُسب المتوقع في درجها");
        x.Status = ExpenseStatuses.Void;
        x.VoidedBy = current.UserId;
        x.VoidedAt = clock.UtcNow;
        x.VoidReason = req.Reason!.Trim();
        audit.Add("EXPENSE_VOIDED", "expense", x.ExpenseId, before: new { status = ExpenseStatuses.Recorded, x.Amount }, after: new { x.Status },
            reason: x.VoidReason);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([x], ct)).Single();
    }

    private async Task<Expense> LoadAsync(Guid expenseId, CancellationToken ct)
    {
        var x = await db.Expenses.FirstOrDefaultAsync(e => e.ExpenseId == expenseId, ct)
                ?? throw new AppException(404, "EXPENSE_NOT_FOUND", "المصروف غير موجود");
        if (current.Role != Roles.Admin && x.BranchId != current.BranchId
            && !await db.UserBranches.AnyAsync(ub => ub.UserId == current.UserId && ub.BranchId == x.BranchId, ct))
            throw new AppException(404, "EXPENSE_NOT_FOUND", "المصروف غير موجود");
        return x;
    }

    private Task<Dictionary<string, string>> CategoryNamesAsync(CancellationToken ct) =>
        db.LookupValues.AsNoTracking().Where(l => l.Category == LookupCategories.ExpenseCategory).ToDictionaryAsync(l => l.Code, l => l.NameAr, ct);

    private async Task<List<ExpenseDto>> ToDtosAsync(List<Expense> rows, CancellationToken ct)
    {
        var names = await CategoryNamesAsync(ct);
        var userIds = rows.Select(r => r.CreatedBy).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.UserId)).ToDictionaryAsync(u => u.UserId, u => u.FullName, ct);
        var branchIds = rows.Select(r => r.BranchId).Distinct().ToList();
        var branches = await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.BranchId)).ToDictionaryAsync(b => b.BranchId, b => b.Name, ct);
        return rows.Select(x => new ExpenseDto(x.ExpenseId, x.ExpenseNumber, x.BranchId, branches.GetValueOrDefault(x.BranchId, ""), x.Category,
            names.GetValueOrDefault(x.Category, x.Category), x.Amount, x.ExpenseDate, x.Description, x.Payee, x.PaidFrom, x.ShiftId, x.Status,
            x.CreatedBy, users.GetValueOrDefault(x.CreatedBy, ""), x.CreatedAt, x.VoidReason, x.VoidedAt)).ToList();
    }
}
