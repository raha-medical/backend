-- =============================================================================
-- ترحيل 006: النسخ الاحتياطي المجاني وإكمال إشعارات المرضى
--   * backup_runs: سجل عمليات النسخ الاحتياطي (pgBackRest) وطلبات التشغيل اليدوي من لوحة المدير
--   * queue_tickets.turn_soon_notified_at: إشعار "اقترب دورك" يُرسل مرة واحدة لكل تذكرة
--   * تنبيه الاستدعاء المستمر: تأكيد المريض "أنا قادم" وعدد التنبيهات المكررة
--   * دعوة الحضور المبكر (fast_forward_offers.mode = invite): المريض يؤكد أنه في الطريق ووقت وصوله
--   * outbound_messages.data: بيانات Push (النوع والرابط والصوت والأولوية)
--   * إعدادات جديدة: وقت التذكير قبل الموعد، دقائق الحضور المبكر، عدد المرضى قبل إشعار "اقترب دورك"،
--     استدعاء المريض التالي تلقائياً عند إنهاء المعاينة (مطفأ افتراضياً)
-- يتطلب الترحيلات 001-005. لا يغير أي بيانات موجودة، ويمكن تشغيله أكثر من مرة.
-- التشغيل: psql "$DATABASE_URL" -f database/migrations/006_backup_and_notifications.sql
-- =============================================================================

BEGIN;

ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS turn_soon_notified_at TIMESTAMPTZ;
ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS call_acknowledged_at  TIMESTAMPTZ;
ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS call_alerts_sent      SMALLINT NOT NULL DEFAULT 0;
ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS last_call_alert_at    TIMESTAMPTZ;
ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS on_the_way_at         TIMESTAMPTZ;
ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS on_the_way_eta_minutes SMALLINT;

ALTER TABLE fast_forward_offers ADD COLUMN IF NOT EXISTS mode VARCHAR(10) NOT NULL DEFAULT 'gap';
ALTER TABLE fast_forward_offers DROP CONSTRAINT IF EXISTS fast_forward_offers_mode_check;
ALTER TABLE fast_forward_offers ADD CONSTRAINT fast_forward_offers_mode_check CHECK (mode IN ('gap', 'invite'));
ALTER TABLE fast_forward_offers ALTER COLUMN gap_minutes DROP NOT NULL;
ALTER TABLE fast_forward_offer_recipients ADD COLUMN IF NOT EXISTS eta_minutes SMALLINT;

ALTER TABLE outbound_messages ADD COLUMN IF NOT EXISTS data JSONB;

CREATE TABLE IF NOT EXISTS backup_runs (
    run_id            UUID PRIMARY KEY,
    type              VARCHAR(10) NOT NULL CHECK (type IN ('full', 'diff', 'incr', 'verify', 'files')),
    trigger           VARCHAR(10) NOT NULL DEFAULT 'schedule' CHECK (trigger IN ('schedule', 'manual')),
    status            VARCHAR(10) NOT NULL CHECK (status IN ('requested', 'running', 'succeeded', 'failed')),
    requested_by      UUID REFERENCES users(user_id),
    started_at        TIMESTAMPTZ,
    finished_at       TIMESTAMPTZ,
    backup_label      VARCHAR(100),                           -- اسم النسخة في pgBackRest
    size_bytes        BIGINT,                                 -- حجم قاعدة البيانات قبل الضغط
    stored_bytes      BIGINT,                                 -- الحجم المخزن بعد الضغط
    repos             VARCHAR(50),                            -- المستودعات التي وصلتها النسخة: 1 أو 1,2
    error             TEXT,
    alerted_at        TIMESTAMPTZ,                            -- وقت تنبيه المدير بالفشل
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_backup_runs_created ON backup_runs(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_backup_runs_pending ON backup_runs(status) WHERE status IN ('requested', 'running');

INSERT INTO system_settings (branch_id, key, value)
SELECT NULL, v.key, v.value::jsonb
  FROM (VALUES ('booking.reminder_minutes', '60'),
               ('booking.arrive_early_minutes', '20'),
               ('queue.turn_soon_ahead', '3'),
               ('queue.auto_call_next', '0'),
               ('queue.call_alert_repeats', '3'),
               ('queue.call_next_by', '"both"')) AS v(key, value)
 WHERE NOT EXISTS (SELECT 1 FROM system_settings s WHERE s.branch_id IS NULL AND s.key = v.key);

COMMIT;
