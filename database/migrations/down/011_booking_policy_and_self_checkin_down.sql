-- =============================================================================
-- تراجع الترحيل 011: يحذف أعمدة السياسة والحضور الذاتي من bookings والصلاحية bookings.policy_override،
-- ويعيد قيود الأنواع كما كانت. لا تُحذف أي فاتورة: بنود «رسوم إلغاء متأخر» تبقى كبنود 'procedure'
-- وخدمة الرسوم تصبح خدمة 'procedure' موقوفة (الكود القديم يعرضها كإجراء عادي ويحصّلها كما هي).
-- التشغيل: psql "$DATABASE_URL" -f database/migrations/down/011_booking_policy_and_self_checkin_down.sql
-- =============================================================================

BEGIN;

DELETE FROM role_permissions WHERE permission = 'bookings.policy_override';
DELETE FROM user_permissions WHERE permission = 'bookings.policy_override';

DROP INDEX IF EXISTS uq_services_fee_per_branch;
UPDATE invoice_items SET item_type = 'procedure' WHERE item_type = 'cancellation_fee';
UPDATE services SET type = 'procedure', is_active = FALSE WHERE type = 'fee';

ALTER TABLE invoice_items DROP CONSTRAINT IF EXISTS invoice_items_item_type_check;
ALTER TABLE invoice_items ADD CONSTRAINT invoice_items_item_type_check
    CHECK (item_type IN ('new_visit', 'follow_up', 'procedure', 'lab', 'radiology', 'pharmacy'));
ALTER TABLE services DROP CONSTRAINT IF EXISTS services_type_check;
ALTER TABLE services ADD CONSTRAINT services_type_check
    CHECK (type IN ('consultation', 'procedure', 'lab', 'radiology', 'pharmacy'));

ALTER TABLE bookings DROP CONSTRAINT IF EXISTS ck_bookings_reschedule_count;
ALTER TABLE bookings DROP CONSTRAINT IF EXISTS ck_bookings_check_in_method;
ALTER TABLE bookings DROP CONSTRAINT IF EXISTS ck_bookings_cancelled_by;
ALTER TABLE bookings DROP COLUMN IF EXISTS cancel_fee_waived;
ALTER TABLE bookings DROP COLUMN IF EXISTS cancel_fee_item_id;
ALTER TABLE bookings DROP COLUMN IF EXISTS cancelled_by;
ALTER TABLE bookings DROP COLUMN IF EXISTS check_in_method;
ALTER TABLE bookings DROP COLUMN IF EXISTS checked_in_at;
ALTER TABLE bookings DROP COLUMN IF EXISTS original_session_id;
ALTER TABLE bookings DROP COLUMN IF EXISTS last_rescheduled_at;
ALTER TABLE bookings DROP COLUMN IF EXISTS reschedule_count;

COMMIT;
