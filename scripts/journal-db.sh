#!/usr/bin/env bash
# Local SQL Server for the fiscal journal.
#   up    start it and apply db/schema.sql to RollerJournal and RollerJournalTest
#   test  run dotnet test with the integration tests pointed at RollerJournalTest
#   sql   interactive sqlcmd on RollerJournal
#   down  stop it (data stays in the journal-data volume)
set -euo pipefail
cd "$(dirname "$0")/.."

new_password() { echo "Rj!$(openssl rand -hex 16)aZ"; }

# SQL Server wants at least 8 characters from three of: upper case, lower case, digits, symbols.
touch .env
chmod 600 .env
[[ -s .env && -n "$(tail -c1 .env)" ]] && echo >> .env
grep -q '^MSSQL_SA_PASSWORD=' .env || echo "MSSQL_SA_PASSWORD=$(new_password)" >> .env
grep -q '^JOURNAL_APP_PASSWORD=' .env || echo "JOURNAL_APP_PASSWORD=$(new_password)" >> .env

compose() { docker compose --env-file .env -f db/compose.yaml "$@"; }
sqlcmd() { compose exec -T sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -I "$@"; }
connection() { echo "Server=127.0.0.1,1433;Database=$1;User ID=journal_app;Password=$(grep '^JOURNAL_APP_PASSWORD=' .env | cut -d= -f2-);TrustServerCertificate=True"; }

case "${1:-}" in
  up)
    compose up -d --wait
    # Piped on stdin: single-file bind mounts in Docker Desktop can serve a stale copy after the file is edited.
    for database in RollerJournal RollerJournalTest; do
      sqlcmd -v JOURNAL_DB_NAME="$database" -i /dev/stdin < db/schema.sql
    done
    echo "journal databases ready on 127.0.0.1:1433"
    ;;
  test)
    JOURNAL_DB="$(connection RollerJournalTest)"
    export JOURNAL_DB
    dotnet test "${@:2}"
    ;;
  sql) compose exec sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -I -d RollerJournal ;;
  down) compose down ;;
  *) echo "usage: $0 up | test | sql | down" >&2; exit 2 ;;
esac
