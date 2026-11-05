using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AlRahma.Application.Abstractions;
using AlRahma.Infrastructure.Security;

namespace AlRahma.Api.Infrastructure;

public class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid UserId => Guid.TryParse(Find(JwtRegisteredClaimNames.Sub) ?? Find(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    public string Role => Find(AppClaims.Role) ?? "";

    public Guid BranchId => Guid.TryParse(Find(AppClaims.BranchId), out var id) ? id : Guid.Empty;

    public Guid? SessionId => Guid.TryParse(Find(AppClaims.SessionId), out var id) ? id : null;

    public IReadOnlySet<string> Permissions =>
        Principal?.FindAll(AppClaims.Permission).Select(c => c.Value).ToHashSet() ?? [];

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    private string? Find(string type) => Principal?.FindFirst(type)?.Value;
}
