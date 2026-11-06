using AlRahma.Api.Infrastructure;
using AlRahma.Application.Common;
using AlRahma.Application.Support;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

/// <summary>قسم الدعم (الجلسة 4): الأسئلة الشائعة، التواصل مع المجمع، طلبات الدعم والشكاوى.</summary>
[ApiController]
[Produces("application/json")]
public class SupportController(SupportService support) : ControllerBase
{
    /// <summary>الأسئلة الشائعة الفعالة (للمراجع وللعموم، بلا تسجيل دخول).</summary>
    [HttpGet("api/v1/public/faq")]
    [AllowAnonymous]
    public async Task<IActionResult> PublicFaq(CancellationToken ct) => Ok(new { items = await support.FaqAsync(false, ct) });

    /// <summary>بيانات التواصل لكل فرع فعال: هاتف، واتساب، عنوان، موقع، دوام.</summary>
    [HttpGet("api/v1/public/contacts")]
    [AllowAnonymous]
    public async Task<IActionResult> Contacts(CancellationToken ct) => Ok(new { items = await support.ContactsAsync(ct) });

    /// <summary>إدارة الأسئلة الشائعة: كل العناصر بما فيها الموقوفة.</summary>
    [HttpGet("api/v1/faq-items")]
    [Authorize(Policy = Permissions.SupportManage)]
    public async Task<IActionResult> FaqAll(CancellationToken ct) => Ok(new { items = await support.FaqAsync(true, ct) });

    [HttpPost("api/v1/faq-items")]
    [Authorize(Policy = Permissions.SupportManage)]
    [ProducesResponseType<FaqItemDto>(201)]
    public async Task<IActionResult> CreateFaq(FaqItemRequest req, CancellationToken ct) => StatusCode(201, await support.CreateFaqAsync(req, ct));

    [HttpPatch("api/v1/faq-items/{faqId:guid}")]
    [Authorize(Policy = Permissions.SupportManage)]
    public Task<FaqItemDto> UpdateFaq(Guid faqId, FaqItemRequest req, CancellationToken ct) => support.UpdateFaqAsync(faqId, req, ct);

    [HttpDelete("api/v1/faq-items/{faqId:guid}")]
    [Authorize(Policy = Permissions.SupportManage)]
    public async Task<IActionResult> DeleteFaq(Guid faqId, CancellationToken ct)
    {
        await support.DeleteFaqAsync(faqId, ct);
        return NoContent();
    }

    /// <summary>طلب دعم أو شكوى من المراجع: type = booking | payment | refund | app | other، ووصف، وحجز اختياري.</summary>
    [HttpPost("api/v1/me/support-requests")]
    [Authorize(Policy = Policies.Patient)]
    [ProducesResponseType<SupportRequestDto>(201)]
    public async Task<IActionResult> Create(CreateSupportRequest req, CancellationToken ct) => StatusCode(201, await support.CreateRequestAsync(req, ct));

    [HttpGet("api/v1/me/support-requests")]
    [Authorize(Policy = Policies.Patient)]
    public async Task<IActionResult> Mine(CancellationToken ct) => Ok(new { items = await support.MyRequestsAsync(ct) });

    /// <summary>لوحة الموظف: طلبات الفرع. status = open | replied.</summary>
    [HttpGet("api/v1/support-requests")]
    [Authorize(Policy = Permissions.SupportManage)]
    public Task<PagedResult<SupportRequestDto>> List([FromQuery(Name = "status")] string? status, [FromQuery(Name = "type")] string? type,
        [FromQuery(Name = "page")] int page = 1, [FromQuery(Name = "page_size")] int pageSize = 20, CancellationToken ct = default) =>
        support.ListAsync(new SupportListQuery(status, type, page, pageSize), ct);

    [HttpPost("api/v1/support-requests/{id:guid}/reply")]
    [Authorize(Policy = Permissions.SupportManage)]
    public Task<SupportRequestDto> Reply(Guid id, SupportReplyRequest req, CancellationToken ct) => support.ReplyAsync(id, req, ct);
}
