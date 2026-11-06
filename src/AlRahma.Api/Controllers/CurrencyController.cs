using AlRahma.Application.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

/// <summary>العملات (7.2): سعر صرف الدولار اليومي للفرع الحالي وسجله.</summary>
[ApiController]
[Route("api/v1/exchange-rates")]
[Produces("application/json")]
[Authorize]
public class CurrencyController(CurrencyService currency) : ControllerBase
{
    /// <summary>سعر اليوم (null = لم يُدخل بعد، والتحصيل بالدولار مرفوض) وسجل آخر 60 يوماً.</summary>
    [HttpGet]
    [ProducesResponseType<ExchangeRateTodayDto>(200)]
    public Task<ExchangeRateTodayDto> Get(CancellationToken ct) => currency.RatesAsync(ct);

    /// <summary>يحدده محاسب الفرع (shifts.review) أو من يملك settings.edit: usd_to_iqd (دينار لكل دولار)، و date اختياري (اليوم أو بعده).</summary>
    [HttpPut]
    [ProducesResponseType<ExchangeRateDto>(200)]
    public Task<ExchangeRateDto> Set(SetExchangeRateRequest req, CancellationToken ct) => currency.SetAsync(req, ct);
}
