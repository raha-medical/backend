-- =============================================================================
-- ترحيل 007: يوم العيادة المرن (المرحلة 2أ)
--   * doctor_sessions: وصول الطبيب الفعلي، تمديد نهاية اليوم، إغلاق التسجيل
--   * queue_tickets: المراجع «الإضافي» فوق السعة ومن وافق عليه
--   * doctors: «ممنوع الإضافي» ومهلة المتأخر لكل طبيب (فارغة = إعداد الفرع queue.no_show_grace_minutes)
-- إضافات فقط، لا يغير أي بيانات موجودة، ويمكن تشغيله أكثر من مرة.
-- التشغيل: psql "$DATABASE_URL" -f database/migrations/007_flexible_clinic_day.sql
-- =============================================================================

BEGIN;

ALTER TABLE doctor_sessions ADD COLUMN IF NOT EXISTS doctor_arrived_at      TIMESTAMPTZ;
ALTER TABLE doctor_sessions ADD COLUMN IF NOT EXISTS extended_end_time      TIME;
ALTER TABLE doctor_sessions ADD COLUMN IF NOT EXISTS registration_closed_at TIMESTAMPTZ;

ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS is_extra          BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS extra_approved_by UUID REFERENCES users(user_id);

ALTER TABLE doctors ADD COLUMN IF NOT EXISTS allow_extra        BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE doctors ADD COLUMN IF NOT EXISTS late_grace_minutes SMALLINT CHECK (late_grace_minutes IS NULL OR late_grace_minutes BETWEEN 0 AND 180);

COMMIT;
