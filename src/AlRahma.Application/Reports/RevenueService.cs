using AlRahma.Application.Abstractions;
using AlRahma.Application.Billing;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Reports;

public record DepartmentRevenue(string Department, long Gross, long Refunds, long Net);
public record MethodRevenue(string Method, long Amount, long Refunded, long Net);
public record BranchRevenueRow(Guid BranchId, string BranchName, long Gross, long Refunds, long TotalNet, long DepositsReceived, long CashIn,
    long Expenses, long NetAfterExpenses, int PaymentsCount);

/// <summary>
/// gross/refunds/total_net: إيراد الخدمات (المحصّل على البنود − المسترجع)، ويشمل ما دُفع من رصيد المريض.
/// by_method: حركة النقد الفعلية حسب طريقة الدفع (تشمل العربون). credit_used: ما دُفع من رصيد المرضى (ليس نقداً جديداً).
/// net_after_expenses = total_net − expenses.
/// </summary>
public record RevenueReport(DateOnly From, DateOnly To, Guid? BranchId, IReadOnlyList<DepartmentRevenue> ByDepartment,
    IReadOnlyList<MethodRevenue> ByMethod, long TotalGross, long TotalRefunds, long TotalNet, long DepositsReceived, long CreditUsed,
    long DeferredNew, long Discounts, long Expenses, long NetAfterExpenses, int PaymentsCount, IReadOnlyList<BranchRevenueRow> ByBranch);

/// <summary>الأرقام الخام لكل فرع في فترة، يستخدمها تقرير الإيراد ولوحة المدير العام.</summary>
public class RevenueNumbers
{
    public Dictionary<(Guid Branch, string Department), long> Gross { get; } = [];
    public Dictionary<(Guid Branch, string Department), long> Refunds { get; } = [];
    public Dictionary<(Guid Branch, string Method), long> Received { get; } = [];
    public Dictionary<(Guid Branch, string Method), long> Refunded { get; } = [];
    public Dictionary<Guid, long> Deposits { get; } = [];
    public Dictionary<Guid, long> CreditUsed { get; } = [];
    public Dictionary<Guid, int> PaymentsCount { get; } = [];
    public Dictionary<Guid, long> Deferred { get; } = [];
    public Dictionary<Guid, long> Discounts { get; } = [];
    public Dictionary<Guid, long> Expenses { get; } = [];

    public long GrossOf(Guid b) => Gross.Where(x => x.Key.Branch == b).Sum(x => x.Value);
    public long RefundsOf(Guid b) => Refunds.Where(x => x.Key.Branch == b).Sum(x => x.Value);
    public long NetOf(Guid b) => GrossOf(b) - RefundsOf(b);
    public long CashInOf(Guid b) => Received.Where(x => x.Key.Branch == b).Sum(x => x.Value) - Refunded.Where(x => x.Key.Branch == b).Sum(x => x.Value);
}

public class RevenueService(IAppDbContext db, ReportScope scope)
{
    public static readonly string[] Departments = ["clinic", "lab", "radiology", "pharmacy"];
    public static readonly string[] Methods = [TenderMethods.Cash, TenderMethods.Pos, TenderMethods.Online];

    /// <summary>date لليوم الواحد، أو from/to لفترة. بدونها: اليوم.</summary>
    public async Task<RevenueReport> ReportAsync(Guid? branchId, DateOnly? date, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var period = date is { } d ? new ReportPeriod(d, d) : from is null && to is null ? new ReportPeriod(scope.Today, scope.Today) : scope.Period(from, to);
        var branches = await scope.BranchesAsync(branchId, ct);
        var n = await NumbersAsync(branches, period, ct);
        var names = await scope.BranchNamesAsync(branches, ct);

        var byDept = Departments.Select(dep =>
        {
            long g = n.Gross.Where(x => x.Key.Department == dep).Sum(x => x.Value), r = n.Refunds.Where(x => x.Key.Department == dep).Sum(x => x.Value);
            return new DepartmentRevenue(dep, g, r, g - r);
        }).ToList();
        var refundMethods = new Dictionary<string, string> { [TenderMethods.Cash] = RefundMethods.Cash, [TenderMethods.Pos] = RefundMethods.Pos, [TenderMethods.Online] = RefundMethods.OnlineReversal };
        var byMethod = Methods.Select(m =>
        {
            long a = n.Received.Where(x => x.Key.Method == m).Sum(x => x.Value), r = n.Refunded.Where(x => x.Key.Method == refundMethods[m]).Sum(x => x.Value);
            return new MethodRevenue(m, a, r, a - r);
        }).ToList();
        var rows = branches.Select(b => Row(b, names.GetValueOrDefault(b, ""), n)).OrderByDescending(r => r.TotalNet).ToList();
        long gross = byDept.Sum(x => x.Gross), refunds = byDept.Sum(x => x.Refunds), expenses = n.Expenses.Values.Sum();
        return new RevenueReport(period.From, period.To, branchId, byDept, byMethod, gross, refunds, gross - refunds, n.Deposits.Values.Sum(),
            n.CreditUsed.Values.Sum(), n.Deferred.Values.Sum(), n.Discounts.Values.Sum(), expenses, gross - refunds - expenses,
            n.PaymentsCount.Values.Sum(), rows);
    }

