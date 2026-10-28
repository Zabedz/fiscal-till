# Drills

![load/drill.sh sets the partner switches in SSM, runs k6 against API Gateway, sends bad messages straight to the fiscalise queue, and logs each drill's window for the dashboard.](diagrams/drills.svg)

`load/drill.sh <drill>` runs one drill from the laptop. On exit, including after a failure, it stops any background k6
and sets the partner back to healthy. A drill that completes appends its window to `load/drills.json`: k6's start and
end, or for `outage` the minutes the partner was down. The next `cdk deploy RollerTill` draws each window on every
dashboard graph, blue at the start and grey at the end.

| Drill | What drill.sh does | What it exercises |
| --- | --- | --- |
| `baseline` | k6 for 10 minutes, ramping to 10 sales/s across 50 tills, 7% resent under the same key | The normal path and idempotent replays |
| `slow` | `partner.sh slow 2000`, then k6 for 5 minutes ramping to 20 sales/s | Signing throughput with a slow partner |
| `outage` | k6 at up to 10 sales/s for 8 minutes; `partner.sh outage` at minute 2, `healthy` at minute 6 | Backoff on the queue and recovery |
| `poison` | `aws sqs send-message` puts 5 bad messages straight on the fiscalise queue, then waits for the DLQ | Dead-lettering, the DLQ alarm and its email |
| `burst` | k6 sends paris-01 150 requests/s for a minute, and Berlin and Vienna 4/s between them | The per-key usage plan limit |

The poison messages: `not json`, `{"saleId":42}`, `{"venueId":"paris-01"}`, and two well-formed messages for sale ids
that do not exist.

## Partner switches

[scripts/partner.sh](https://github.com/Zabedz/fiscal-till/blob/main/scripts/partner.sh) writes `{latencyMs, errorRate, outage}` to the SSM parameter
`/roller/partner/switches`. Each partner instance caches it for 5 s.

| Switch | Partner behaviour |
| --- | --- |
| `outage` | 503 to every request, before signing |
| `errors <rate>` | 500 to that share of requests, before signing |
| `slow <ms>` | Signs, then waits before answering, retries included. Above about 3 s every call times out at the worker, so the sales stay PENDING and dead-letter. |

## After the drills

`python3 scripts/verify.py` refuses to run until both queues are empty. It writes `load/results/summary.json`, which
git ignores, and exits 1 if an invariant fails.

| Check | Compares |
| --- | --- |
| Nothing lost or added | k6's accepted count per run with the DynamoDB sales carrying that run's key prefix |
| Receipt numbers agree | Each sale's number in DynamoDB with the journal's |
| Gapless | `dbo.ReceiptGaps` per till, and the DynamoDB numbers per till |
| VAT | Gross and VAT per sale and rate, journal against DynamoDB |
| Nothing stuck | PENDING, or SIGNED without a receipt, for over 10 minutes |
| Timings | For sales made inside each drill's window: seconds to the partner's signature and to the receipt number |
| Cold starts | Logs Insights over each Lambda's `platform.report` records |

Results and findings are in the [top-level README](https://github.com/Zabedz/fiscal-till#results).
