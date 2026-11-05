-- =============================================================================
-- ترحيل 008: تذكيرات الحجز الإضافية (المرحلة 2ب) وإعدادات كل طبيب (المرحلة 3)
--   * bookings.day_before_sent_at: تذكير «موعدك غداً» مرة واحدة
--   * bookings.soon_sent_at: تذكير «موعدك بعد 20 دقيقة تقريباً» مرة واحدة
--   (تذكير الساعة موجود: reminder_sent_at)
--   * doctors: مدة المراجعة وسعرها (مجانية = 0) والعربون الإلكتروني لكل طبيب (فارغ = إعداد الفرع، 0 = لا عربون)
-- إضافات فقط، ويمكن تشغيله أكثر من مرة.
-- =============================================================================

BEGIN;
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS day_before_sent_at TIMESTAMPTZ;
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS soon_sent_at       TIMESTAMPTZ;
ALTER TABLE doctors ADD COLUMN IF NOT EXISTS follow_up_days        SMALLINT CHECK (follow_up_days IS NULL OR follow_up_days BETWEEN 0 AND 180);
ALTER TABLE doctors ADD COLUMN IF NOT EXISTS follow_up_price       BIGINT   CHECK (follow_up_price IS NULL OR follow_up_price >= 0);
ALTER TABLE doctors ADD COLUMN IF NOT EXISTS online_deposit_amount BIGINT   CHECK (online_deposit_amount IS NULL OR online_deposit_amount >= 0);
COMMIT;
