#!/usr/bin/env bash
# Runs one load or failure drill and appends its window to load/drills.json; the next cdk deploy marks it on the dashboard.
#   baseline  10 minutes peaking at 10 sales per second, partner healthy
#   slow      5 minutes peaking at 20 sales per second, partner answering after 2 s
#   outage    8 minutes at 10 sales per second; the partner is down from minute 2 to minute 6
#   poison    malformed and unknown-sale messages put straight on the fiscalise queue
#   burst     one venue at 150 requests per second for a minute against its 50 per second limit
set -euo pipefail
cd "$(dirname "$0")/.."
export AWS_PROFILE="${AWS_PROFILE:-roller}" AWS_REGION=eu-west-2

OUTAGE_STARTS_AFTER=120
OUTAGE_LASTS=240
POISON_MESSAGES=5
DLQ_WAIT_LIMIT=300

scenario="${1:?usage: load/drill.sh baseline | slow | outage | poison | burst}"
run="${scenario}-$(date -u +%Y%m%dT%H%M%SZ)"
status=0
mkdir -p load/results

output() {
  aws cloudformation describe-stacks --stack-name RollerTill \
    --query "Stacks[0].Outputs[?OutputKey=='$1'].OutputValue | [0]" --output text
}

now() { date -u +%Y-%m-%dT%H:%M:%SZ; }

record() {
  python3 -I - "$1" "$2" "$3" <<'PY'
import json, os, sys
path = "load/drills.json"
drills = json.load(open(path)) if os.path.exists(path) else []
drills.append({"name": sys.argv[1], "start": sys.argv[2], "end": sys.argv[3]})
with open(path, "w") as file:
    json.dump(drills, file, indent=2)
    file.write("\n")
PY
}

k6run() { k6 run --quiet -e SCENARIO="$scenario" -e RUN="$run" load/till.js; }

dlq_depth() {
  aws sqs get-queue-attributes --queue-url "$1" --attribute-names ApproximateNumberOfMessages \
    --query Attributes.ApproximateNumberOfMessages --output text
}

# Whatever happens, stop a background load and leave the partner healthy.
trap 'kill "${load:-}" 2>/dev/null || true; scripts/partner.sh healthy >/dev/null' EXIT

case "$scenario" in
  baseline)
    start="$(now)"; k6run || status=$?; record "Baseline" "$start" "$(now)" ;;
  slow)
    scripts/partner.sh slow 2000
    start="$(now)"; k6run || status=$?; record "Slow partner (2 s)" "$start" "$(now)" ;;
  outage)
    k6run &
    load=$!
    sleep "$OUTAGE_STARTS_AFTER"
    scripts/partner.sh outage; off="$(now)"
    sleep "$OUTAGE_LASTS"
    scripts/partner.sh healthy; on="$(now)"
    wait "$load" || status=$?
    record "Partner outage" "$off" "$on" ;;
  poison)
    queue="$(output FiscaliseQueueUrl)"; dlq="$(output FiscaliseDlqUrl)"
    before="$(dlq_depth "$dlq")"
    start="$(now)"
    for body in 'not json' '{"saleId":42}' '{"venueId":"paris-01"}' \
      '{"saleId":"00000000000000000000000000000000","venueId":"paris-01","tillId":"T01"}' \
      '{"saleId":"ffffffffffffffffffffffffffffffff","venueId":"berlin-01","tillId":"T02"}'; do
      aws sqs send-message --queue-url "$queue" --message-body "$body" >/dev/null
    done
    status=1
    for (( waited = 0; waited < DLQ_WAIT_LIMIT; waited += 10 )); do
      arrived=$(( $(dlq_depth "$dlq") - before ))
      echo "fiscalise DLQ holds $arrived of $POISON_MESSAGES poison messages after ${waited}s"
      (( arrived >= POISON_MESSAGES )) && { status=0; break; }
      sleep 10
    done
    record "Poison messages" "$start" "$(now)" ;;
  burst)
    start="$(now)"; k6run || status=$?; record "Burst over limit" "$start" "$(now)" ;;
  *)
    echo "unknown drill: $scenario" >&2; exit 2 ;;
esac

echo "drill $run recorded in load/drills.json"
exit "$status"
