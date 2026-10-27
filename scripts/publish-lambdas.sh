#!/usr/bin/env bash
# Publishes each Lambda project for the arm64 managed runtime; CDK zips artifacts/<project>.
set -euo pipefail
cd "$(dirname "$0")/.."

for project in Roller.Api Roller.Fiscalise; do
  rm -rf "artifacts/$project"
  dotnet publish "src/$project/$project.csproj" -c Release -r linux-arm64 --self-contained false \
    -o "artifacts/$project" --nologo -v quiet
done
