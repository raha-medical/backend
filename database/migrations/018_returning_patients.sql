-- =============================================================================
-- ترحيل 018: المراجع العائد بالنتائج (التدقيق 1.2، 1.3-د، 2.3-ج)
--   * حالة دور جديدة awaiting_results (بانتظار المختبر / الأشعة / السونار / الصيدلية): خارج الترتيب.
--   * أولوية جديدة returning: بعد الطوارئ وقبل المحجوزين والمباشرين، بلا فاتورة جديدة.
--   * أعمدة: awaiting_since، returned_at، free_return_until، original_ticket_id (عودة في وجبة أخرى).
--   * مدة العودة المجانية: doctors.free_return_days ثم clinics.free_return_days ثم الإعداد queue.free_return_days (7).
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/018_returning_patients_down.sql
-- =============================================================================

BEGIN;

ALTER TABLE queue_tickets DROP CONSTRAINT IF EXISTS queue_tickets_status_check;
ALTER TABLE queue_tickets ADD CONSTRAINT queue_tickets_status_check CHECK (status IN (
    'waiting', 'arrived', 'called', 'in_room', 'completed', 'skipped', 'no_show', 'cancelled', 'awaiting_results'));
ALTER TABLE queue_tickets DROP CONSTRAINT IF EXISTS queue_tickets_priority_check;
ALTER TABLE queue_tickets ADD CONSTRAINT queue_tickets_priority_check CHECK (priority IN ('normal', 'emergency', 'late_reinserted', 'returning'));

ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS awaiting_since TIMESTAMPTZ;
ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS returned_at TIMESTAMPTZ;
ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS free_return_until TIMESTAMPTZ;
ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS original_ticket_id UUID REFERENCES queue_tickets(ticket_id);

CREATE INDEX IF NOT EXISTS idx_queue_free_return ON queue_tickets(patient_id, doctor_id, free_return_until) WHERE free_return_until IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_queue_awaiting ON queue_tickets(patient_id, doctor_id) WHERE status = 'awaiting_results';

ALTER TABLE doctors ADD COLUMN IF NOT EXISTS free_return_days SMALLINT CHECK (free_return_days IS NULL OR free_return_days BETWEEN 0 AND 90);
ALTER TABLE clinics ADD COLUMN IF NOT EXISTS free_return_days SMALLINT CHECK (free_return_days IS NULL OR free_return_days BETWEEN 0 AND 90);

INSERT INTO system_settings (branch_id, key, value)
SELECT NULL, 'queue.free_return_days', '7'
 WHERE NOT EXISTS (SELECT 1 FROM system_settings WHERE branch_id IS NULL AND key = 'queue.free_return_days');

COMMIT;
