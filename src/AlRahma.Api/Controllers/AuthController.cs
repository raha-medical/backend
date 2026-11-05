using AlRahma.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public class AuthController(AuthService auth) : ControllerBase
{
    /// <summary>تسجيل دخول الموظف.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<AuthResponse>(200)]
    public Task<AuthResponse> Login(LoginRequest req, CancellationToken ct) => auth.LoginAsync(req, ct);

    /// <summary>تجديد التوكن (الـ refresh القديم يُلغى).</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType<AuthResponse>(200)]
    public Task<AuthResponse> Refresh(RefreshRequest req, CancellationToken ct) => auth.RefreshAsync(req, ct);

    /// <summary>تسجيل الخروج: إلغاء جلسة هذا التوكن.</summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(ct);
        return NoContent();
    }

    [HttpPost("switch-branch")]
    [Authorize]
    [ProducesResponseType<AuthResponse>(200)]
    public Task<AuthResponse> SwitchBranch(SwitchBranchRequest req, CancellationToken ct) => auth.SwitchBranchAsync(req, ct);

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<AuthUserDto>(200)]
    public Task<AuthUserDto> Me(CancellationToken ct) => auth.MeAsync(ct);

    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType(204)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest req, CancellationToken ct)
    {
        await auth.ChangePasswordAsync(req, ct);
        return NoContent();
    }
}
