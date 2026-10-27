#!/usr/bin/env bash
# Writes web/till/public/config.json for the Angular till: API URL, venues and their API keys. The file is gitignored.
set -euo pipefail
cd "$(dirname "$0")/.."
export AWS_PROFILE="${AWS_PROFILE:-roller}" AWS_REGION=eu-west-2

outputs="$(aws cloudformation describe-stacks --stack-name RollerTill --query 'Stacks[0].Outputs' --output json)"

python3 -I - "$outputs" <<'PY'
import json
import subprocess
import sys

outputs = {entry["OutputKey"]: entry["OutputValue"] for entry in json.loads(sys.argv[1])}
key_ids = json.loads(outputs["ApiKeyIds"])
markets = {market["market"]: market for market in json.load(open("config/markets.json"))}


def api_key(key_id):
    return subprocess.run(
        ["aws", "apigateway", "get-api-key", "--api-key", key_id, "--include-value", "--query", "value", "--output", "text"],
        check=True, capture_output=True, text=True).stdout.strip()


venues = []
for venue in json.load(open("config/venues.json")):
    market = markets[venue["market"]]
    venues.append({**venue, "currency": market["currency"], "locale": market["locale"], "apiKey": api_key(key_ids[venue["venueId"]])})

with open("web/till/public/config.json", "w") as config:
    json.dump({"apiUrl": outputs["ApiUrl"].rstrip("/"), "venues": venues}, config, indent=2)
print(f"web/till/public/config.json written for {len(venues)} venues")
PY
