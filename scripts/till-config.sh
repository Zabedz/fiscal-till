#!/usr/bin/env bash
# Writes the Angular till's config.json: API URL, venues and their API keys. Both target files are gitignored.
#   scripts/till-config.sh              web/till/public/config.json for ng serve, with the API's URL and the venue keys
#   scripts/till-config.sh site <file>  <file> for the CloudFront site, with the site's URL and the demo keys
set -euo pipefail
cd "$(dirname "$0")/.."
export AWS_PROFILE="${AWS_PROFILE:-roller}" AWS_REGION=eu-west-2

mode="${1:-local}"
case "$mode" in
  local) target=web/till/public/config.json ;;
  site) target="${2:?usage: scripts/till-config.sh site <file>}" ;;
  *) echo "usage: scripts/till-config.sh [site <file>]" >&2; exit 2 ;;
esac

outputs="$(aws cloudformation describe-stacks --stack-name RollerTill --query 'Stacks[0].Outputs' --output json)"

python3 -I - "$outputs" "$mode" "$target" <<'PY'
import json
import subprocess
import sys

outputs = {entry["OutputKey"]: entry["OutputValue"] for entry in json.loads(sys.argv[1])}
mode, target = sys.argv[2], sys.argv[3]
if mode == "site":
    api_url, key_ids = outputs["SiteUrl"], json.loads(outputs["DemoApiKeyIds"])
else:
    api_url, key_ids = outputs["ApiUrl"].rstrip("/"), json.loads(outputs["ApiKeyIds"])
markets = {market["market"]: market for market in json.load(open("config/markets.json"))}


def api_key(key_id):
    return subprocess.run(
        ["aws", "apigateway", "get-api-key", "--api-key", key_id, "--include-value", "--query", "value", "--output", "text"],
        check=True, capture_output=True, text=True).stdout.strip()


venues = []
for venue in json.load(open("config/venues.json")):
    market = markets[venue["market"]]
    venues.append({**venue, "currency": market["currency"], "locale": market["locale"], "apiKey": api_key(key_ids[venue["venueId"]])})

with open(target, "w") as config:
    json.dump({"apiUrl": api_url, "venues": venues}, config, indent=2)
print(f"{target} written for {len(venues)} venues")
PY
