using System.Text.Json;
using AlRahma.Application.Abstractions;
using AlRahma.Domain.Entities;
using AlRahma.Infrastructure.Persistence;

namespace AlRahma.Infrastructure.Services;

public class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public class AuditLogger(AppDbContext db, ICurrentUser current, IClock clock) : IAuditLogger
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public void Add(string action, string entityType, Guid? entityId, object? before = null, object? after = null, string? reason = null)
    {
        System.Net.IPAddress.TryParse(current.IpAddress, out var ip);
        db.AuditLogs.Add(new AuditLog
        {
            // المريض والطلبات العامة (بوابة الدفع) بلا موظف ولا فرع
            BranchId = current.StaffUserId is not null && current.BranchId != Guid.Empty ? current.BranchId : null,
            UserId = current.StaffUserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Before = before is null ? null : JsonSerializer.Serialize(before, Json),
            After = after is null ? null : JsonSerializer.Serialize(after, Json),
            Reason = reason,
            IpAddress = ip,
            OccurredAt = clock.UtcNow,
        });
    }
}
