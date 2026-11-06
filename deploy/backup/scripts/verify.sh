#!/usr/bin/env bash
# اختبار استرجاع تلقائي (أسبوعي): يسترجع آخر نسخة في مجلد مؤقت، يشغلها على منفذ داخلي،
# يتحقق من الجداول الأساسية، ثم يحذفها. لا يمس قاعدة البيانات العاملة.
#   verify.sh [schedule|manual] [run_id]
source "$(dirname "$0")/lib.sh"
TRIGGER=${1:-schedule}; RUN_ID=$(run_start verify "$TRIGGER" "${2:-}")
LOG="$BACKUP_LOG_DIR/run-$RUN_ID.log"
PORT=${BACKUP_VERIFY_PORT:-55432}
PGBIN=${PGBIN:-$(dirname "$(command -v pg_ctl || echo /usr/lib/postgresql/16/bin/pg_ctl)")}
cleanup() { "$PGBIN/pg_ctl" -D "$BACKUP_VERIFY_DIR" -m immediate stop >/dev/null 2>&1 || true; rm -rf "$BACKUP_VERIFY_DIR"; }
fail() { run_failed "$RUN_ID" "$1: $(tail -n 15 "$LOG" 2>/dev/null)"; cleanup; log "verify FAILED: $1"; exit 1; }
trap cleanup EXIT

log "verify run=$RUN_ID"
cleanup; mkdir -p "$BACKUP_VERIFY_DIR"; chmod 700 "$BACKUP_VERIFY_DIR"
LABEL=$("${PGBR[@]}" --repo=1 --output=json info | jq -r '.[0].backup | last | .label')
[[ "$LABEL" == "null" || -z "$LABEL" ]] && fail "لا توجد نسخة للاختبار"
"${PGBR[@]}" --repo=1 --set="$LABEL" --pg1-path="$BACKUP_VERIFY_DIR" --type=immediate --target-action=promote restore >>"$LOG" 2>&1 \
  || fail "فشل الاسترجاع"
# تثبيتات لا تحفظ الإعدادات داخل مجلد البيانات (مثل Ubuntu): إعدادات دنيا للتشغيل المحلي فقط
[[ -f "$BACKUP_VERIFY_DIR/postgresql.conf" ]] || : >"$BACKUP_VERIFY_DIR/postgresql.conf"
[[ -f "$BACKUP_VERIFY_DIR/pg_hba.conf" ]] || echo "local all all trust" >"$BACKUP_VERIFY_DIR/pg_hba.conf"
[[ -f "$BACKUP_VERIFY_DIR/pg_ident.conf" ]] || : >"$BACKUP_VERIFY_DIR/pg_ident.conf"
# بدون أرشفة حتى لا تختلط WAL النسخة التجريبية بالمستودع
"$PGBIN/pg_ctl" -D "$BACKUP_VERIFY_DIR" -w -t 600 -l "$LOG" start \
  -o "-p $PORT -c listen_addresses='' -c unix_socket_directories=/tmp -c archive_mode=off -c hba_file=$BACKUP_VERIFY_DIR/pg_hba.conf -c ident_file=$BACKUP_VERIFY_DIR/pg_ident.conf" >/dev/null \
  || fail "تعذر تشغيل النسخة المسترجعة"
for _ in $(seq 1 120); do
  [[ "$(psql -X -qAt -h /tmp -p "$PORT" -d "$ALRAHMA_DB" -c 'SELECT NOT pg_is_in_recovery()' 2>/dev/null)" == "t" ]] && break
  sleep 1
done
CHECK=$(psql -X -qAt -h /tmp -p "$PORT" -d "$ALRAHMA_DB" -c \
  "SELECT (SELECT count(*) FROM branches) || ' branches, ' || (SELECT count(*) FROM patients) || ' patients'" 2>>"$LOG") \
  || fail "قاعدة البيانات المسترجعة لا تستجيب"
SIZE=$(du -sb "$BACKUP_VERIFY_DIR" | cut -f1)
run_ok "$RUN_ID" "$LABEL" "$SIZE" "" "1"
log "verify OK ($LABEL): $CHECK"
