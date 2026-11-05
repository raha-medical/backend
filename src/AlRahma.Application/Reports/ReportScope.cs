using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Reports;

/// <summary>فترة تقرير بتوقيت بغداد، مع حدودها بالـ UTC: [StartUtc, EndUtc).</summary>
public record ReportPeriod(DateOnly From, DateOnly To)
{
    public DateTimeOffset StartUtc => ClockExtensions.ToUtc(From, TimeOnly.MinValue);
    public DateTimeOffset EndUtc => ClockExtensions.ToUtc(To.AddDays(1), TimeOnly.MinValue);
}

/// <summary>
/// نطاق التقارير: المدير العام يرى كل الفروع، وغيره الفروع المسندة إليه فقط (طلب فرع آخر: 403).
/// </summary>
public class ReportScope(IAppDbContext db, ICurrentUser current, IClock clock)
{
    public const int MaxDays = 366;

    public DateOnly Today => clock.BaghdadToday();

    /// <summary>الافتراضي: من بداية شهر النهاية حتى اليوم. الحد الأقصى 366 يوماً.</summary>
    public ReportPeriod Period(DateOnly? from, DateOnly? to)
    {
        var end = to ?? Today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1);
        if (end < start || end.DayNumber - start.DayNumber >= MaxDays)
            throw AppException.Validation(new Dictionary<string, string[]> { ["to"] = [$"الفترة {MaxDays} يوماً كحد أقصى، والنهاية بعد البداية"] });
        return new ReportPeriod(start, end);
    }

    /// <summary>الفروع التي يشملها التقرير: فرع واحد إن طُلب، وإلا كل الفروع المتاحة للمستخدم.</summary>
    public async Task<IReadOnlyList<Guid>> BranchesAsync(Guid? branchId, CancellationToken ct)
    {
        if (current.Role == Roles.Admin)
        {
            if (branchId is not { } one) return await db.Branches.AsNoTracking().OrderBy(b => b.Code).Select(b => b.BranchId).ToListAsync(ct);
            if (!await db.Branches.AnyAsync(b => b.BranchId == one, ct)) throw AppException.NotFound("الفرع غير موجود");
            return [one];
        }
        var mine = await db.UserBranches.AsNoTracking().Where(ub => ub.UserId == current.UserId).Select(ub => ub.BranchId).ToListAsync(ct);
        if (branchId is not { } only) return mine;
        if (!mine.Contains(only)) throw AppException.Forbidden("الفرع ليس من فروعك");
        return [only];
    }

    public async Task<Dictionary<Guid, string>> BranchNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        return await db.Branches.AsNoTracking().Where(b => list.Contains(b.BranchId)).ToDictionaryAsync(b => b.BranchId, b => b.Name, ct);
    }
}
