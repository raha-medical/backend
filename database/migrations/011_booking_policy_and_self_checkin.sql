-- =============================================================================
-- ترحيل 011: المرحلة 3 - تثبيت الحضور الذاتي (QR)، وسياسة الإلغاء وتغيير الموعد
--   * bookings: عدد مرات تغيير الموعد والوجبة الأصلية، وقت وطريقة تثبيت الحضور (reception | self_qr)،
--     من ألغى الحجز (patient | staff | system)، وبند رسوم الإلغاء المتأخر وهل أُعفي منه
--   * services.type يقبل 'fee' (خدمة نظام داخلية لكل فرع: «رسوم إلغاء متأخر»، لا تظهر في الكتالوج)
--   * invoice_items.item_type يقبل 'cancellation_fee'
--   * الصلاحية الجديدة bookings.policy_override في الكتالوج (كود فقط، بلا صفوف هنا): المدير العام يملكها دائماً،
--     ولا تُضاف لأي دور نظام تلقائياً؛ تُمنح من شاشة الأدوار أو كصلاحية إضافية لموظف
--   * إعدادات السياسة نفسها (booking.late_cancel_fee ...) قيمها الافتراضية في الكود، فلا صفوف هنا
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/011_booking_policy_and_self_checkin_down.sql
-- التشغيل: psql "$DATABASE_URL" -f database/migrations/011_booking_policy_and_self_checkin.sql
-- =============================================================================

BEGIN;

ALTER TABLE bookings ADD COLUMN IF NOT EXISTS reschedule_count     SMALLINT NOT NULL DEFAULT 0;
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS last_rescheduled_at  TIMESTAMPTZ;
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS original_session_id  UUID REFERENCES doctor_sessions(session_id);
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS checked_in_at        TIMESTAMPTZ;
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS check_in_method      VARCHAR(10);
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS cancelled_by         VARCHAR(10);
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS cancel_fee_item_id   UUID REFERENCES invoice_items(invoice_item_id);
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS cancel_fee_waived    BOOLEAN NOT NULL DEFAULT FALSE;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_bookings_reschedule_count') THEN
        ALTER TABLE bookings ADD CONSTRAINT ck_bookings_reschedule_count CHECK (reschedule_count >= 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_bookings_check_in_method') THEN
        ALTER TABLE bookings ADD CONSTRAINT ck_bookings_check_in_method CHECK (check_in_method IN ('reception', 'self_qr'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_bookings_cancelled_by') THEN
        ALTER TABLE bookings ADD CONSTRAINT ck_bookings_cancelled_by CHECK (cancelled_by IN ('patient', 'staff', 'system'));
    END IF;
END $$;

-- الحجوزات التي ثُبت حضورها قبل الترحيل: الاستقبال (الطريقة الوحيدة سابقاً)، والوقت من التذكرة
UPDATE bookings b SET check_in_method = 'reception', checked_in_at = t.arrived_at
  FROM queue_tickets t
 WHERE t.booking_id = b.booking_id AND b.check_in_method IS NULL AND t.arrived_at IS NOT NULL
   AND b.status IN ('checked_in', 'completed');

-- أنواع الخدمات وبنود الفاتورة: إضافة 'fee' و 'cancellation_fee'
ALTER TABLE services DROP CONSTRAINT IF EXISTS services_type_check;
ALTER TABLE services ADD CONSTRAINT services_type_check
    CHECK (type IN ('consultation', 'procedure', 'lab', 'radiology', 'pharmacy', 'fee'));
ALTER TABLE invoice_items DROP CONSTRAINT IF EXISTS invoice_items_item_type_check;
ALTER TABLE invoice_items ADD CONSTRAINT invoice_items_item_type_check
    CHECK (item_type IN ('new_visit', 'follow_up', 'procedure', 'lab', 'radiology', 'pharmacy', 'cancellation_fee'));

-- خدمة الرسوم واحدة لكل فرع
CREATE UNIQUE INDEX IF NOT EXISTS uq_services_fee_per_branch ON services(branch_id) WHERE type = 'fee';

COMMIT;
