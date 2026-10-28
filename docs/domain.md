# Domain

## Fiscal terms

| Term | Meaning here |
| --- | --- |
| Fiscalise | Have a sale signed by certified software or a certified device, so a missing or altered sale can be detected |
| NF525 | French certification for till software: the certified software signs and chains its own records. `FrenchRules` builds a mock payload. |
| KassenSichV, TSE | German till law, and the certified security device (TSE) it requires to sign each transaction. `GermanRules` builds a mock payload. |
| DSFinV-K | German export format; its VAT keys set the order of the DE payload's amounts |
| RKSV | Austrian till regulation: each receipt carries a signed code with the gross per VAT field. `AustrianRules` builds a mock one. |
| Signing device | The mock partner keeps one per till, with its own counter |
| Gapless receipt numbers | Each till's numbers run 1, 2, 3 with none skipped, so a deleted sale leaves a visible gap. `dbo.RecordSale` assigns them after signing; they are separate from the signing device's counter and can differ from it. |
| Z-report | End-of-day report for one venue and trading day: VAT per rate per till, then each receipt with a running total |

## Sale status

| Status | Set by | When |
| --- | --- | --- |
| PENDING | CreateSale | Stored, waiting to be signed |
| SIGNED | FiscaliseWorker | The partner signed it |
| FAILED | FiscaliseWorker | The partner answered 422 |

`Version` starts at 1 and goes up with each status change. A SIGNED sale later gains `ReceiptNumber`, `ReceiptKey` and
`JournaledAt`, plus `EmailedAt` once emailed; none of these change `Version`.

## API answers

| Status | When |
| --- | --- |
| 200 | GET for one of the venue's own sales |
| 202 | New sale stored and queued, or the same key and body sent again (header `Idempotent-Replayed: true`) |
| 400 | Missing or malformed `Idempotency-Key`, a body that is not a sale (bad JSON, or an unknown, missing or null field), or validation errors (listed in the body) |
| 403 | API key not mapped to a venue; API Gateway itself answers 403 to a missing or wrong key |
| 404 | GET for an unknown or malformed id, or another venue's sale |
| 422 | Key reused with a different valid body |
| 429 | API Gateway: over the key's usage-plan limits or the per-method limit ([values](aws-resources.md#till-api)) |
| 503 | CreateSale only: AWS throttling, an AWS 5xx error or running out of time, with `Retry-After: 1`. GetSale's AWS errors surface as 502. |

Validation runs before the idempotency check, so a changed body that is also invalid gets 400.

## VAT

Prices include VAT, in integer cents. VAT per line is `gross x rate / (100 + rate)`, rounded half away from zero
(`Math.Round` defaults to banker's rounding, so the code passes `MidpointRounding.AwayFromZero`). Totals add the
rounded lines. [config/markets.json](https://github.com/Zabedz/fiscal-till-pipeline/blob/main/config/markets.json) holds the rates:

| Market | Standard rate | Reduced rates | Dated changes |
| --- | --- | --- | --- |
| FR | 20: alcohol, merchandise | 10: admission, food, soft drinks on site; 5.5: staple goods | None |
| DE | 19: admission, soft drinks, alcohol, merchandise | 7: staple goods, takeaway | Food on site 19% until 2025-12-31, 7% from 2026-01-01 |
| AT | 20: soft drinks, alcohol, merchandise | 13: admission; 10: food | Staple goods 10% until 2026-06-30, 4.9% from 2026-07-01 |

DE and AT payloads have a fixed field per rate. A rate with no field (DE 10%, say) makes CreateSale, FiscaliseWorker
and the journal worker fail as they start, until the rules class handles it. The FR payload lists whatever rates a sale
has.

## Two dates per sale

| Date | Definition | Used for |
| --- | --- | --- |
| Business date | Trading day, 05:00 to 05:00 local | Z-report grouping, the `ByVenueDay` index key |
| Supply date | Local calendar date | Picking the VAT rate |

A sale at 01:30 on 1 January 2026 in Berlin belongs to business date 31 December 2025 and takes 2026's 7% food rate.
