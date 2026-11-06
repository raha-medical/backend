#!/usr/bin/env bash
# أدوات مشتركة لسكربتات النسخ الاحتياطي. كل الإعدادات من متغيرات البيئة.
set -euo pipefail

: "${STANZA:=alrahma}"
: "${ALRAHMA_DB:=${POSTGRES_DB:-alrahma}}"
: "${PGUSER:=postgres}"
: "${PGHOST:=/var/run/postgresql}"
: "${PGPORT:=5432}"
: "${BACKUP_REPO2:=0}"                      # 1 = إرسال كل نسخة للموقع الثاني أيضاً
: "${BACKUP_FILES_MODE:=off}"               # off | mirror
: "${BACKUP_STATE_DIR:=/var/lib/pgbackrest/.alrahma-state}"
: "${BACKUP_VERIFY_DIR:=/var/lib/pgbackrest/.verify}"
: "${BACKUP_LOG_DIR:=/var/log/pgbackrest}"
export PGUSER PGHOST PGPORT TZ=Asia/Baghdad
mkdir -p "$BACKUP_STATE_DIR" "$BACKUP_LOG_DIR"

PGBR=(pgbackrest --stanza="$STANZA")
[[ -n "${PGBACKREST_CONFIG:-}" ]] && PGBR+=(--config="$PGBACKREST_CONFIG")

log() { echo "[$(date '+%F %T')] $*"; }

# psql على قاعدة النظام. القيم تمر كمتغيرات psql (:'name') وليس داخل نص SQL.
# sql "SELECT ... :'name'" -v name=value ...   (المتغيرات لا تعمل مع psql -c، لذلك النص يمر عبر stdin)
sql() { local q=$1; shift; psql -X -qAt -v ON_ERROR_STOP=1 -d "$ALRAHMA_DB" "$@" <<<"$q"; }

repos() { if [[ "$BACKUP_REPO2" == "1" ]]; then echo "1 2"; else echo "1"; fi; }

new_uuid() { cat /proc/sys/kernel/random/uuid; }

# بداية تشغيل: يحدّث طلباً يدوياً موجوداً أو يسجل تشغيلاً مجدولاً جديداً. يطبع run_id.
run_start() {
  local type=$1 trigger=$2 run_id=${3:-}
  if [[ -n "$run_id" ]]; then
    sql "UPDATE backup_runs SET status = 'running', started_at = now() WHERE run_id = :'id'" -v id="$run_id" >/dev/null
  else
    run_id=$(new_uuid)
    sql "INSERT INTO backup_runs (run_id, type, trigger, status, started_at) VALUES (:'id', :'type', :'trig', 'running', now())" \
      -v id="$run_id" -v type="$type" -v trig="$trigger" >/dev/null
  fi
  echo "$run_id"
}

run_ok() { # run_id label size stored repos
  sql "UPDATE backup_runs SET status = 'succeeded', finished_at = now(), error = NULL,
              backup_label = NULLIF(:'label', ''), size_bytes = NULLIF(:'size', '')::bigint,
              stored_bytes = NULLIF(:'stored', '')::bigint, repos = NULLIF(:'repos', '')
        WHERE run_id = :'id'" \
    -v id="$1" -v label="${2:-}" -v size="${3:-}" -v stored="${4:-}" -v repos="${5:-}" >/dev/null
}

run_failed() { # run_id error
  sql "UPDATE backup_runs SET status = 'failed', finished_at = now(), error = :'err' WHERE run_id = :'id'" \
    -v id="$1" -v err="${2:0:4000}" >/dev/null || true
}
