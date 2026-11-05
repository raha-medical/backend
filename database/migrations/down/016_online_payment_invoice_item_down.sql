-- تراجع الترحيل 016: الدفعات المعلقة على بند ستُعامل كدفع للفاتورة كاملة، وعدم تطابق المبلغ يجعلها رصيداً للمريض.
BEGIN;
ALTER TABLE online_payments DROP COLUMN IF EXISTS invoice_item_id;
COMMIT;
