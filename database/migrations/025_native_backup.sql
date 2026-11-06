-- =============================================================================
-- ترحيل 025: النسخ الاحتياطي الأصلي على Windows بدون Docker (Backup__Mode=native)
--   * backup_runs.path: مجلد النسخة (قاعدة البيانات + ملفات التخزين + مفتاح توقيع الإيصالات)
--   * backup_runs.files_bytes: حجم نسخة ملفات التخزين داخل المجلد
--   * backup_runs.offsite_status: off (لا قرص خارجي) | pending (بانتظار القرص الخارجي) | copied | failed
--   * backup_runs.offsite_path / offsite_at: مكان النسخة على القرص الخارجي ووقت وصولها
--   * backup_runs.deleted_at: حُذف المجلد بسياسة الاحتفاظ (آخر 14 نسخة)، ويبقى السجل للتاريخ
--   وقت النسخ اليومي في system_settings بالمفتاح backup.daily_time (مثل "03:00" بتوقيت بغداد)،
--   والقيمة غير الموجودة = 03:00.
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/025_native_backup_down.sql
-- =============================================================================

BEGIN;

ALTER TABLE backup_runs ADD COLUMN IF NOT EXISTS path           TEXT;
ALTER TABLE backup_runs ADD COLUMN IF NOT EXISTS files_bytes    BIGINT;
ALTER TABLE backup_runs ADD COLUMN IF NOT EXISTS offsite_status VARCHAR(10);
ALTER TABLE backup_runs ADD COLUMN IF NOT EXISTS offsite_path   TEXT;
ALTER TABLE backup_runs ADD COLUMN IF NOT EXISTS offsite_at     TIMESTAMPTZ;
ALTER TABLE backup_runs ADD COLUMN IF NOT EXISTS deleted_at     TIMESTAMPTZ;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_backup_runs_offsite_status') THEN
        ALTER TABLE backup_runs ADD CONSTRAINT ck_backup_runs_offsite_status
            CHECK (offsite_status IS NULL OR offsite_status IN ('off', 'pending', 'copied', 'failed'));
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS idx_backup_runs_offsite_pending ON backup_runs(created_at) WHERE offsite_status = 'pending';

COMMIT;
