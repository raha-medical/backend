-- =============================================================================
-- تراجع الترحيل 021. يحذف بيانات الدعم والأسئلة الشائعة وصور النتائج الخارجية.
-- =============================================================================

BEGIN;
DELETE FROM role_permissions WHERE permission = 'support.manage';
DELETE FROM user_permissions WHERE permission = 'support.manage';

DELETE FROM patient_credit_ledger WHERE kind IN ('booking_rebook', 'left_before_entry');
ALTER TABLE patient_credit_ledger DROP CONSTRAINT IF EXISTS ck_credit_kind;
ALTER TABLE patient_credit_ledger ADD CONSTRAINT ck_credit_kind CHECK (kind IN (
    'unban_deposit', 'booking_deposit', 'booking_cancel_credit', 'refund_credit', 'applied_to_payment', 'refunded_out'));

ALTER TABLE bookings DROP CONSTRAINT IF EXISTS ck_bookings_cancel_resolution;
ALTER TABLE bookings DROP COLUMN IF EXISTS rebooked_booking_id;
ALTER TABLE bookings DROP COLUMN IF EXISTS cancel_resolution;

DROP TABLE IF EXISTS support_requests;
DROP TABLE IF EXISTS faq_items;
ALTER TABLE branches DROP COLUMN IF EXISTS whatsapp;

DELETE FROM files WHERE purpose IN ('external_result', 'prescription');
ALTER TABLE files DROP CONSTRAINT IF EXISTS ck_files_purpose;
ALTER TABLE files ADD CONSTRAINT ck_files_purpose CHECK (purpose IN ('lab', 'radiology', 'doctor_photo'));
DROP INDEX IF EXISTS idx_files_patient;
DROP INDEX IF EXISTS idx_files_ticket;
ALTER TABLE files DROP COLUMN IF EXISTS patient_id;
ALTER TABLE files DROP COLUMN IF EXISTS ticket_id;
COMMIT;
