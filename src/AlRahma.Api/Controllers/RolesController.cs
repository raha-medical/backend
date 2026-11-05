using AlRahma.Api.Infrastructure;
using AlRahma.Application.Security;
using AlRahma.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlRahma.Api.Controllers;

/// <summary>الأدوار والصلاحيات (القسم 2.1 من العقد). الإدارة للمدير العام فقط.</summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize]
public class RolesController(RoleService roles) : ControllerBase
{
    /// <summary>كتالوج الصلاحيات مقسماً حسب القسم والإجراء (لمصفوفة الصلاحيات في الواجهة).</summary>
    [HttpGet("permissions/catalog")]
    [Authorize(Policy = Policies.RolesRead)]
    [ProducesResponseType<PermissionCatalogDto>(200)]
    public PermissionCatalogDto Catalog() => RoleService.Catalog();

    /// <summary>الأدوار مع صلاحياتها وعدد موظفيها. include_inactive يظهر الأدوار المخصصة الموقوفة.</summary>
    [HttpGet("roles")]
    [Authorize(Policy = Policies.RolesRead)]
    public async Task<IActionResult> List([FromQuery(Name = "include_inactive")] bool includeInactive, CancellationToken ct) =>
        Ok(new { items = await roles.ListAsync(includeInactive, ct) });

    [HttpGet("roles/{code}")]
    [Authorize(Policy = Policies.RolesRead)]
    [ProducesResponseType<RoleDto>(200)]
    public Task<RoleDto> Get(string code, CancellationToken ct) => roles.GetAsync(code, ct);

    [HttpPost("roles")]
    [Authorize(Policy = Permissions.RolesCreate)]
    [Idempotent]
    [ProducesResponseType<RoleDto>(201)]
    public async Task<IActionResult> Create(CreateRoleRequest req, CancellationToken ct)
    {
        var role = await roles.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { code = role.Code }, role);
    }

    [HttpPatch("roles/{code}")]
    [Authorize(Policy = Permissions.RolesEdit)]
    [ProducesResponseType<RoleDto>(200)]
    public Task<RoleDto> Update(string code, UpdateRoleRequest req, CancellationToken ct) => roles.UpdateAsync(code, req, ct);

    /// <summary>حذف دور مخصص لا موظفين عليه. أدوار النظام لا تُحذف.</summary>
    [HttpDelete("roles/{code}")]
    [Authorize(Policy = Permissions.RolesDelete)]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Delete(string code, CancellationToken ct)
    {
        await roles.DeleteAsync(code, ct);
        return NoContent();
    }
}
