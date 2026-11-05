using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Reports;

/// <summary>
/// رصيد قُبض في فرع (holder) واستُخدم لخدمة في فرع آخر (serving): النقد في درج الأول والإيراد في الثاني،
/// فالأول مدين للثاني بالمبلغ.
/// </summary>
public record SettlementFlowDto(Guid HolderBranchId, string HolderBranchName, Guid ServingBranchId, string ServingBranchName, long Amount, int Entries);

/// <summary>
/// credit_received: أرصدة قُبضت في الفرع خلال الفترة. credit_used: أرصدة استُخدمت فيه.
/// net_settlement: موجب = للفرع مبلغ عند الفروع الأخرى، سالب = عليه للفروع الأخرى.
/// outstanding_credit_held: أرصدة المرضى التي قبضها الفرع ولم تُستخدم بعد (التزام عليه) حتى نهاية الفترة.
/// </summary>
public record BranchSettlementRow(Guid BranchId, string BranchName, long CreditReceived, long CreditUsed, long UsedFromOtherBranches,
    long UsedByOtherBranches, long NetSettlement, long OutstandingCreditHeld);

public record BranchSettlementReport(DateOnly From, DateOnly To, IReadOnlyList<BranchSettlementRow> Branches, IReadOnlyList<SettlementFlowDto> Flows,
    long TotalOutstandingCredit);

/// <summary>
/// التسويات بين الفروع لرصيد المريض. الرصيد موحد في كل الفروع، فكل استخدام يُنسب لأقدم رصيد للمريض (الأول يُستخدم أولاً)
/// ليُعرف الفرع الذي قبض المبلغ أصلاً.
/// </summary>
public class BranchSettlementService(IAppDbContext db, ICurrentUser current, IClock clock)
{
    public const int MaxDays = 366;

    public async Task<BranchSettlementReport> ReportAsync(DateOnly? from, DateOnly? to, Guid? branchId, CancellationToken ct)
    {
        var today = clock.BaghdadToday();
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1);
        if (end < start || end.DayNumber - start.DayNumber >= MaxDays)
            throw AppException.Validation(new Dictionary<string, string[]> { ["to"] = [$"الفترة {MaxDays} يوماً كحد أقصى، والنهاية بعد البداية"] });

        // المدير العام يرى كل الفروع، وغيره فروعه المسندة إليه فقط
        var visible = current.Role == Roles.Admin
            ? null
            : (await db.UserBranches.Where(ub => ub.UserId == current.UserId).Select(ub => ub.BranchId).ToListAsync(ct)).ToHashSet();
        if (branchId is { } only && visible is not null && !visible.Contains(only)) throw AppException.Forbidden("الفرع ليس من فروعك");

        var startUtc = ClockExtensions.ToUtc(start, TimeOnly.MinValue);
        var endUtc = ClockExtensions.ToUtc(end.AddDays(1), TimeOnly.MinValue);
        var entries = await db.PatientCredits.AsNoTracking().Where(e => e.CreatedAt < endUtc)
            .OrderBy(e => e.PatientId).ThenBy(e => e.CreatedAt).ThenBy(e => e.EntryId)
            .Select(e => new { e.PatientId, e.BranchId, e.Amount, e.CreatedAt }).ToListAsync(ct);

        var received = new Dictionary<Guid, long>();
        var used = new Dictionary<Guid, long>();
        var outstanding = new Dictionary<Guid, long>();
        var flows = new Dictionary<(Guid Holder, Guid Serving), (long Amount, int Entries)>();
        static void Add(Dictionary<Guid, long> d, Guid k, long v) => d[k] = d.GetValueOrDefault(k) + v;

        foreach (var patient in entries.GroupBy(e => e.PatientId))
        {
            var lots = new LinkedList<(Guid Branch, long Remaining)>();
            foreach (var e in patient)
            {
                var inPeriod = e.CreatedAt >= startUtc;
                if (e.Amount > 0)
                {
                    lots.AddLast((e.BranchId, e.Amount));
                    if (inPeriod) Add(received, e.BranchId, e.Amount);
                    continue;
                }
                var need = -e.Amount;
                if (inPeriod) Add(used, e.BranchId, need);
                while (need > 0 && lots.First is { } lot)
                {
                    var take = Math.Min(need, lot.Value.Remaining);
                    need -= take;
                    if (take == lot.Value.Remaining) lots.RemoveFirst();
                    else lot.Value = (lot.Value.Branch, lot.Value.Remaining - take);
                    if (inPeriod && lot.Value.Branch != e.BranchId)
                    {
                        var key = (lot.Value.Branch, e.BranchId);
                        var f = flows.GetValueOrDefault(key);
                        flows[key] = (f.Amount + take, f.Entries + 1);
                    }
                }
            }
            foreach (var lot in lots) Add(outstanding, lot.Branch, lot.Remaining);
        }

        var names = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.BranchId, b => b.Name, ct);
        bool Shown(Guid b) => (branchId is null || branchId == b) && (visible is null || visible.Contains(b));
        var flowList = flows.Where(f => Shown(f.Key.Holder) || Shown(f.Key.Serving))
            .Select(f => new SettlementFlowDto(f.Key.Holder, names.GetValueOrDefault(f.Key.Holder, ""), f.Key.Serving,
                names.GetValueOrDefault(f.Key.Serving, ""), f.Value.Amount, f.Value.Entries))
            .OrderByDescending(f => f.Amount).ToList();
        var rows = names.Keys.Where(Shown)
            .Select(b =>
            {
                var fromOthers = flows.Where(f => f.Key.Serving == b).Sum(f => f.Value.Amount);
                var byOthers = flows.Where(f => f.Key.Holder == b).Sum(f => f.Value.Amount);
                return new BranchSettlementRow(b, names[b], received.GetValueOrDefault(b), used.GetValueOrDefault(b), fromOthers, byOthers,
                    fromOthers - byOthers, outstanding.GetValueOrDefault(b));
            })
            .OrderBy(r => r.BranchName).ToList();
        return new BranchSettlementReport(start, end, rows, flowList, rows.Sum(r => r.OutstandingCreditHeld));
    }
}
