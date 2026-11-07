-- =============================================================================
-- ترحيل 029: «حضر» من الاستقبال
--   queue_tickets.call_ack_by: من أكّد حضور المستدعى: patient (ضغط «تم، أنا قادم» في التطبيق) | staff (زر «حضر» في الاستقبال).
--   queue_tickets.call_ack_staff_id: الموظف الذي ضغط «حضر».
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/029_call_ack_by_staff_down.sql
-- =============================================================================

BEGIN;

ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS call_ack_by       TEXT;
ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS call_ack_staff_id UUID;
ALTER TABLE queue_tickets DROP CONSTRAINT IF EXISTS queue_tickets_call_ack_by_check;
ALTER TABLE queue_tickets ADD CONSTRAINT queue_tickets_call_ack_by_check CHECK (call_ack_by IS NULL OR call_ack_by IN ('patient', 'staff'));
UPDATE queue_tickets SET call_ack_by = 'patient' WHERE call_acknowledged_at IS NOT NULL AND call_ack_by IS NULL;

COMMIT;
