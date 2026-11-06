#!/usr/bin/env bash
# مجدول النسخ (يعمل داخل حاوية قاعدة البيانات، كل دقيقة، بتوقيت بغداد):
#   * 03:00 كل جمعة: نسخة كاملة. 03:00 باقي الأيام: تزايدية.
#   * 05:00 كل جمعة: اختبار استرجاع (verify.sh).
#   * 03:30 يومياً: نسخ ملفات MinIO إذا كان مفعلاً.
#   * تنفيذ الطلبات اليدوية من لوحة المدير (backup_runs.status = requested).
# الـ API لا ينفذ أي أمر هنا؛ هذا السكربت وحده ينفذ.
source "$(dirname "$0")/lib.sh"
DIR=$(dirname "$0")
: "${BACKUP_FULL_DOW:=5}"       # 5 = الجمعة (date +%u)
: "${BACKUP_AT:=0300}"
: "${BACKUP_VERIFY_AT:=0500}"
: "${BACKUP_FILES_AT:=0330}"

until pg_isready -q -h "$PGHOST" -p "$PGPORT"; do sleep 5; done
until sql "SELECT 1 FROM backup_runs LIMIT 1" >/dev/null 2>&1; do log "waiting for backup_runs table (migration 006)"; sleep 30; done
"${PGBR[@]}" stanza-create >>"$BACKUP_LOG_DIR/scheduler.log" 2>&1 || log "stanza-create failed (see scheduler.log)"
"${PGBR[@]}" check >>"$BACKUP_LOG_DIR/scheduler.log" 2>&1 || log "check failed (see scheduler.log)"
# تشغيل انقطع بإعادة تشغيل الحاوية
sql "UPDATE backup_runs SET status = 'failed', finished_at = now(), error = 'انقطع التشغيل (إعادة تشغيل السيرفر)' WHERE status = 'running'" >/dev/null

once() { # key: يُنفذ مرة واحدة في اليوم
  local f="$BACKUP_STATE_DIR/$1"
  [[ "$(cat "$f" 2>/dev/null)" == "$(date +%F)" ]] && return 1
  date +%F >"$f"
}

log "scheduler started"
while true; do
  now=$(date +%H%M); dow=$(date +%u)
  if [[ "$now" -ge "$BACKUP_AT" ]] && once daily; then
    if [[ "$dow" == "$BACKUP_FULL_DOW" ]] || ! "${PGBR[@]}" --repo=1 --output=json info | jq -e '.[0].backup | length > 0' >/dev/null; then
      "$DIR/backup.sh" full schedule || true
    else
      "$DIR/backup.sh" incr schedule || true
    fi
  fi
  if [[ "$dow" == "$BACKUP_FULL_DOW" && "$now" -ge "$BACKUP_VERIFY_AT" ]] && once verify; then
    "$DIR/verify.sh" schedule || true
  fi
  if [[ "$BACKUP_FILES_MODE" != "off" && "$now" -ge "$BACKUP_FILES_AT" ]] && once files; then
    "$DIR/files-sync.sh" schedule || true
  fi
  while read -r id type; do
    [[ -z "$id" ]] && break
    log "manual request $id ($type)"
    "$DIR/backup.sh" "$type" manual "$id" || true
  done < <(sql "SELECT run_id, type FROM backup_runs WHERE status = 'requested' ORDER BY created_at LIMIT 1" -F ' ')
  sleep 60
done
