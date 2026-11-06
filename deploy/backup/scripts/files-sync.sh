#!/usr/bin/env bash
# نسخ ملفات MinIO (أشعة، نتائج، صور) إلى الموقع الثاني وتعبئة files.s3_synced_at.
# مطفأ افتراضياً (BACKUP_FILES_MODE=off). mirror = نسخ تزايدي بـ mc mirror (لا يحذف من الهدف).
#   BACKUP_FILES_SOURCE  مثل http://minio:9000 مع MINIO_ACCESS_KEY / MINIO_SECRET_KEY و MINIO_BUCKET
#   BACKUP_FILES_TARGET  مسار محلي (قرص ثانٍ) مثل /mnt/backup2/files، أو s3://bucket/prefix مع
#                        BACKUP_FILES_TARGET_URL و BACKUP_FILES_TARGET_KEY و BACKUP_FILES_TARGET_SECRET
source "$(dirname "$0")/lib.sh"
TRIGGER=${1:-schedule}
if [[ "$BACKUP_FILES_MODE" == "off" ]]; then echo "files backup is off" >&2; exit 0; fi
RUN_ID=$(run_start files "$TRIGGER" "${2:-}")
LOG="$BACKUP_LOG_DIR/run-$RUN_ID.log"
STARTED=$(date -u +%FT%TZ)
: "${MINIO_BUCKET:=medical}"
fail() { run_failed "$RUN_ID" "$1: $(tail -n 15 "$LOG" 2>/dev/null)"; log "files FAILED: $1"; exit 1; }

mc alias set src "${BACKUP_FILES_SOURCE:?}" "${MINIO_ACCESS_KEY:?}" "${MINIO_SECRET_KEY:?}" >>"$LOG" 2>&1 || fail "تعذر الاتصال بـ MinIO"
TARGET=${BACKUP_FILES_TARGET:?}
if [[ "$TARGET" == s3://* ]]; then
  mc alias set dst "${BACKUP_FILES_TARGET_URL:?}" "${BACKUP_FILES_TARGET_KEY:?}" "${BACKUP_FILES_TARGET_SECRET:?}" >>"$LOG" 2>&1 \
    || fail "تعذر الاتصال بالموقع الثاني"
  TARGET="dst/${TARGET#s3://}"
fi
mc mirror --overwrite "src/$MINIO_BUCKET" "$TARGET" >>"$LOG" 2>&1 || fail "فشل نسخ الملفات"
COUNT=$(sql "WITH s AS (UPDATE files SET s3_synced_at = now()
                         WHERE status = 'stored' AND s3_synced_at IS NULL AND created_at < :'started'::timestamptz RETURNING 1)
             SELECT count(*) FROM s" -v started="$STARTED")
run_ok "$RUN_ID" "files-$(date +%Y%m%d-%H%M)" "" "" "2"
log "files synced, $COUNT new files marked"
