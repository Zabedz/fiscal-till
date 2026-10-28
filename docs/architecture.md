# Architecture

![The till on the laptop calls API Gateway; CreateSale stores the sale and queues it; FiscaliseWorker signs it through the mock partner and queues it for the journal; the journal worker on the laptop numbers it in SQL Server, writes the receipt to S3 and emails it when the customer gave an address.](diagrams/architecture.svg)

Arrows in the text colour carry every sale. Red arrows are the failure path: dashed into and out of the dead-letter
queues, solid from the alarm to the email.

## A sale's path

1. The till posts `POST /sales` with its venue's API key and an `Idempotency-Key`. API Gateway applies the usage plan's
   per-key limits ([aws-resources.md](aws-resources.md#till-api)).
2. CreateSale validates and prices the sale, then writes it to the sales table with `attribute_not_exists(SaleId)`. The
   sale id is the first 32 hex characters of SHA-256 of the venue id, a newline and the key, so a resend lands on the same
   item. It sends `{saleId, venueId, tillId}` to the fiscalise queue and answers 202.
3. FiscaliseWorker receives up to 10 messages per invocation, at most 5 invocations at once. For each it asks the
   partner to sign, writes `SIGNED` and the signature under a version check, and sends the sale id to the journal FIFO
   queue.
4. The journal worker on the laptop runs 16 consumers by default, each taking one message at a time. It reads the
   signed sale from DynamoDB, and `dbo.RecordSale` gives it the till's next receipt number in the transaction that
   journals it. The worker then writes the receipt HTML to S3, records the number on the sale, and emails the receipt
   through SES when the customer gave an address.
5. The till polls `GET /sales/{saleId}` until the receipt number arrives or the status is `FAILED`.

## Where each part runs

| Part | Runs on | Code |
| --- | --- | --- |
| Till | Browser, `ng serve` on localhost:4200 | [web/till/src/app](https://github.com/Zabedz/fiscal-till/tree/main/web/till/src/app) |
| API Gateway, usage plan, keys | AWS | [TillStack.cs](https://github.com/Zabedz/fiscal-till/blob/main/infra/Roller.Infra/TillStack.cs) |
| CreateSale, GetSale | Lambda, .NET 10 on arm64, 1024 MB | [src/Roller.Api](https://github.com/Zabedz/fiscal-till/tree/main/src/Roller.Api) |
| Sale model, VAT, validation, DynamoDB store | Library used by CreateSale, GetSale, FiscaliseWorker, the journal worker and the CDK app | [src/Roller.Core](https://github.com/Zabedz/fiscal-till/tree/main/src/Roller.Core) |
| FiscaliseWorker | Lambda with an SQS trigger | [src/Roller.Fiscalise](https://github.com/Zabedz/fiscal-till/tree/main/src/Roller.Fiscalise) |
| Mock partner | Lambda behind a function URL, with its own contracts | [src/Roller.MockPartner](https://github.com/Zabedz/fiscal-till/tree/main/src/Roller.MockPartner) |
| Journal worker | .NET worker service on the laptop, started with `dotnet run` | [src/Roller.Journal](https://github.com/Zabedz/fiscal-till/tree/main/src/Roller.Journal) |
| SQL Server 2022 | Docker on the laptop | [db/schema.sql](https://github.com/Zabedz/fiscal-till/blob/main/db/schema.sql), [db/compose.yaml](https://github.com/Zabedz/fiscal-till/blob/main/db/compose.yaml) |
| Infrastructure | CDK in C# | [infra/Roller.Infra](https://github.com/Zabedz/fiscal-till/tree/main/infra/Roller.Infra) |

## Stacks

| Stack | Holds | Lifetime |
| --- | --- | --- |
| RollerGuardrails | SNS topic `roller-alarms` with the email subscription, SES sender identity, $10 monthly budget | Kept, with termination protection, so the AWS confirmation emails are clicked once |
| RollerTill | Every other AWS resource: API, Lambdas, tables, queues, bucket, secrets, partner switches, alarms, dashboard ([list](aws-resources.md)) | Deleted by `scripts/teardown.sh` |

## Design choices

| Choice | Reason | Code |
| --- | --- | --- |
| Sale id derived from venue and `Idempotency-Key` | A resend finds the first copy; the conditional put decides which request was first. A stored hash of the request gives 422 to the same key with a different valid body. | `SaleIds.cs`, `SaleStore.PutIfAbsentAsync`, `CreateSaleService.cs` |
| Queue between taking a sale and signing it | The till gets 202 in about 100 ms whatever the partner is doing. Each country secures a sale before its receipt, so production signs at payment ([compliance.md](compliance.md)). | `CreateSaleService.cs` |
| Standard fiscalise queue, FIFO journal queue | The mock signs in any order. Numbering takes each till's sales one at a time, in the order they reach the journal queue. All three countries sign each till's sales in order, so production signs at payment. | `TillStack.cs` |
| Optimistic lock on `Status` and `Version` | Two workers holding copies of one message cannot both move the sale out of `PENDING`. | `SaleStore.MarkSignedAsync` |
| Partial batch failures | A failing sale is retried alone, leaving the other nine in its batch done. | `FiscaliseFunction.cs` |
| Backoff through message visibility | The wait happens on the queue at no compute cost. | `FiscaliseProcessor.cs`, `Backoff.cs` |
| Receipt numbers in SQL Server | A counter row locked per till gives gapless numbers, and any error rolls the increment back. | `dbo.RecordSale` |
| One message per receive in the journal worker | SQS counts every returned message as a receive, so a batch would dead-letter sales queued behind a failing one. | `JournalWorker.cs` |
| Every journal step but the email safe to repeat | SQL returns the existing number for a known sale, S3 writes with `If-None-Match: *`, and the DynamoDB update checks the number. The email goes twice if the worker dies between sending it and saving `EmailedAt`. | `JournalProcessor.cs` |

Known limits are in the [top-level README](https://github.com/Zabedz/fiscal-till#known-limits), and the gaps against each
country's rules in [compliance.md](compliance.md).
