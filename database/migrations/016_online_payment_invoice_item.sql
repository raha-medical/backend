-- =============================================================================
-- ترحيل 016: دفع بند واحد من الفاتورة إلكترونياً (العقد: invoice_item_id في POST /online-payments).
-- تطبيق المريض يعرض مبلغ البند، فلا يصح أن يُحصّل كل بنود الفاتورة.
-- إضافة آمنة، ويمكن تشغيله أكثر من مرة. الصفوف القائمة تبقى NULL وتعني كل الفاتورة كما كانت.
-- التراجع: down/016_online_payment_invoice_item_down.sql
-- =============================================================================

BEGIN;

ALTER TABLE online_payments ADD COLUMN IF NOT EXISTS invoice_item_id UUID REFERENCES invoice_items(invoice_item_id);

COMMIT;
