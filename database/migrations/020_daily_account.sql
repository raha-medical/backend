-- =============================================================================
-- ترحيل 020: الحساب اليومي (قرار المالك، التدقيق القسم 4)
--   * queue_tickets.checked_in_by: من أكّد حضور المراجع (لعرض «الإلكتروني لمن أكّدتُ حضورهم» و«غير الدافعين»).
--   * receipts.printed_at و unprinted_reason: الوصل يُعلَّم مطبوعاً، وغير المطبوع يحتاج سبباً عند الإغلاق.
--   * shifts.auto_closed: الحساب أُغلق تلقائياً بنهاية اليوم بانتظار إقرار الموظف بالمبلغ المعدود.
--   * staff_liabilities: ذمة الموظف (العجز أو الزيادة) باسمه، تُرحَّل مع الحساب لمحاسب الفرع.
--   * الإعداد billing.collection_mode = central | distributed (central = السلوك الحالي).
-- إضافات آمنة، ويمكن تشغيله أكثر من مرة. التراجع: down/020_daily_account_down.sql
-- =============================================================================

BEGIN;

ALTER TABLE queue_tickets ADD COLUMN IF NOT EXISTS checked_in_by UUID REFERENCES users(user_id);
CREATE INDEX IF NOT EXISTS idx_queue_checked_in_by ON queue_tickets(checked_in_by, arrived_at) WHERE checked_in_by IS NOT NULL;

ALTER TABLE receipts ADD COLUMN IF NOT EXISTS printed_at TIMESTAMPTZ;
ALTER TABLE receipts ADD COLUMN IF NOT EXISTS unprinted_reason TEXT;
-- الوصولات السابقة لهذا الترحيل تُعتبر مطبوعة (لا تُطلب أسباب لحسابات قديمة)
UPDATE receipts SET printed_at = issued_at WHERE printed_at IS NULL AND issued_at < now();

ALTER TABLE shifts ADD COLUMN IF NOT EXISTS auto_closed BOOLEAN NOT NULL DEFAULT FALSE;

CREATE TABLE IF NOT EXISTS staff_liabilities (
    liability_id    UUID PRIMARY KEY,
    shift_id        UUID NOT NULL UNIQUE REFERENCES shifts(shift_id),
    branch_id       UUID NOT NULL REFERENCES branches(branch_id),
    user_id         UUID NOT NULL REFERENCES users(user_id),
    amount          BIGINT NOT NULL CHECK (amount <> 0),          -- سالب = عجز في ذمة الموظف، موجب = زيادة
    kind            VARCHAR(10) NOT NULL CHECK (kind IN ('shortage', 'overage')),
    status          VARCHAR(12) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'accepted', 'escalated')),
    note            TEXT,
    reviewed_by     UUID REFERENCES users(user_id),
    reviewed_at     TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_staff_liabilities_user ON staff_liabilities(user_id, created_at);

INSERT INTO system_settings (branch_id, key, value)
SELECT NULL, 'billing.collection_mode', '"central"'
 WHERE NOT EXISTS (SELECT 1 FROM system_settings WHERE branch_id IS NULL AND key = 'billing.collection_mode');

COMMIT;
