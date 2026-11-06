-- تراجع الترحيل 025: يحذف أعمدة النسخ الأصلي ووقت النسخ اليومي. مجلدات النسخ على القرص لا تُحذف.
BEGIN;
DROP INDEX IF EXISTS idx_backup_runs_offsite_pending;
ALTER TABLE backup_runs DROP CONSTRAINT IF EXISTS ck_backup_runs_offsite_status;
ALTER TABLE backup_runs DROP COLUMN IF EXISTS deleted_at;
ALTER TABLE backup_runs DROP COLUMN IF EXISTS offsite_at;
ALTER TABLE backup_runs DROP COLUMN IF EXISTS offsite_path;
ALTER TABLE backup_runs DROP COLUMN IF EXISTS offsite_status;
ALTER TABLE backup_runs DROP COLUMN IF EXISTS files_bytes;
ALTER TABLE backup_runs DROP COLUMN IF EXISTS path;
DELETE FROM system_settings WHERE branch_id IS NULL AND key = 'backup.daily_time';
COMMIT;
