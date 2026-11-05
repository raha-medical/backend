-- =============================================================================
-- ترحيل 015: المرحلة 6 - أنظمة أجور الكوادر وسجل الصرف.
--   * staff_pay_plans: نظام الأجر لكل موظف في فرعه: نسبة، أجر ثابت شهري، أو لكل حالة.
--   * payouts: عمليات صرف المستحقات. تُسجل ولا تُحذف، ومعها سجل التدقيق.
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/015_entitlements_and_payouts_down.sql
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS staff_pay_plans (
    plan_id         UUID PRIMARY KEY,
    branch_id       UUID NOT NULL REFERENCES branches(branch_id),
    user_id         UUID NOT NULL REFERENCES users(user_id),
    model           VARCHAR(12) NOT NULL CHECK (model IN ('percentage', 'fixed', 'per_case')),
    percent         NUMERIC(5, 2) CHECK (percent IS NULL OR (percent >= 0 AND percent <= 100)),
    fixed_monthly   BIGINT CHECK (fixed_monthly IS NULL OR fixed_monthly >= 0),
    per_case_amount BIGINT CHECK (per_case_amount IS NULL OR per_case_amount >= 0),
    effective_from  DATE NOT NULL,
    created_by      UUID NOT NULL REFERENCES users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (branch_id, user_id, effective_from)
);
CREATE INDEX IF NOT EXISTS idx_pay_plans_user ON staff_pay_plans(branch_id, user_id, effective_from DESC);

CREATE TABLE IF NOT EXISTS payouts (
    payout_id       UUID PRIMARY KEY,
    branch_id       UUID NOT NULL REFERENCES branches(branch_id),
    user_id         UUID NOT NULL REFERENCES users(user_id),
    period_from     DATE NOT NULL,
    period_to       DATE NOT NULL,
    amount          BIGINT NOT NULL CHECK (amount > 0),
    method          VARCHAR(10) NOT NULL CHECK (method IN ('cash', 'transfer')),
    note            VARCHAR(500),
    created_by      UUID NOT NULL REFERENCES users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (period_to >= period_from)
);
CREATE INDEX IF NOT EXISTS idx_payouts_branch ON payouts(branch_id, created_at DESC);

COMMIT;
