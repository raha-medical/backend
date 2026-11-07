using AlRahma.Api.Infrastructure;
using AlRahma.Application.Services;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

[ApiController]
[Route("api/v1/pharmacy/drugs")]
[Produces("application/json")]
public class PharmacyCatalogController(ServiceCatalog catalog) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Policies.PharmacyRead)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(new { items = await catalog.ListAsync(new ServiceListQuery("pharmacy", null, null, false), ct) });

    [HttpPost]
    [Authorize(Policy = Permissions.PharmacyDispense)]
    [Idempotent]
    public async Task<IActionResult> Create(PharmacyDrugRequest req, CancellationToken ct) =>
        StatusCode(201, await catalog.CreateDrugAsync(req, ct));

    [HttpPatch("{serviceId:guid}")]
    [Authorize(Policy = Permissions.PharmacyDispense)]
    public Task<ServiceDto> Update(Guid serviceId, PharmacyDrugRequest req, CancellationToken ct) =>
        catalog.UpdateDrugAsync(serviceId, req, ct);
}
