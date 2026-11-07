-- =============================================================================
-- ترحيل 027: إصلاحات الإشعارات
--   * دور «المتابعة والإشعارات» (followup): إدارة طلبات الدعم (support.manage) ومتابعة إشعارات الفرع
--     (notifications.branch_view). كان في بيانات التطوير فقط، فصار في كل قاعدة جديدة أو قائمة.
--   * «تأخر الطبيب» المعلن من الاستقبال أو الإدارة لوجبة اليوم: doctor_sessions.delay_minutes / delay_reason / delay_announced_at.
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/027_notifications_followup_delay_down.sql
-- =============================================================================

BEGIN;

INSERT INTO roles (code, name_ar, description, home_section, is_system) VALUES
    ('followup', 'موظف المتابعة والإشعارات', 'متابعة طلبات الدعم والرد عليها، ومتابعة إشعارات الفرع', NULL, FALSE)
ON CONFLICT (code) DO NOTHING;

INSERT INTO role_permissions (role_code, permission) VALUES
    ('followup', 'support.manage'), ('followup', 'notifications.branch_view')
ON CONFLICT (role_code, permission) DO NOTHING;

ALTER TABLE doctor_sessions ADD COLUMN IF NOT EXISTS delay_minutes      SMALLINT NOT NULL DEFAULT 0;
ALTER TABLE doctor_sessions ADD COLUMN IF NOT EXISTS delay_reason       TEXT;
ALTER TABLE doctor_sessions ADD COLUMN IF NOT EXISTS delay_announced_at TIMESTAMPTZ;
ALTER TABLE doctor_sessions DROP CONSTRAINT IF EXISTS doctor_sessions_delay_minutes_check;
ALTER TABLE doctor_sessions ADD CONSTRAINT doctor_sessions_delay_minutes_check CHECK (delay_minutes BETWEEN 0 AND 240);

COMMIT;
