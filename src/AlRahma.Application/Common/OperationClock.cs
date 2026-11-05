using AlRahma.Application.Abstractions;

namespace AlRahma.Application.Common;

/// <summary>
/// وقت العملية الجارية (لكل طلب). عادةً = الآن. عمليات الأوفلاين التي تصل عبر /sync/batch تُنفذ داخل
/// <see cref="Offline"/> فيصبح وقتها وقت حدوثها الفعلي (occurred_at): الوجبة تُختار به، وأوقات الوصول والاستدعاء
/// والمعاينة تُسجل به، فلا تفسد التقارير ومتوسط المعاينة (C2، M4).
/// </summary>
public sealed class OperationClock(IClock clock)
{
    public DateTimeOffset? OfflineAt { get; private set; }
    public bool IsOffline => OfflineAt is not null;
    public DateTimeOffset Now => OfflineAt ?? clock.UtcNow;
    public DateOnly Today => DateOnly.FromDateTime(Now.ToOffset(ClockExtensions.BaghdadOffset).DateTime);

    /// <summary>ينفذ ما بداخله بوقت حدوث عملية أوفلاين، ويعيد الوقت كما كان عند الخروج.</summary>
    public IDisposable Offline(DateTimeOffset occurredAt)
    {
        var previous = OfflineAt;
        // لا يُقبل وقت في المستقبل (ساعة جهاز خاطئة): يُعامل كالآن
        OfflineAt = occurredAt > clock.UtcNow ? clock.UtcNow : occurredAt;
        return new Restore(() => OfflineAt = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        private Action? _undo = undo;
        public void Dispose() { _undo?.Invoke(); _undo = null; }
    }
}
