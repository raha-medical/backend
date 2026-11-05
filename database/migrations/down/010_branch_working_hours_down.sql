-- =============================================================================
-- تراجع الترحيل 010: يحذف أوقات الدوام المهيكلة وفحص الإحداثيات.
-- النص القديم branches.working_hours يبقى كما هو (يحمل ملخص الجدول إن كان قد وُلّد منه).
-- التشغيل: psql "$DATABASE_URL" -f database/migrations/down/010_branch_working_hours_down.sql
-- =============================================================================

BEGIN;
ALTER TABLE branches DROP CONSTRAINT IF EXISTS ck_branches_location;
DROP TABLE IF EXISTS branch_working_hours;
COMMIT;
