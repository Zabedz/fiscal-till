#!/usr/bin/env bash
# Runs the local journal worker against the deployed RollerTill stack and the Docker SQL Server (scripts/journal-db.sh up first).
set -euo pipefail
cd "$(dirname "$0")/.."
export AWS_PROFILE="${AWS_PROFILE:-roller}" AWS_REGION=eu-west-2

output() {
  local value
  value="$(aws cloudformation describe-stacks --stack-name RollerTill \
    --query "Stacks[0].Outputs[?OutputKey=='$1'].OutputValue | [0]" --output text)"
  [[ -n "$value" && "$value" != "None" ]] || { echo "RollerTill output $1 is missing; is the stack deployed?" >&2; exit 1; }
  echo "$value"
}

set -a
source .env
set +a

# Assigned before export: 'export X="$(cmd)"' hides a failing cmd from set -e.
JOURNAL_QUEUE_URL="$(output JournalQueueUrl)"
SALES_TABLE="$(output SalesTableName)"
RECEIPTS_BUCKET="$(output ReceiptsBucketName)"
RECEIPT_SENDER="$(python3 -I -c 'import json; print(json.load(open("cdk.context.json"))["alertEmail"])')"
JOURNAL_DB="Server=127.0.0.1,1433;Database=RollerJournal;User ID=journal_app;Password=${JOURNAL_APP_PASSWORD};TrustServerCertificate=True"
export JOURNAL_QUEUE_URL SALES_TABLE RECEIPTS_BUCKET RECEIPT_SENDER JOURNAL_DB

exec dotnet run --project src/Roller.Journal "$@"
