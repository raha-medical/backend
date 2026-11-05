using AlRahma.Application.Devices;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

[ApiController]
[Route("api/v1/devices")]
[Produces("application/json")]
[Authorize]
public class DevicesController(DeviceService devices) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Permissions.DevicesView)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(new { items = await devices.ListAsync(ct) });

    /// <summary>تسجيل جهاز. لشاشة الانتظار (kind = display) يُعاد display_key مرة واحدة فقط.</summary>
    [HttpPost]
    [Authorize(Policy = Permissions.DevicesCreate)]
    [ProducesResponseType<CreateDeviceResponse>(201)]
    public async Task<IActionResult> Create(CreateDeviceRequest req, CancellationToken ct) =>
        StatusCode(201, await devices.CreateAsync(req, ct));

    [HttpPost("{deviceId:guid}/display-key")]
    [Authorize(Policy = Permissions.DevicesEdit)]
    [ProducesResponseType<CreateDeviceResponse>(200)]
    public Task<CreateDeviceResponse> RotateKey(Guid deviceId, CancellationToken ct) => devices.RotateDisplayKeyAsync(deviceId, ct);
}
