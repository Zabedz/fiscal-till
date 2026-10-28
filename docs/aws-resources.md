# AWS resources

The two CDK stacks in [infra/Roller.Infra](https://github.com/Zabedz/fiscal-till-pipeline/tree/main/infra/Roller.Infra) define everything here except the last section.
Regional resources are in eu-west-2; the budget and the IAM user are global. Where the code sets no name,
CloudFormation names the resource `<Stack>-<ConstructId><hash>-<suffix>`: the fiscalise DLQ alarm, for example, is
`RollerTill-FiscaliseDlqNotEmpty` followed by a hash and a random suffix.

## Alarms and notifications

| In the console | Defined in | Behaviour |
| --- | --- | --- |
| CloudWatch alarm `RollerTill-FiscaliseDlqNotEmpty...` | [TillStack.cs](https://github.com/Zabedz/fiscal-till-pipeline/blob/main/infra/Roller.Infra/TillStack.cs), `AlarmWhenNotEmpty("FiscaliseDlq", ...)` | ALARM when the fiscalise DLQ's visible-message count, maximum over 1 minute, is above 0. Missing data counts as OK. |
| CloudWatch alarm `RollerTill-JournalDlqNotEmpty...` | Same method, `"JournalDlq"` | The same rule on the journal DLQ. |
| SNS topic `roller-alarms` and its email subscription | [GuardrailsStack.cs](https://github.com/Zabedz/fiscal-till-pipeline/blob/main/infra/Roller.Infra/GuardrailsStack.cs) | Both alarms publish here; it emails the `alertEmail` address. |
| Budget `roller-monthly-cost` (Billing, Budgets) | GuardrailsStack.cs | AWS Budgets emails at 50% of $10 actual and 100% forecast. It covers the whole account. |

- An alarm emails when it enters ALARM. Clearing sends nothing.
- While a DLQ holds messages its alarm stays in ALARM, so later dead letters send no email until the DLQ is emptied.
- No alarm watches the journal queue backlog. A stopped journal worker shows only on the dashboard.
- RollerTill finds `roller-alarms` by its name, so RollerGuardrails has to be deployed first.

## Till API

| In the console | Settings |
| --- | --- |
| REST API `roller-till`, stage `live` | Default method throttling: 200 requests/s, burst 400, for each method. CORS for `http://localhost:4200` only. No access logs. |
| Usage plan `roller-venues` | Holds every venue's key. Per key: 50 requests/s, burst 100, 300,000 a day. |
| API keys `roller-paris-01`, `roller-berlin-01`, `roller-vienna-01` | One per venue in [config/venues.json](https://github.com/Zabedz/fiscal-till-pipeline/blob/main/config/venues.json). AWS generates the values; `scripts/till-config.sh` fetches them. |
| Gateway responses `DEFAULT_4XX`, `DEFAULT_5XX` | Add CORS headers so the browser can read API Gateway's own 403 and 429. |
| Resources `/sales` (POST), `/sales/{saleId}` (GET) | API key required, Lambda proxy integration. |

## Lambdas

| Construct id | Code | Timeout | Trigger and limits |
| --- | --- | --- | --- |
| CreateSale | [CreateSaleFunction.cs](https://github.com/Zabedz/fiscal-till-pipeline/blob/main/src/Roller.Api/CreateSaleFunction.cs) | 10 s | POST /sales |
| GetSale | [GetSaleFunction.cs](https://github.com/Zabedz/fiscal-till-pipeline/blob/main/src/Roller.Api/GetSaleFunction.cs) | 10 s | GET /sales/{saleId} |
| FiscaliseWorker | [FiscaliseFunction.cs](https://github.com/Zabedz/fiscal-till-pipeline/blob/main/src/Roller.Fiscalise/FiscaliseFunction.cs) | 15 s | The fiscalise queue ([trigger settings](queues-and-failures.md#the-fiscalise-queue)) |
| MockPartner | [PartnerFunction.cs](https://github.com/Zabedz/fiscal-till-pipeline/blob/main/src/Roller.MockPartner/PartnerFunction.cs) | 15 s | Function URL with auth `NONE` and a shared key header; reserved concurrency 60 |

- All four run .NET 10 on arm64 with 1024 MB and JSON logs. Each logs to `RollerTill-<ConstructId>Logs...`, kept a
  week.
- `RollerTill-CustomS3AutoDeleteObjects...` is CDK's helper that empties the receipts bucket when the stack is deleted.
  It logs to `RollerTill-AutoDeleteObjectsLogs...`, also kept a week.
- `cdk deploy` runs [scripts/publish-lambdas.sh](https://github.com/Zabedz/fiscal-till-pipeline/blob/main/scripts/publish-lambdas.sh) first, and CDK zips each folder under
  `artifacts/`. CreateSale and GetSale share one zip.

## Data, queues and configuration

| In the console | Settings |
| --- | --- |
| DynamoDB `RollerTill-Sales...` | On demand, key `SaleId`, index `ByVenueDay` (`VenueDay`, `CreatedAt`), which nothing reads yet. |
| DynamoDB `RollerTill-PartnerSignatures...` | The mock partner's counter per till (`DEVICE#...`) and signature per sale (`SIG#...`). |
| SQS `RollerTill-FiscaliseQueue...`, `RollerTill-FiscaliseDlq...` | Standard; settings in [queues-and-failures.md](queues-and-failures.md#the-fiscalise-queue). |
| SQS `RollerTill-JournalQueue....fifo`, `RollerTill-JournalDlq....fifo` | FIFO; settings in [queues-and-failures.md](queues-and-failures.md#the-journal-queue). |
| S3 `rollertill-receipts...` | Private, encrypted, HTTPS only, emptied and deleted with the stack. |
| SSM parameter `/roller/partner/switches` | Partner drill switches; `scripts/partner.sh` overwrites it. |
| Secrets `PartnerApiKey...`, `PartnerSigningKey...` | CloudFormation creates the values, so they change after every teardown. |
| SES identity: the alert address (RollerGuardrails, kept on teardown) | Receipt sender and, in the SES sandbox, the only allowed recipient. |
| CloudWatch dashboard `roller-till` | [TillDashboard.cs](https://github.com/Zabedz/fiscal-till-pipeline/blob/main/infra/Roller.Infra/TillDashboard.cs): alarm status, then queues, workers and API, with drill windows marked. GetSale is not graphed. |

Stack outputs (CloudFormation, RollerTill, Outputs) hold the API URL, key ids, table and bucket names, queue URLs and the
partner URL. The scripts read them with `describe-stacks`.

## Outside the stacks

| Resource | Origin |
| --- | --- |
| Stack `CDKToolkit` and its assets bucket | `cdk bootstrap` |
| IAM user behind the CLI profile `roller` | Created by hand. The journal worker and the scripts use its credentials, so no role in RollerTill grants the journal's access to the journal queue, the sales table, S3 or SES. |
