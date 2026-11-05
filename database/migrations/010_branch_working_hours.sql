-- =============================================================================
-- ترحيل 010: أوقات الدوام المهيكلة للفروع (شاشة الفروع والخريطة)
--   * branch_working_hours: فترة أو فترتان لكل يوم (0 = الأحد ... 6 = السبت، بتوقيت بغداد)، واليوم بلا صفوف مغلق.
--     الفرع بلا أي صف = لم يُضبط جدول مهيكل (يبقى النص القديم branches.working_hours للعرض).
--   * فحص مدى الإحداثيات (latitude/longitude) على مستوى القاعدة أيضاً.
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/010_branch_working_hours_down.sql
-- التشغيل: psql "$DATABASE_URL" -f database/migrations/010_branch_working_hours.sql
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS branch_working_hours (
    branch_id       UUID NOT NULL REFERENCES branches(branch_id) ON DELETE CASCADE,
    weekday         SMALLINT NOT NULL CHECK (weekday BETWEEN 0 AND 6),     -- 0 = الأحد (مثل جداول الأطباء)
    shift_no        SMALLINT NOT NULL CHECK (shift_no BETWEEN 1 AND 2),    -- فترة صباحية ومسائية
    opens_at        TIME NOT NULL,
    closes_at       TIME NOT NULL,
    PRIMARY KEY (branch_id, weekday, shift_no),
    CHECK (closes_at > opens_at)
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_branches_location') THEN
        ALTER TABLE branches ADD CONSTRAINT ck_branches_location CHECK (
            (latitude IS NULL AND longitude IS NULL)
            OR (latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180));
    END IF;
END $$;

COMMIT;
