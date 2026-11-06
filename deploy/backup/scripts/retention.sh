#!/usr/bin/env bash
# الاحتفاظ: آخر 4 نسخ كاملة (أسبوعية) + آخر نسخة كاملة من كل شهر لآخر 12 شهراً.
# النسخ التزايدية تُحفظ لآخر نسختين كاملتين فقط (14 يوماً)، مثل سجل WAL.
#   retention.sh <repo>
source "$(dirname "$0")/lib.sh"
REPO=${1:-1}
INFO=$("${PGBR[@]}" --repo="$REPO" --output=json info)
FULLS=$(jq -r '.[0].backup[] | select(.type == "full") | "\(.label) \(.timestamp.stop)"' <<<"$INFO")
[[ -z "$FULLS" ]] && exit 0

declare -A keep=() month_seen=()
mapfile -t rows < <(sort -r -k2 -n <<<"$FULLS")
i=0
for row in "${rows[@]}"; do
  label=${row% *}; stop=${row#* }
  (( i < 4 )) && keep[$label]=1
  month=$(date -d "@$stop" +%Y-%m)
  if [[ -z "${month_seen[$month]:-}" && ${#month_seen[@]} -lt 12 ]]; then
    month_seen[$month]=1; keep[$label]=1
  fi
  i=$((i + 1))
done

# الأقدم أولاً، والنسخ المحذوفة تحذف معها التزايدية التابعة لها
for row in $(printf '%s\n' "${rows[@]}" | sort -k2 -n | cut -d' ' -f1); do
  if [[ -z "${keep[$row]:-}" ]]; then
    log "expire full $row (repo$REPO)"
    "${PGBR[@]}" --repo="$REPO" --set="$row" expire
  fi
done

# التزايدية/التفاضلية التابعة لنسخ كاملة أقدم من آخر نسختين
recent=$(printf '%s\n' "${rows[@]}" | head -n 2 | cut -d' ' -f1 | paste -sd'|')
INFO=$("${PGBR[@]}" --repo="$REPO" --output=json info)
for label in $(jq -r '.[0].backup[] | select(.type != "full") | "\(.label) \(.prior)"' <<<"$INFO" \
                 | awk -v recent="^($recent)" '{ split($1, a, "_"); if (a[1] !~ recent) print $1 }'); do
  if "${PGBR[@]}" --repo="$REPO" --output=json info | jq -e --arg l "$label" '.[0].backup[] | select(.label == $l)' >/dev/null; then
    log "expire $label (repo$REPO)"
    "${PGBR[@]}" --repo="$REPO" --set="$label" expire
  fi
done