    public static BranchRevenueRow Row(Guid b, string name, RevenueNumbers n)
    {
        var net = n.NetOf(b);
        var expenses = n.Expenses.GetValueOrDefault(b);
        return new BranchRevenueRow(b, name, n.GrossOf(b), n.RefundsOf(b), net, n.Deposits.GetValueOrDefault(b), n.CashInOf(b), expenses, net - expenses,
            n.PaymentsCount.GetValueOrDefault(b));
    }

    public async Task<RevenueNumbers> NumbersAsync(IReadOnlyList<Guid> branches, ReportPeriod period, CancellationToken ct)
    {
        var ids = branches.ToList();
        DateTimeOffset s = period.StartUtc, e = period.EndUtc;
        var n = new RevenueNumbers();
        var payments = db.Payments.AsNoTracking().Where(p => ids.Contains(p.BranchId) && p.CreatedAt >= s && p.CreatedAt < e);

        var allocations = await (
            from p in payments
            join a in db.PaymentAllocations on p.PaymentId equals a.PaymentId
            join i in db.InvoiceItems on a.InvoiceItemId equals i.InvoiceItemId
            group a.Amount by new { p.BranchId, i.ItemType } into g
            select new { g.Key.BranchId, g.Key.ItemType, Sum = g.Sum() }).ToListAsync(ct);
        foreach (var a in allocations) Add(n.Gross, (a.BranchId, FinanceCore.DepartmentOf(a.ItemType)), a.Sum);

        var tenders = await (
            from p in payments
            join t in db.PaymentTenders on p.PaymentId equals t.PaymentId
            group t.Amount by new { p.BranchId, t.Method } into g
            select new { g.Key.BranchId, g.Key.Method, Sum = g.Sum() }).ToListAsync(ct);
        foreach (var t in tenders) Add(n.Received, (t.BranchId, t.Method), t.Sum);

        var perBranch = await payments.GroupBy(p => p.BranchId)
            .Select(g => new
            {
                Branch = g.Key,
                Count = g.Count(),
                CreditUsed = g.Sum(p => p.DepositUsed),
                Deposits = g.Where(p => !p.Allocations.Any()).Sum(p => p.TotalPaid),
            }).ToListAsync(ct);
        foreach (var p in perBranch)
        {
            n.PaymentsCount[p.Branch] = p.Count;
            n.CreditUsed[p.Branch] = p.CreditUsed;
            n.Deposits[p.Branch] = p.Deposits;
        }

        var refunds = await (
            from r in db.Refunds.AsNoTracking()
            join i in db.InvoiceItems on r.InvoiceItemId equals i.InvoiceItemId
            join inv in db.Invoices on i.InvoiceId equals inv.InvoiceId
            where r.Status == RefundStatuses.Completed && ids.Contains(inv.BranchId)
                  && (r.DecidedAt ?? r.CreatedAt) >= s && (r.DecidedAt ?? r.CreatedAt) < e
            group r.Amount by new { inv.BranchId, i.ItemType, r.Method } into g
            select new { g.Key.BranchId, g.Key.ItemType, g.Key.Method, Sum = g.Sum() }).ToListAsync(ct);
        foreach (var r in refunds)
        {
            Add(n.Refunds, (r.BranchId, FinanceCore.DepartmentOf(r.ItemType)), r.Sum);
            if (r.Method != RefundMethods.PatientCredit) Add(n.Refunded, (r.BranchId, r.Method), r.Sum);
        }

        foreach (var x in await db.Deferrals.AsNoTracking().Where(d => ids.Contains(d.BranchId) && d.CreatedAt >= s && d.CreatedAt < e)
                     .GroupBy(d => d.BranchId).Select(g => new { g.Key, Sum = g.Sum(d => d.Amount) }).ToListAsync(ct))
            n.Deferred[x.Key] = x.Sum;
        foreach (var x in await db.Invoices.AsNoTracking()
                     .Where(i => ids.Contains(i.BranchId) && i.CreatedAt >= s && i.CreatedAt < e && i.Status != InvoiceStatuses.Void)
                     .GroupBy(i => i.BranchId).Select(g => new { g.Key, Sum = g.Sum(i => i.DiscountAmount) }).ToListAsync(ct))
            n.Discounts[x.Key] = x.Sum;
        foreach (var x in await db.Expenses.AsNoTracking()
                     .Where(x => ids.Contains(x.BranchId) && x.Status == ExpenseStatuses.Recorded && x.ExpenseDate >= period.From && x.ExpenseDate <= period.To)
                     .GroupBy(x => x.BranchId).Select(g => new { g.Key, Sum = g.Sum(x => x.Amount) }).ToListAsync(ct))
            n.Expenses[x.Key] = x.Sum;
        return n;
    }

    private static void Add<TKey>(Dictionary<TKey, long> d, TKey key, long v) where TKey : notnull => d[key] = d.GetValueOrDefault(key) + v;
}
