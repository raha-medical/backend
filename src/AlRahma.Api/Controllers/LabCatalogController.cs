using AlRahma.Api.Infrastructure;
using AlRahma.Application.Services;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

/// <summary>كتالوج المختبر في الفرع الحالي، دون منح الفني إدارة خدمات الأقسام الأخرى.</summary>
[ApiController]
[Route("api/v1/lab/services")]
[Produces("application/json")]
public class LabCatalogController(ServiceCatalog catalog) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Policies.LabRead)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(new { items = await catalog.ListAsync(new ServiceListQuery("lab", null, null, false), ct) });

    [HttpPost]
    [Authorize(Policy = Permissions.LabWrite)]
    [Idempotent]
    public async Task<IActionResult> Create(LabServiceRequest req, CancellationToken ct) =>
        StatusCode(201, await catalog.CreateLabAsync(req, ct));

    [HttpPatch("{serviceId:guid}")]
    [Authorize(Policy = Permissions.LabWrite)]
    public Task<ServiceDto> Update(Guid serviceId, LabServiceRequest req, CancellationToken ct) =>
        catalog.UpdateLabAsync(serviceId, req, ct);
}
