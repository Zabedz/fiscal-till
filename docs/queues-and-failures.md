# Queues and failures

## The fiscalise queue

The SQS queue between taking a sale and signing it. CreateSale sends `{saleId, venueId, tillId}`; FiscaliseWorker
receives it and gets the sale signed. Signing is what NF525, KassenSichV and RKSV require of every sale (see
[domain.md](domain.md)). The queue lets the till keep selling while the partner is slow or down.

| Setting | Value |
| --- | --- |
| Type | Standard: delivered at least once, in any order |
| Visibility timeout | 90 s: AWS's guidance of six times the 15 s function timeout, which leaves room to retry a throttled batch |
| Receives before the DLQ | 10 |
| Retention | 14 days, and the same for the DLQ |
| Lambda trigger | Batches of up to 10, at most 5 invocations at once, partial batch failures on |

![Timeline to scale: a malformed message is received 10 times within seconds and dead-lettered; a sale queued as a 4-minute outage began fails 7 times and is signed at 255 seconds; in an outage that outlasts the 10th receive, all 10 receives fail and the message is dead-lettered.](diagrams/fiscalise-retries.svg)

### What happens to each message

| Case | Worker | Message | Sale |
| --- | --- | --- | --- |
| Partner signs (200) | `MarkSignedAsync` if `Status = PENDING` and `Version` matches, then send to the journal queue | Deleted | SIGNED |
| Partner rejects (422) | `MarkFailedAsync` | Deleted | FAILED |
| Partner unavailable: any status but 2xx or 422, no answer in 3 s, a connection error, an unusable body | Hide the message for 5, 10, 20, 40, then 60 s, each up to 20% longer | Retried | PENDING |
| Any other exception: DynamoDB, the journal send, the sale changed while signing | The same backoff | Retried | PENDING, or SIGNED if only the journal send failed |
| Body unreadable, or no such sale | Make it visible at once | DLQ within seconds | None |
| Sale already SIGNED | Send it to the journal queue again | Deleted | Unchanged |
| Sale already FAILED | Nothing | Deleted | Unchanged |
| Invocation out of time | Report it unfinished | Back after 90 s | Unchanged |

Code: [FiscaliseProcessor.cs](https://github.com/Zabedz/fiscal-till/blob/main/src/Roller.Fiscalise/FiscaliseProcessor.cs),
[Backoff.cs](https://github.com/Zabedz/fiscal-till/blob/main/src/Roller.Fiscalise/Backoff.cs), [PartnerClient.cs](https://github.com/Zabedz/fiscal-till/blob/main/src/Roller.Fiscalise/PartnerClient.cs).

- A sale's 10th receive comes 375 to 450 s after it was queued. If the partner is still down then, the message moves to
  the DLQ on the next receive, about a minute later, and needs a redrive.
- Duplicate deliveries are expected: the queue is standard, and CreateSale sends a PENDING sale again when its POST is
  replayed. The partner signs once per sale id, and the version check stops a second SIGNED write.

## The journal queue

FIFO, with message group `{venueId}-{tillId}` and deduplication id = sale id. While one of a group's messages is in
flight, SQS returns no other message from that group, and the worker asks for one message at a time. Each till's sales
are therefore journaled one at a time, in the order the fiscalise worker queued them. Signing runs concurrently, so that
order can differ from the partner's counter. Deduplication covers 5 minutes; after that the journal's unique `SaleId`
catches repeats.

| Setting or case | Value or result |
| --- | --- |
| Visibility timeout | 60 s |
| Receives before the DLQ | 5 |
| Worker | 16 consumers by default (`JOURNAL_CONSUMERS`, [JournalSettings.cs](https://github.com/Zabedz/fiscal-till/blob/main/src/Roller.Journal/JournalSettings.cs)), 1 message per receive, 20 s long poll ([JournalWorker.cs](https://github.com/Zabedz/fiscal-till/blob/main/src/Roller.Journal/JournalWorker.cs)) |
| Unreadable, or sale not SIGNED | Visible at once, DLQ within seconds |
| SQL deadlock victim (error 1205) | Retried in process, 3 attempts in all |
| Any other exception | Left undeleted, back after 60 s |

- A failing sale holds up its till's group until it succeeds or dead-letters.
- A redriven sale gets a later receipt number than sales queued after it.

## Dead-letter queues and their alarms

Each DLQ keeps messages 14 days and has an alarm that emails through SNS (see [aws-resources.md](aws-resources.md)).

1. Look at the messages: SQS console, the DLQ, "Send and receive messages", "Poll for messages". Polling raises their
   receive count, which is harmless in a DLQ.
2. Find the cause in the worker's log group, `RollerTill-FiscaliseWorkerLogs...`, or in the journal worker's console.
3. After fixing it, "Start DLQ redrive" moves the messages back to their source queue. Purge them instead when they are
   junk, like the poison drill's.
4. The alarm returns to OK a few minutes after the DLQ empties.
