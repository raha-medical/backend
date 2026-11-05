-- =============================================================================
-- ترحيل 004: الإدارة والمال
--   * expenses: مصاريف الفروع، من درج الوردية (ينقص من المتوقع) أو من صندوق الفرع
--   * تصنيفات المصاريف في lookup_values (category = expense_category)
--   * فهارس للتقارير
-- يتطلب الترحيلات 001-003. آمن على قاعدة فيها بيانات: إضافات فقط، ويمكن تشغيله أكثر من مرة.
-- التشغيل: psql "$DATABASE_URL" -f database/migrations/004_admin_finance.sql
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS expenses (
    expense_id        UUID PRIMARY KEY,
    expense_number    VARCHAR(30) NOT NULL UNIQUE,              -- مثل B1-EXP-000123
    branch_id         UUID NOT NULL REFERENCES branches(branch_id),
    category          VARCHAR(50) NOT NULL,                     -- lookup_values.code (category = expense_category)
    amount            BIGINT NOT NULL CHECK (amount > 0),
    expense_date      DATE NOT NULL,                            -- بتوقيت بغداد
    description       TEXT NOT NULL,
    payee             VARCHAR(150),
    paid_from         VARCHAR(20) NOT NULL CHECK (paid_from IN ('shift_cash', 'treasury')),
    shift_id          UUID REFERENCES shifts(shift_id),         -- نقد من درج هذه الوردية
    status            VARCHAR(20) NOT NULL DEFAULT 'recorded' CHECK (status IN ('recorded', 'void')),
    created_by        UUID NOT NULL REFERENCES users(user_id),
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    voided_by         UUID REFERENCES users(user_id),
    voided_at         TIMESTAMPTZ,
    void_reason       TEXT,
    CHECK ((paid_from = 'shift_cash') = (shift_id IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS idx_expenses_branch_date ON expenses(branch_id, expense_date);
CREATE INDEX IF NOT EXISTS idx_expenses_shift ON expenses(shift_id) WHERE shift_id IS NOT NULL;

INSERT INTO lookup_values (category, code, name_ar, sort_order, is_system) VALUES
    ('expense_category', 'salaries', 'رواتب وأجور', 10, FALSE),
    ('expense_category', 'rent', 'إيجار', 20, FALSE),
    ('expense_category', 'utilities', 'كهرباء ومولدة وماء', 30, FALSE),
    ('expense_category', 'maintenance', 'صيانة', 40, FALSE),
    ('expense_category', 'medical_supplies', 'مستلزمات طبية', 50, FALSE),
    ('expense_category', 'lab_supplies', 'مواد مختبر', 60, FALSE),
    ('expense_category', 'office_supplies', 'قرطاسية ومستلزمات مكتبية', 70, FALSE),
    ('expense_category', 'cleaning', 'تنظيف وضيافة', 80, FALSE),
    ('expense_category', 'fuel', 'وقود', 90, FALSE),
    ('expense_category', 'other', 'أخرى', 999, TRUE)
ON CONFLICT (category, code) DO NOTHING;

CREATE INDEX IF NOT EXISTS idx_payments_branch_created ON payments(branch_id, created_at);
CREATE INDEX IF NOT EXISTS idx_refunds_decided ON refunds(decided_at) WHERE status = 'completed';

COMMIT;
