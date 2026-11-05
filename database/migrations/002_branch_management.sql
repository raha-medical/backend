-- =============================================================================
-- ترحيل 002: إدارة الفروع
--   * branches: موقع الفرع وأوقات الدوام (لواجهة الفرع في تطبيق المريض)
--   * branch_closures: إغلاق مؤقت للفرع كاملاً (صيانة، عطلة، افتتاح قريب...)
--   * سبب إغلاق جديد: افتتاح قريباً
-- يتطلب الترحيل 001. آمن على قاعدة فيها بيانات: إضافات فقط، ويمكن تشغيله أكثر من مرة.
-- التشغيل: psql "$DATABASE_URL" -f database/migrations/002_branch_management.sql
-- =============================================================================

BEGIN;

ALTER TABLE branches ADD COLUMN IF NOT EXISTS latitude      NUMERIC(9,6);
ALTER TABLE branches ADD COLUMN IF NOT EXISTS longitude     NUMERIC(9,6);
ALTER TABLE branches ADD COLUMN IF NOT EXISTS working_hours VARCHAR(255);

CREATE TABLE IF NOT EXISTS branch_closures (
    closure_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branch_id       UUID NOT NULL REFERENCES branches(branch_id),
    reason          VARCHAR(50) NOT NULL,                       -- lookup_values.code (category = closure_reason)
    note            TEXT,
    starts_on       DATE NOT NULL,                              -- بتوقيت بغداد
    ends_on         DATE,                                       -- NULL = حتى إعادة الفتح يدوياً
    created_by      UUID REFERENCES users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    ended_at        TIMESTAMPTZ,
    ended_by        UUID REFERENCES users(user_id),
    CHECK (ends_on IS NULL OR ends_on >= starts_on)
);
CREATE INDEX IF NOT EXISTS idx_branch_closures_branch ON branch_closures(branch_id, starts_on);

INSERT INTO lookup_values (category, code, name_ar, sort_order, is_system) VALUES
    ('closure_reason', 'opening_soon', 'افتتاح قريباً', 60, FALSE)
ON CONFLICT (category, code) DO NOTHING;

COMMIT;
