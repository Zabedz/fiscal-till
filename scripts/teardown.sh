#!/usr/bin/env bash
# Deletes RollerTill through CloudFormation directly, so teardown works when the .NET build or synth is broken.
set -euo pipefail
export AWS_PROFILE="${AWS_PROFILE:-roller}" AWS_REGION=eu-west-2

aws cloudformation delete-stack --stack-name RollerTill
aws cloudformation wait stack-delete-complete --stack-name RollerTill
echo "RollerTill deleted. RollerGuardrails and CDKToolkit remain."
