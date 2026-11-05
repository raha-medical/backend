using AlRahma.Api.Infrastructure;
using AlRahma.Application.Services;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

[ApiController]
[Route("api/v1/services")]
[Produces("application/json")]
[Authorize]
public class ServicesController(ServiceCatalog catalog) : ControllerBase
{
    /// <summary>خدمات الفرع الحالي وأسعارها. include_inactive لمن يملك services.view (أو services.manage).</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery(Name = "type")] string? type,
        [FromQuery(Name = "clinic_id")] Guid? clinicId,
        [FromQuery(Name = "q")] string? q,
        [FromQuery(Name = "include_inactive")] bool includeInactive = false,
        CancellationToken ct = default) =>
        Ok(new { items = await catalog.ListAsync(new ServiceListQuery(type, clinicId, q, includeInactive), ct) });

    [HttpGet("{serviceId:guid}")]
    [ProducesResponseType<ServiceDto>(200)]
    public Task<ServiceDto> Get(Guid serviceId, CancellationToken ct) => catalog.GetAsync(serviceId, ct);

    [HttpPost]
    [Authorize(Policy = Permissions.ServicesCreate)]
    [Idempotent]
    [ProducesResponseType<ServiceDto>(201)]
    public async Task<IActionResult> Create(CreateServiceRequest req, CancellationToken ct)
    {
        var s = await catalog.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { serviceId = s.ServiceId }, s);
    }

    /// <summary>تعديل الاسم أو السعر أو التفعيل. تغيير السعر لا يؤثر على الفواتير السابقة.</summary>
    [HttpPatch("{serviceId:guid}")]
    [Authorize(Policy = Permissions.ServicesEdit)]
    [ProducesResponseType<ServiceDto>(200)]
    public Task<ServiceDto> Update(Guid serviceId, UpdateServiceRequest req, CancellationToken ct) =>
        catalog.UpdateAsync(serviceId, req, ct);
}
