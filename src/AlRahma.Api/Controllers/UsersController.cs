using AlRahma.Api.Infrastructure;
using AlRahma.Application.Common;
using AlRahma.Application.Users;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize]
public class UsersController(UserService users) : ControllerBase
{
    [HttpGet("users")]
    [Authorize(Policy = Permissions.UsersView)]
    [ProducesResponseType<PagedResult<UserDto>>(200)]
    public Task<PagedResult<UserDto>> List(
        [FromQuery(Name = "branch_id")] Guid? branchId,
        [FromQuery(Name = "role")] string? role,
        [FromQuery(Name = "q")] string? q,
        [FromQuery(Name = "is_active")] bool? isActive,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20,
        CancellationToken ct = default) =>
        users.ListAsync(new UserListQuery(branchId, role, q, isActive, page, pageSize), ct);

    [HttpPost("users")]
    [Authorize(Policy = Permissions.UsersCreate)]
    [Idempotent]
    [ProducesResponseType<UserDto>(201)]
    public async Task<IActionResult> Create(CreateUserRequest req, CancellationToken ct)
    {
        var user = await users.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { userId = user.UserId }, user);
    }

    [HttpGet("users/{userId:guid}")]
    [Authorize(Policy = Permissions.UsersView)]
    [ProducesResponseType<UserDto>(200)]
    public Task<UserDto> Get(Guid userId, CancellationToken ct) => users.GetAsync(userId, ct);

    [HttpPatch("users/{userId:guid}")]
    [Authorize(Policy = Permissions.UsersEdit)]
    [ProducesResponseType<UserDto>(200)]
    public Task<UserDto> Update(Guid userId, UpdateUserRequest req, CancellationToken ct) => users.UpdateAsync(userId, req, ct);

    /// <summary>
    /// إيقاف موظف. للطبيب الذي عليه حجوزات قادمة: 409 DOCTOR_HAS_BOOKINGS إلا مع cancel_bookings=true
    /// فتُلغى وجباته القادمة وحجوزاتها (المدفوع يتحول رصيداً للمريض) ويعود 200 بالأعداد.
    /// </summary>
    [HttpPost("users/{userId:guid}/disable")]
    [Authorize(Policy = Permissions.UsersDelete)]
    [ProducesResponseType(204)]
    [ProducesResponseType<DisableUserResponse>(200)]
    public async Task<IActionResult> Disable(Guid userId, [FromQuery(Name = "cancel_bookings")] bool cancelBookings, CancellationToken ct)
    {
        var result = await users.SetActiveAsync(userId, false, ct, cancelBookings);
        return result.CancelledSessions > 0 ? Ok(result) : NoContent();
    }

    [HttpPost("users/{userId:guid}/enable")]
    [Authorize(Policy = Permissions.UsersEdit)]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Enable(Guid userId, CancellationToken ct)
    {
        await users.SetActiveAsync(userId, true, ct);
        return NoContent();
    }

    /// <summary>كلمة مرور مؤقتة تُعرض مرة واحدة.</summary>
    [HttpPost("users/{userId:guid}/reset-password")]
    [Authorize(Policy = Permissions.UsersEdit)]
    [ProducesResponseType<ResetPasswordResponse>(200)]
    public Task<ResetPasswordResponse> ResetPassword(Guid userId, CancellationToken ct) => users.ResetPasswordAsync(userId, ct);

    // GET /roles انتقل إلى RolesController (نفس الشكل مع حقول إضافية)
}
