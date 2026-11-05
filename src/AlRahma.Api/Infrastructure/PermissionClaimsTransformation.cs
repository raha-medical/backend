using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Security;
using AlRahma.Domain.Common;
using AlRahma.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;

namespace AlRahma.Api.Infrastructure;

/// <summary>
/// صلاحيات الموظف تُحسب من قاعدة البيانات في كل طلب (من ذاكرة مؤقتة)، لا من التوكن فقط: أي تعديل على دور أو موظف
/// يسري فوراً دون انتظار انتهاء التوكن. التوكن يبقى صالحاً كما هو (role و perm للواجهة)، والسيرفر يستبدل
/// perm و role بالقيم الحالية. موظف معطل أو أُزيل من فرع التوكن: الطلب غير مصادق (401).
/// </summary>
public class PermissionClaimsTransformation(IAppDbContext db, AccessCache cache) : IClaimsTransformation
{
    private const string Marker = "perm_source";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity) return principal;
        if (principal.HasClaim(c => c.Type == Marker)) return principal;
        var role = identity.FindFirst(AppClaims.Role)?.Value;
        if (role is null || role == Roles.Patient) return principal;
        if (!Guid.TryParse(identity.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId)) return principal;
        Guid.TryParse(identity.FindFirst(AppClaims.BranchId)?.Value, out var branchId);

        // المعطل أو المُزال من فرع التوكن يُرفض قبل هذا في OnTokenValidated (401)؛ هنا احتياط فقط
        var access = await cache.UserAsync(db, userId);
        if (access is null || !access.IsActive) return Anonymous();
        if (access.Role != Roles.Admin && !access.BranchIds.Contains(branchId)) return Anonymous();

        var roles = await cache.RolesAsync(db);
        var perms = roles.Effective(access.Role, access.Extras);

        var claims = identity.Claims.Where(c => c.Type != AppClaims.Permission && c.Type != AppClaims.Role).ToList();
        claims.Add(new Claim(AppClaims.Role, access.Role));
        claims.AddRange(perms.Select(p => new Claim(AppClaims.Permission, p)));
        claims.Add(new Claim(Marker, "db"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType));
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());
}
