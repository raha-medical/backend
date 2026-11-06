using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using AlRahma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Billing;

public record ExchangeRateDto(Guid BranchId, DateOnly Date, int UsdToIqd, string? SetByName, DateTimeOffset SetAt);
public record SetExchangeRateRequest(int? UsdToIqd, DateOnly? Date);
public record ExchangeRateTodayDto(DateOnly Date, ExchangeRateDto? Today, IReadOnlyList<ExchangeRateDto> History);
/// <summary>سعر بند بعملته: unit_price بالدينار دائماً، و original/rate لبند USD.</summary>
public record ItemPrice(long UnitPriceIqd, string Currency, long? OriginalUnitPrice, int? Rate);

/// <summary>
/// الدينار والدولار (7.2، خطة المالك): سعر صرف يومي لكل فرع يحدده محاسب الفرع (shifts.review) أو من يملك settings.edit.
/// كل المبالغ الداخلية بالدينار؛ خدمة USD تتحول بسعر اليوم عند إنشاء البند، والتحصيل بالدولار يُرفض قبل إدخال سعر اليوم.
/// </summary>
public class CurrencyService(IAppDbContext db, ICurrentUser current, IAuditLogger audit, IClock clock)
{
    public const string Iqd = "IQD";
    public const string Usd = "USD";

    public async Task<int?> TodayRateAsync(Guid branchId, CancellationToken ct)
    {
        var today = clock.BaghdadToday();
        return await db.ExchangeRates.AsNoTracking().Where(r => r.BranchId == branchId && r.RateDate == today)
            .Select(r => (int?)r.UsdToIqd).FirstOrDefaultAsync(ct);
    }

    public async Task<int> RequireTodayRateAsync(Guid branchId, CancellationToken ct) =>
        await TodayRateAsync(branchId, ct)
        ?? throw new AppException(409, "EXCHANGE_RATE_REQUIRED", "لم يُدخل سعر صرف الدولار لليوم", "يُدخله محاسب الفرع من «الإعدادات ← العملات»");

    /// <summary>سعر الخدمة بالدينار: خدمة USD تتحول بسعر اليوم (يُرفض البند قبل إدخال السعر).</summary>
    public async Task<ItemPrice> PriceAsync(Service s, Guid branchId, CancellationToken ct)
    {
        if (s.Currency != Usd) return new ItemPrice(s.Price, Iqd, null, null);
        var rate = await RequireTodayRateAsync(branchId, ct);
        return new ItemPrice(checked(s.Price * rate), Usd, s.Price, rate);
    }

    public async Task<ExchangeRateTodayDto> RatesAsync(CancellationToken ct)
    {
        var today = clock.BaghdadToday();
        var rows = await db.ExchangeRates.AsNoTracking().Where(r => r.BranchId == current.BranchId).OrderByDescending(r => r.RateDate).Take(60).ToListAsync(ct);
        var dtos = await ToDtosAsync(rows, ct);
        return new ExchangeRateTodayDto(today, dtos.FirstOrDefault(r => r.Date == today), dtos);
    }

    public async Task<ExchangeRateDto> SetAsync(SetExchangeRateRequest req, CancellationToken ct)
    {
        if (!current.Has(Permissions.ShiftsReview) && !current.Has(Permissions.SettingsEdit))
            throw AppException.Forbidden("سعر الصرف يحدده محاسب الفرع");
        var today = clock.BaghdadToday();
        var date = req.Date ?? today;
        new Validator()
            .Check(req.UsdToIqd is >= 100 and <= 10000, "usd_to_iqd", "سعر الدولار بالدينار بين 100 و 10000")
            .Check(date >= today, "date", "لا يُعدَّل سعر يوم مضى")
            .ThrowIfInvalid();
        var row = await db.ExchangeRates.FirstOrDefaultAsync(r => r.BranchId == current.BranchId && r.RateDate == date, ct);
        var before = row is null ? null : new { row.UsdToIqd };
        if (row is null)
        {
            row = new ExchangeRate { BranchId = current.BranchId, RateDate = date };
            db.ExchangeRates.Add(row);
        }
        row.UsdToIqd = req.UsdToIqd!.Value;
        row.SetBy = current.UserId;
        row.SetAt = clock.UtcNow;
        audit.Add("EXCHANGE_RATE_SET", "branch", current.BranchId, before, new { date, row.UsdToIqd });
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([row], ct))[0];
    }

    private async Task<List<ExchangeRateDto>> ToDtosAsync(List<ExchangeRate> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => r.SetBy != null).Select(r => r.SetBy!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.UserId)).ToDictionaryAsync(u => u.UserId, u => u.FullName, ct);
        return rows.Select(r => new ExchangeRateDto(r.BranchId, r.RateDate, r.UsdToIqd, r.SetBy is { } u ? names.GetValueOrDefault(u) : null, r.SetAt)).ToList();
    }
}
