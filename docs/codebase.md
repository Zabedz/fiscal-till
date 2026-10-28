# Codebase

| Path | Contents |
| --- | --- |
| `roller.slnx` | Solution: 1 infra, 5 src and 4 test projects |
| `Directory.Build.props` | Every project: net10.0, nullable reference types on, nullable warnings as errors |
| `Directory.Packages.props` | Package versions for every project (central package management) |
| `global.json` | SDK 10.0.403; `dotnet test` uses Microsoft.Testing.Platform |
| `config/venues.json` | Three venues: id, market, time zone, 20 tills each |
| `config/markets.json` | Per market: currency, locale, VAT rate per category with start dates |
| `src/Roller.Core` | Sale model, validation, VAT and fiscal payloads, DynamoDB store; `config/*.json` is embedded in this DLL |
| `src/Roller.Api` | CreateSale and GetSale handlers |
| `src/Roller.Fiscalise` | FiscaliseWorker handler, partner client, backoff |
| `src/Roller.MockPartner` | Mock signing partner |
| `src/Roller.Journal` | Journal worker that runs on the laptop |
| `infra/Roller.Infra` | CDK app |
| `db` | SQL Server schema and Docker Compose file |
| `web/till` | Angular till |
| `load` | k6 script, drill runner, drill windows |
| `scripts` | Lambda publish (cdk's build step), teardown, local database, journal worker, partner switches, till config, checks |
| `tests` | xUnit v3, one project per src project except MockPartner |

## Key files

| File | Job |
| --- | --- |
| `src/Roller.Core/SaleIds.cs` | Sale id and request hash |
| `src/Roller.Core/SaleValidator.cs` | Input rules |
| `src/Roller.Core/MarketConfig.cs`, `MarketRules.cs` | Rate lookup by date, pricing |
| `src/Roller.Core/FrenchRules.cs`, `GermanRules.cs`, `AustrianRules.cs` | Mock NF525, TSE and RKSV payloads |
| `src/Roller.Core/MarketRulesRegistry.cs` | Pairs each market in the config with its rules class; throws when a Lambda or the journal worker starts if they disagree |
| `src/Roller.Core/BusinessDay.cs` | Trading day and supply date |
| `src/Roller.Core/Vat.cs` | VAT from a gross amount |
| `src/Roller.Core/SaleStore.cs` | Every DynamoDB read and write of a sale |
| `src/Roller.Core/StrictJson.cs` | JSON that rejects unknown, missing and null fields |
| `src/Roller.Api/CreateSaleFunction.cs`, `CreateSaleService.cs` | HTTP mapping, and the create logic |
| `src/Roller.Api/VenueKeys.cs` | API key id to venue |
| `src/Roller.Fiscalise/FiscaliseFunction.cs`, `FiscaliseProcessor.cs` | The batch, and one message |
| `src/Roller.Fiscalise/PartnerClient.cs`, `Backoff.cs` | Partner call and result, retry delay |
| `src/Roller.MockPartner/SigningDevice.cs` | Counter per till, advanced in a DynamoDB transaction |
| `src/Roller.MockPartner/Switches.cs` | Drill switches from SSM |
| `src/Roller.Journal/JournalWorker.cs`, `JournalProcessor.cs` | Consumer loops, and one sale |
| `src/Roller.Journal/JournalDb.cs` | `dbo.RecordSale` call and deadlock retry |
| `src/Roller.Journal/ReceiptDelivery.cs`, `ReceiptDocument.cs` | S3 and SES, receipt HTML |
| `db/schema.sql` | `TillCounter`, `JournalEntry`, `JournalLine`, `RecordSale`, `ZReport`, `ReceiptGaps`, `journal_app` login |
| `infra/Roller.Infra/TillStack.cs`, `GuardrailsStack.cs`, `TillDashboard.cs` | The stacks and the dashboard |
| `web/till/src/app/app.ts`, `till-api.ts` | Till screen, and the API calls with retries |

## Scripts

| Command | Does |
| --- | --- |
| `scripts/publish-lambdas.sh` | Publishes the three Lambda projects to `artifacts/`; `cdk` runs it before synth |
| `scripts/teardown.sh` | Deletes RollerTill through CloudFormation |
| `scripts/journal-db.sh` with `up`, `test`, `sql` or `down` | Local SQL Server: start and apply the schema, run tests against it, open a shell, stop |
| `scripts/journal.sh` | Journal worker. Reads the RollerTill outputs, `.env` (from `journal-db.sh up`) and `alertEmail` in `cdk.context.json` as the SES sender. |
| `scripts/partner.sh` with `healthy`, `slow <ms>`, `errors <rate>` or `outage` | Partner switches |
| `scripts/till-config.sh` | Writes the till's `config.json` with the API URL and key values |
| `load/drill.sh <drill>` | One drill |
| `python3 scripts/verify.py` | Invariant checks after the drills |

## Tests

| Project | Covers | Cases |
| --- | --- | --- |
| `Roller.Core.Tests` | Business day, rate config, rules registry, payloads, sale ids, DynamoDB mapping, validation, VAT | 59 |
| `Roller.Api.Tests` | HTTP statuses, idempotent replays, key reuse, 20 concurrent retries against an in-memory store | 20 |
| `Roller.Fiscalise.Tests` | Backoff schedule, partial batch failures, each partner outcome | 30 |
| `Roller.Journal.Tests` | Journal processor, receipt HTML, and 6 tests against SQL Server | 17 |

`dotnet test` skips the SQL Server tests unless `JOURNAL_DB` is set; `scripts/journal-db.sh test` sets it. The Angular
till has no unit tests. Run `dotnet test` before deploying a change to `config/markets.json`: `cdk deploy` does not load
it, and a bad entry only fails when the Lambdas start.

## Local files kept out of git

`.env` (database passwords), `cdk.context.json` (alert email), `web/till/public/config.json` (API key values),
`load/results/`, `artifacts/`, `cdk.out/`.
