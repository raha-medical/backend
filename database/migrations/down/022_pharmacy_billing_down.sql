-- =============================================================================
-- تراجع الترحيل 022. يحذف سجل صرف الصيدلية (بنود الفواتير تبقى).
-- =============================================================================

BEGIN;
DELETE FROM role_permissions WHERE role_code = 'pharmacist' AND permission = 'payments.collect';
DROP TABLE IF EXISTS pharmacy_dispense_lines;
ALTER TABLE prescription_lines DROP COLUMN IF EXISTS invoice_item_id;
ALTER TABLE prescription_lines DROP COLUMN IF EXISTS unit_price;
ALTER TABLE prescription_lines DROP COLUMN IF EXISTS quantity;
ALTER TABLE prescription_lines DROP COLUMN IF EXISTS service_id;
ALTER TABLE services DROP COLUMN IF EXISTS requires_prescription;
COMMIT;
