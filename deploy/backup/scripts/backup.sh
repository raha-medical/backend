#!/usr/bin/env bash
# نسخة قاعدة البيانات أو تشغيل آخر، مع تسجيله في backup_runs.
#   backup.sh <full|diff|incr|verify|files> [schedule|manual] [run_id]
source "$(dirname "$0")/lib.sh"
TYPE=${1:?type}; TRIGGER=${2:-schedule}; RUN_ID=${3:-}
DIR=$(dirname "$0")

case "$TYPE" in
  verify) exec "$DIR/verify.sh" "$TRIGGER" "$RUN_ID" ;;
  files)  exec "$DIR/files-sync.sh" "$TRIGGER" "$RUN_ID" ;;
  full|diff|incr) ;;
  *) echo "unknown type $TYPE" >&2; exit 2 ;;
esac

RUN_ID=$(run_start "$TYPE" "$TRIGGER" "$RUN_ID")
LOG="$BACKUP_LOG_DIR/run-$RUN_ID.log"
log "backup $TYPE ($TRIGGER) run=$RUN_ID"
done_repos=()
for repo in $(repos); do
  if ! "${PGBR[@]}" --repo="$repo" --type="$TYPE" backup >>"$LOG" 2>&1; then
    run_failed "$RUN_ID" "repo$repo: $(tail -n 15 "$LOG")"
    log "FAILED (repo$repo), see $LOG"
    exit 1
  fi
  "$DIR/retention.sh" "$repo" >>"$LOG" 2>&1 || log "retention warning on repo$repo (see $LOG)"
  done_repos+=("$repo")
done

INFO=$("${PGBR[@]}" --repo=1 --output=json info)
LAST=$(jq -c '.[0].backup | last' <<<"$INFO")
run_ok "$RUN_ID" "$(jq -r '.label' <<<"$LAST")" "$(jq -r '.info.size' <<<"$LAST")" \
  "$(jq -r '.info.repository.delta' <<<"$LAST")" "$(IFS=,; echo "${done_repos[*]}")"
log "backup $TYPE done: $(jq -r '.label' <<<"$LAST")"
