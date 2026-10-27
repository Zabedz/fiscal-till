#!/usr/bin/env bash
# Sets the mock partner's drill switches; the partner picks them up within 5 seconds.
set -euo pipefail
export AWS_PROFILE="${AWS_PROFILE:-roller}" AWS_REGION=eu-west-2
PARAMETER=/roller/partner/switches
MAX_LATENCY_MS=10000

usage() { echo "usage: $0 healthy | slow <ms, 0 to $MAX_LATENCY_MS> | errors <rate, 0 to 1> | outage" >&2; exit 2; }

case "${1:-}" in
  healthy) value='{"latencyMs":0,"errorRate":0,"outage":false}' ;;
  slow)
    [[ "${2:-}" =~ ^[0-9]+$ ]] && (( $2 <= MAX_LATENCY_MS )) || usage
    value="{\"latencyMs\":$2,\"errorRate\":0,\"outage\":false}" ;;
  errors)
    [[ "${2:-}" =~ ^(0(\.[0-9]+)?|1(\.0+)?)$ ]] || usage
    value="{\"latencyMs\":0,\"errorRate\":$2,\"outage\":false}" ;;
  outage) value='{"latencyMs":0,"errorRate":0,"outage":true}' ;;
  *) usage ;;
esac

# The parameter belongs to RollerTill; creating it here would block the next deploy.
aws ssm get-parameter --name "$PARAMETER" >/dev/null 2>&1 || { echo "RollerTill is not deployed: $PARAMETER is missing" >&2; exit 1; }
aws ssm put-parameter --name "$PARAMETER" --type String --overwrite --value "$value" >/dev/null
echo "partner switches: $value"
