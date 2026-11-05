using System.Text.Json.Nodes;
using AlRahma.Application.Abstractions;
using AlRahma.Application.Common;
using AlRahma.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AlRahma.Application.Reports;

public record AuditLogDto(long LogId, Guid? BranchId, string? BranchName, Guid? UserId, string? UserName, string Action, string EntityType,
    Guid? EntityId, JsonNode? Before, JsonNode? After, string? Reason, string? Ip, Guid? DeviceId, DateTimeOffset OccurredAt, bool SyncedOffline);
public record AuditLogQuery(Guid? BranchId, Guid? UserId, string? Action, string? EntityType, Guid? EntityId, DateOnly? From, DateOnly? To,
    int Page = 1, int PageSize = 50);

/// <summary>
/// سجل التدقيق (للقراءة فقط، لا تعديل ولا حذف). المدير العام يرى كل الفروع والعمليات العامة (بدون فرع)،
/// والمحاسب فروعه المسندة إليه فقط.
/// </summary>
public class AuditLogService(IAppDbContext db, ICurrentUser current, ReportScope scope)
{
    public async Task<PagedResult<AuditLogDto>> ListAsync(AuditLogQuery q, CancellationToken ct)
    {
        var period = scope.Period(q.From ?? q.To?.AddDays(-30) ?? scope.Today.AddDays(-30), q.To);
        var page = new PageQuery(q.Page, q.PageSize <= 0 ? 50 : q.PageSize);
        var branches = (await scope.BranchesAsync(q.BranchId, ct)).ToList();
        DateTimeOffset s = period.StartUtc, e = period.EndUtc;
        var query = db.AuditLogs.AsNoTracking().Where(l => l.OccurredAt >= s && l.OccurredAt < e);
        query = current.Role == Roles.Admin && q.BranchId is null
            ? query
            : query.Where(l => l.BranchId != null && branches.Contains(l.BranchId.Value));
        if (q.UserId is { } u) query = query.Where(l => l.UserId == u);
        if (!string.IsNullOrWhiteSpace(q.Action)) query = query.Where(l => l.Action == q.Action.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(q.EntityType)) query = query.Where(l => l.EntityType == q.EntityType.Trim());
        if (q.EntityId is { } eid) query = query.Where(l => l.EntityId == eid);

        var total = await query.CountAsync(ct);
        var rows = await (
            from l in query
            join usr in db.Users on l.UserId equals usr.UserId into uj
            from usr in uj.DefaultIfEmpty()
            join b in db.Branches on l.BranchId equals b.BranchId into bj
            from b in bj.DefaultIfEmpty()
            orderby l.OccurredAt descending, l.LogId descending
            select new { l, UserName = usr == null ? null : usr.FullName, BranchName = b == null ? null : b.Name })
            .Skip((page.SafePage - 1) * page.SafePageSize).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<AuditLogDto>(rows.Select(r => new AuditLogDto(r.l.LogId, r.l.BranchId, r.BranchName, r.l.UserId, r.UserName, r.l.Action,
            r.l.EntityType, r.l.EntityId, Parse(r.l.Before), Parse(r.l.After), r.l.Reason, r.l.IpAddress?.ToString(), r.l.DeviceId, r.l.OccurredAt,
            r.l.SyncedOffline)).ToList(), page.SafePage, page.SafePageSize, total);
    }

    private static JsonNode? Parse(string? json)
    {
        if (json is null) return null;
        try { return JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return JsonValue.Create(json); }
    }
}
