namespace AlRahma.Domain.Entities;

public class IdempotencyRecord
{
    public Guid IdempotencyKey { get; set; }
    public string Scope { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public short? ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public class AuditLog
{
    public long LogId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public Guid? EntityId { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public string? Reason { get; set; }
    public System.Net.IPAddress? IpAddress { get; set; }
    public Guid? DeviceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public bool SyncedOffline { get; set; }
}
