using AlRahma.Api.Infrastructure;
using AlRahma.Application.Lookups;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

/// <summary>قوائم الاختيار (أسباب الإغلاق، الاختصاصات، العناوين الوظيفية). القراءة للجميع، والإدارة للمدير العام.</summary>
[ApiController]
[Route("api/v1/lookups")]
[Produces("application/json")]
[Authorize]
public class LookupsController(LookupService lookups) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Categories(CancellationToken ct) =>
        Ok(new { items = await lookups.CategoriesAsync(ct) });

    /// <summary>قيم قائمة مرتبة. include_inactive للمدير العام فقط. قائمة الاختصاصات متاحة بلا تسجيل دخول (لتطبيق المريض).</summary>
    [HttpGet("{category}")]
    [AllowAnonymous]
    public async Task<IActionResult> List(string category, [FromQuery(Name = "include_inactive")] bool includeInactive, CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true && category != Domain.Entities.LookupCategories.DoctorSpecialty)
            throw new Application.Common.AppException(401, "UNAUTHORIZED", "يلزم تسجيل الدخول");
        return Ok(new { items = await lookups.ListAsync(category, includeInactive, ct) });
    }

    [HttpPost("{category}")]
    [Authorize(Roles = Roles.Admin)]
    [Idempotent]
    [ProducesResponseType<LookupDto>(201)]
    public async Task<IActionResult> Create(string category, CreateLookupRequest req, CancellationToken ct) =>
        StatusCode(201, await lookups.CreateAsync(category, req, ct));

    [HttpPatch("{category}/{lookupId:guid}")]
    [Authorize(Roles = Roles.Admin)]
    [ProducesResponseType<LookupDto>(200)]
    public Task<LookupDto> Update(string category, Guid lookupId, UpdateLookupRequest req, CancellationToken ct) =>
        lookups.UpdateAsync(lookupId, req, ct);
}
