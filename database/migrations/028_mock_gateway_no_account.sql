-- =============================================================================
-- ترحيل 028: لا يبقى رقم البطاقة أو المحفظة في معاملات البوابة التجريبية.
--   test_account صار يحمل نتيجة الحساب التجريبي (مثل approved) لا الرقم نفسه.
--   يمسح الأرقام القديمة من المعاملات المنتهية. آمن للتكرار.
--   التراجع: down/028_mock_gateway_no_account_down.sql (لا يعيد الأرقام الممسوحة)
-- =============================================================================
BEGIN;
UPDATE mock_gateway_transactions SET test_account = NULL WHERE status <> 'pending' AND test_account ~ '^[0-9]+$';
COMMENT ON COLUMN mock_gateway_transactions.test_account IS 'نتيجة الحساب التجريبي، وليس رقم البطاقة أو المحفظة';
COMMIT;
