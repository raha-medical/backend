-- تراجع الترحيل 027: يحذف أعمدة «تأخر الطبيب». دور followup يبقى إن كان له مستخدمون (يُحذف فقط إن لم يُسند لأحد).
BEGIN;
ALTER TABLE doctor_sessions DROP CONSTRAINT IF EXISTS doctor_sessions_delay_minutes_check;
ALTER TABLE doctor_sessions DROP COLUMN IF EXISTS delay_announced_at;
ALTER TABLE doctor_sessions DROP COLUMN IF EXISTS delay_reason;
ALTER TABLE doctor_sessions DROP COLUMN IF EXISTS delay_minutes;
DELETE FROM role_permissions WHERE role_code = 'followup' AND NOT EXISTS (SELECT 1 FROM users WHERE role = 'followup');
DELETE FROM roles WHERE code = 'followup' AND NOT EXISTS (SELECT 1 FROM users WHERE role = 'followup');
COMMIT;
