-- تراجع الترحيل 029: يحذف عمودي «حضر» من الاستقبال.
BEGIN;
ALTER TABLE queue_tickets DROP CONSTRAINT IF EXISTS queue_tickets_call_ack_by_check;
ALTER TABLE queue_tickets DROP COLUMN IF EXISTS call_ack_staff_id;
ALTER TABLE queue_tickets DROP COLUMN IF EXISTS call_ack_by;
COMMIT;
