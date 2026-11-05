using AlRahma.Api.Infrastructure;
using AlRahma.Application.Abstractions;
using Microsoft.AspNetCore.Authentication;
using AlRahma.Application.Common;
using AlRahma.Application.Devices;
using AlRahma.Domain.Common;
using AlRahma.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AlRahma.Api.Realtime;

/// <summary>
/// قناة التحديث المباشر /hubs/live. الموظف يتصل بتوكنه (?access_token=) ويُضاف تلقائياً لمجموعات فرعه وطابوره.
/// شاشة الانتظار تتصل بدون توكن ثم تستدعي JoinDisplay بمعرفها ومفتاحها.
/// </summary>
[AllowAnonymous]
public class LiveHub(DeviceService devices) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        if (user?.Identity?.IsAuthenticated != true && Context.GetHttpContext() is { } http)
        {
            // توكن تطبيق المريض (مخطط Patient): مجموعة حسابه فقط
            var patient = await http.AuthenticateAsync(AuthSchemes.Patient);
            if (patient.Succeeded && patient.Principal.FindFirst(AppClaims.Role)?.Value == Roles.Patient
                && Guid.TryParse(patient.Principal.FindFirst("sub")?.Value, out var accountId))
                await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.PatientAccount(accountId));
        }
        else if (user?.Identity?.IsAuthenticated == true)
        {
            var branch = user.FindFirst(AppClaims.BranchId)?.Value;
            var sub = user.FindFirst("sub")?.Value;
            var role = user.FindFirst(AppClaims.Role)?.Value;
            if (Guid.TryParse(branch, out var branchId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.BranchQueue(branchId));
                if (user.FindAll(AppClaims.Permission).Any(c => c.Value is Permissions.ShiftsReview or Permissions.RefundsApprove))
                    await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.BranchFinance(branchId));
                if (user.FindAll(AppClaims.Permission).Any(c => c.Value is Permissions.LabWrite or Permissions.LabView))
                    await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.BranchDepartment(branchId, "lab"));
                if (user.FindAll(AppClaims.Permission).Any(c => c.Value is Permissions.RadiologyWrite or Permissions.RadiologyView))
                    await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.BranchDepartment(branchId, "radiology"));
                if (user.FindAll(AppClaims.Permission).Any(c => c.Value is Permissions.PharmacyDispense or Permissions.PharmacyView))
                    await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.BranchDepartment(branchId, "pharmacy"));
            }
            if (Guid.TryParse(sub, out var userId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.User(userId));
                if (role == Roles.Doctor)
                {
                    var doctorId = await DoctorIdAsync(userId);
                    if (doctorId is { } did) await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Doctor(did));
                }
            }
        }
        await base.OnConnectedAsync();
    }

    /// <summary>للشاشات العامة: الاشتراك في أحداث شاشة واحدة (ticket.called) بعد التحقق من مفتاحها.</summary>
    public async Task<bool> JoinDisplay(Guid displayId, string displayKey)
    {
        var device = await devices.AuthenticateDisplayAsync(displayId, displayKey, Context.ConnectionAborted);
        if (device is null) return false;
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Display(device.DeviceId));
        return true;
    }

    private async Task<Guid?> DoctorIdAsync(Guid userId)
    {
        var db = Context.GetHttpContext()?.RequestServices.GetRequiredService<IAppDbContext>();
        if (db is null) return null;
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.Doctors.Where(d => d.UserId == userId).Select(d => (Guid?)d.DoctorId));
    }
}

public class SignalRRealtime(IHubContext<LiveHub> hub, ILogger<SignalRRealtime> log) : IRealtime
{
    public async Task SendAsync(string group, string eventName, object payload)
    {
        try
        {
            await hub.Clients.Group(group).SendAsync(eventName, payload);
        }
        catch (Exception e)
        {
            // فشل البث لا يُفشل العملية؛ الواجهة تعيد التحميل عند إعادة الاتصال
            log.LogWarning(e, "Realtime send failed for {Group} {Event}", group, eventName);
        }
    }
}
