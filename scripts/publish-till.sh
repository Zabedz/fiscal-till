#!/usr/bin/env bash
# Builds the Angular till and publishes it to the RollerTill CloudFront site, configured with the demo keys.
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

bucket="$(output SiteBucketName)"
distribution="$(output SiteDistributionId)"
site_url="$(output SiteUrl)"
build=web/till/dist/till/browser
assets=(--only-show-errors --cache-control "public, max-age=300" --exclude index.html --exclude config.json)

[[ -d web/till/node_modules ]] || (cd web/till && npm ci)
(cd web/till && npm run build)
scripts/till-config.sh site "$build/config.json"

# New bundles land before the page that names them, and old ones go only after it, so a load mid-publish still finds its
# files. The page and its config are revalidated on every load; the bundles' names change with their content.
aws s3 sync "$build" "s3://$bucket" "${assets[@]}"
aws s3 cp --only-show-errors "$build/config.json" "s3://$bucket/config.json" --cache-control no-cache --content-type application/json
aws s3 cp --only-show-errors "$build/index.html" "s3://$bucket/index.html" --cache-control no-cache --content-type "text/html; charset=utf-8"
aws cloudfront create-invalidation --distribution-id "$distribution" --paths '/*' >/dev/null
aws s3 sync "$build" "s3://$bucket" --delete "${assets[@]}"
echo "till published at $site_url"
