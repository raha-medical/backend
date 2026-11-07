-- تراجع الترحيل 030
BEGIN;
ALTER TABLE mock_gateway_transactions DROP COLUMN IF EXISTS otp_code;
COMMIT;
