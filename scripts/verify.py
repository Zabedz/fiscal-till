#!/usr/bin/env python3
"""Checks the pipeline's invariants in DynamoDB and the SQL journal, times each drill, and writes load/results/summary.json."""

import glob
import json
import os
import re
import subprocess
import sys
import time
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from decimal import Decimal

REGION = "eu-west-2"
STUCK_AFTER = timedelta(minutes=10)
QUERY_POLL_SECONDS = 2
# Lambda's JSON log format writes each invocation's report as a platform.report record; only cold starts carry initDurationMs.
COLD_START_QUERY = """filter type = "platform.report"
| stats count(*) as invocations, count(record.metrics.initDurationMs) as coldStarts,
        pct(record.metrics.initDurationMs, 50) as initMsP50, pct(record.metrics.initDurationMs, 95) as initMsP95,
        max(record.metrics.initDurationMs) as initMsMax by @log"""


def aws(*args):
    result = subprocess.run(["aws", *args, "--region", REGION, "--output", "json"], check=True, capture_output=True, text=True)
    return json.loads(result.stdout) if result.stdout.strip() else None


def stack_output(key):
    outputs = aws("cloudformation", "describe-stacks", "--stack-name", "RollerTill")["Stacks"][0]["Outputs"]
    return next(entry["OutputValue"] for entry in outputs if entry["OutputKey"] == key)


def sql(query):
    """Runs a read-only query as sa inside the container (the password comes from the container's SQLCMDPASSWORD)."""
    result = subprocess.run(
        ["docker", "compose", "--env-file", ".env", "-f", "db/compose.yaml", "exec", "-T", "sql",
         "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-C", "-I", "-b", "-d", "RollerJournal",
         "-W", "-s", "|", "-h", "-1", "-Q", f"SET NOCOUNT ON; {query}"],
        check=True, capture_output=True, text=True)
    return [line.split("|") for line in result.stdout.splitlines() if line.strip()]


def instant(value):
    return datetime.fromisoformat(value)


def percentile(values, share):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, round(share * (len(ordered) - 1)))]


def seconds(values):
    return {"count": len(values), "p50": percentile(values, 0.5), "p95": percentile(values, 0.95), "max": max(values) if values else None}


def rate_text(value):
    """A VAT rate as plain text (10, 5.5), the same whichever store it came from."""
    return format(Decimal(value).normalize(), "f")


def vat_by_rate(lines):
    totals = defaultdict(lambda: [0, 0])
    for line in lines:
        rate = rate_text(line["M"]["VatRate"]["N"])
        totals[rate][0] += int(line["M"]["GrossCents"]["N"])
        totals[rate][1] += int(line["M"]["VatCents"]["N"])
    return dict(totals)


def queued_sales():
    """Messages still in the fiscalise and journal queues, waiting or in flight."""
    counts = {}
    for name in ("FiscaliseQueueUrl", "JournalQueueUrl"):
        attributes = aws("sqs", "get-queue-attributes", "--queue-url", stack_output(name), "--attribute-names",
                         "ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible")["Attributes"]
        counts[name] = sum(int(value) for value in attributes.values())
    return counts


def load_sales(table):
    sales = []
    for item in aws("dynamodb", "scan", "--table-name", table, "--consistent-read")["Items"]:
        sales.append({
            "saleId": item["SaleId"]["S"],
            "venue": item["VenueId"]["S"],
            "till": item["TillId"]["S"],
            "businessDate": item["BusinessDate"]["S"],
            "key": item["IdempotencyKey"]["S"],
            "status": item["Status"]["S"],
            "createdAt": instant(item["CreatedAt"]["S"]),
            "signedAt": instant(item["SignedAt"]["S"]) if "SignedAt" in item else None,
            "journaledAt": instant(item["JournaledAt"]["S"]) if "JournaledAt" in item else None,
            "receipt": int(item["ReceiptNumber"]["N"]) if "ReceiptNumber" in item else None,
            "vatByRate": vat_by_rate(item["Lines"]["L"]),
        })
    return sales


def load_journal():
    """Each journaled sale's till, receipt number and per-rate totals, keyed by sale id."""
    entries = {}
    for sale_id, till_key, receipt in sql("SELECT SaleId, TillKey, ReceiptNo FROM dbo.JournalEntry WHERE VenueId NOT LIKE 'test-%'"):
        entries[sale_id] = {"till": till_key, "receipt": int(receipt), "vatByRate": {}}
    rows = sql("""SELECT e.SaleId, l.VatRate, SUM(l.GrossCents), SUM(l.VatCents)
                  FROM dbo.JournalEntry e JOIN dbo.JournalLine l ON l.JournalEntryId = e.JournalEntryId
                  WHERE e.VenueId NOT LIKE 'test-%' GROUP BY e.SaleId, l.VatRate""")
    for sale_id, rate, gross, vat in rows:
        entries[sale_id]["vatByRate"][rate_text(rate)] = [int(gross), int(vat)]
    return entries


def check_sales(sales, journal, k6_runs):
    accepted_vs_stored = {}
    for run, accepted in k6_runs.items():
        stored = sum(1 for sale in sales if sale["key"].startswith(f"{run}-"))
        accepted_vs_stored[run] = {"acceptedByK6": accepted, "storedInDynamoDb": stored}

    numbered = [sale for sale in sales if sale["receipt"] is not None]
    disagree = [sale["saleId"] for sale in numbered
                if (entry := journal.get(sale["saleId"])) is None
                or (entry["till"], entry["receipt"]) != (f"{sale['venue']}-{sale['till']}", sale["receipt"])]
    stored_ids = {sale["saleId"] for sale in sales}
    return {
        "acceptedVsStored": accepted_vs_stored,
        "receiptNumberDiffersFromJournal": disagree,
        # Sales journaled under an earlier deployment whose sales table has since been deleted.
        "journalOnlySales": sum(1 for sale_id in journal if sale_id not in stored_ids),
    }


def check_gaps(sales):
    sql_gaps = [
        {"till": row[0], "receipts": int(row[1]), "lastReceiptNo": int(row[2]), "missing": int(row[3])}
        for row in sql("SELECT TillKey, Receipts, LastReceiptNo, MissingNumbers FROM dbo.ReceiptGaps WHERE TillKey NOT LIKE 'test-%' ORDER BY TillKey")
    ]
    numbers = defaultdict(list)
    for sale in sales:
        if sale["receipt"] is not None:
            numbers[f"{sale['venue']}-{sale['till']}"].append(sale["receipt"])
    # From each till's lowest number: after a redeploy the sales table restarts while the journal's counters carry on.
    dynamo_gaps = {till: sorted(set(range(min(held), max(held) + 1)) - set(held)) for till, held in numbers.items()}
    repeated = {till: len(held) - len(set(held)) for till, held in numbers.items() if len(held) != len(set(held))}
    return {
        "tills": len(sql_gaps),
        "tillsWithMissingNumbers": [gap for gap in sql_gaps if gap["missing"] != 0],
        "dynamoTillsWithMissingNumbers": {till: missing[:10] for till, missing in dynamo_gaps.items() if missing},
        "dynamoRepeatedNumbers": repeated,
        "receiptsInJournal": sum(gap["receipts"] for gap in sql_gaps),
    }


def check_vat(sales, journal):
    """Per sale and rate, the journal's gross and VAT equal DynamoDB's, so the Z-report sums what the till sold."""
    compared = [sale for sale in sales if sale["saleId"] in journal]
    mismatches = [
        {"saleId": sale["saleId"], "dynamo": sale["vatByRate"], "journal": journal[sale["saleId"]]["vatByRate"]}
        for sale in compared if sale["vatByRate"] != journal[sale["saleId"]]["vatByRate"]
    ]
    return {
        "salesCompared": len(compared),
        "venueDays": len({(sale["venue"], sale["businessDate"]) for sale in compared}),
        "mismatchCount": len(mismatches),
        "mismatches": mismatches[:20],
    }


def check_stuck(sales, now):
    pending = [sale["saleId"] for sale in sales if sale["status"] == "PENDING" and now - sale["createdAt"] > STUCK_AFTER]
    unjournaled = [sale["saleId"] for sale in sales if sale["status"] == "SIGNED" and sale["receipt"] is None and now - sale["createdAt"] > STUCK_AFTER]
    failed = [sale["saleId"] for sale in sales if sale["status"] == "FAILED"]
    return {"pendingOlderThan10Minutes": pending, "signedWithoutReceiptOlderThan10Minutes": unjournaled, "failed": failed}


def drill_timings(sales, drills):
    timings = []
    for drill in drills:
        start, end = instant(drill["start"]), instant(drill["end"])
        # For the outage, the sales of interest are those made while the partner was down.
        window = [sale for sale in sales if start <= sale["createdAt"] <= end]
        timings.append({
            "drill": drill["name"],
            "start": drill["start"],
            "end": drill["end"],
            "sales": len(window),
            # The partner stamps SignedAt before any injected delay, so this leaves out the slow drill's 2 s answer time.
            "secondsToPartnerSignature": seconds([(sale["signedAt"] - sale["createdAt"]).total_seconds() for sale in window if sale["signedAt"]]),
            "secondsToReceipt": seconds([(sale["journaledAt"] - sale["createdAt"]).total_seconds() for sale in window if sale["journaledAt"]]),
        })
    return timings


def cold_starts(since, until):
    resources = aws("cloudformation", "describe-stack-resources", "--stack-name", "RollerTill")["StackResources"]
    groups = [resource["PhysicalResourceId"] for resource in resources
              if resource["ResourceType"] == "AWS::Logs::LogGroup" and not resource["LogicalResourceId"].startswith("AutoDelete")]
    query_id = aws("logs", "start-query", "--log-group-names", *groups, "--start-time", str(int(since.timestamp())),
                   "--end-time", str(int(until.timestamp())), "--query-string", COLD_START_QUERY)["queryId"]
    while (response := aws("logs", "get-query-results", "--query-id", query_id))["status"] in ("Scheduled", "Running"):
        time.sleep(QUERY_POLL_SECONDS)
    if response["status"] != "Complete":
        raise RuntimeError(f"Logs Insights query {query_id} ended {response['status']}")

    functions = {}
    for row in response["results"]:
        fields = {field["field"]: field["value"] for field in row}
        # @log is account:group; keep the function's name from RollerTill-<Function>Logs<hash>-<suffix>.
        name = re.match(r"RollerTill-(\w+?)Logs", fields.pop("@log").split(":")[-1]).group(1)
        functions[name] = {key: round(float(value)) for key, value in fields.items()}
    return functions


def k6_results():
    runs = {}
    details = {}
    for path in sorted(glob.glob("load/results/*.json")):
        name = os.path.basename(path)[:-5]
        if name == "summary" or name.startswith("smoke-"):
            continue
        metrics = json.load(open(path))["metrics"]

        def value(metric, field):
            return metrics.get(metric, {}).get("values", {}).get(field)

        runs[name] = int(value("sales_accepted", "count") or 0)
        details[name] = {
            "requests": value("http_reqs", "count"),
            "accepted": value("sales_accepted", "count"),
            "resent": value("sales_resent", "count"),
            "throttled": value("sales_throttled", "count"),
            "checksPassed": value("checks", "rate"),
            "createSaleMs": {"p50": value("http_req_duration", "med"), "p95": value("http_req_duration", "p(95)"), "max": value("http_req_duration", "max")},
        }
    return runs, details


def main():
    os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
    os.environ.setdefault("AWS_PROFILE", "roller")
    # The invariants hold once the pipeline is idle; mid-flight, the journal is ahead of the sales table.
    busy = queued_sales()
    if any(busy.values()):
        sys.exit(f"pipeline still busy, run again once both queues are empty: {busy}")
    now = datetime.now(timezone.utc)
    drills = json.load(open("load/drills.json")) if os.path.exists("load/drills.json") else []
    sales = load_sales(stack_output("SalesTableName"))
    journal = load_journal()
    k6_runs, k6_details = k6_results()

    summary = {
        "checkedAt": now.isoformat(),
        "salesInDynamoDb": len(sales),
        "sales": check_sales(sales, journal, k6_runs),
        "gapless": check_gaps(sales),
        "vatMatchesDynamoDb": check_vat(sales, journal),
        "stuck": check_stuck(sales, now),
        "drills": drill_timings(sales, drills),
        "coldStarts": cold_starts(instant(drills[0]["start"]), now) if drills else {},
        "k6": k6_details,
    }
    with open("load/results/summary.json", "w") as file:
        json.dump(summary, file, indent=2, default=str)

    checked = summary["sales"]
    gaps = summary["gapless"]
    vat = summary["vatMatchesDynamoDb"]
    stuck = summary["stuck"]
    lost = {run: counts for run, counts in checked["acceptedVsStored"].items() if counts["acceptedByK6"] != counts["storedInDynamoDb"]}
    print(f"sales in DynamoDB: {len(sales)}; journal-only sales: {checked['journalOnlySales']}")
    print(f"runs where k6 and DynamoDB disagree: {lost or 'none'}; "
          f"receipt numbers differing from the journal: {len(checked['receiptNumberDiffersFromJournal'])}")
    print(f"gapless: {gaps['tills']} tills, {gaps['receiptsInJournal']} receipts, tills with missing numbers: "
          f"{len(gaps['tillsWithMissingNumbers'])} (SQL), {len(gaps['dynamoTillsWithMissingNumbers'])} (DynamoDB); "
          f"tills with repeated numbers: {len(gaps['dynamoRepeatedNumbers'])}")
    print(f"VAT per sale and rate, journal vs DynamoDB: {vat['salesCompared']} sales, {vat['venueDays']} venue days, "
          f"mismatches: {vat['mismatchCount']}")
    print(f"stuck: {json.dumps({key: len(value) for key, value in stuck.items()})}")
    for drill in summary["drills"]:
        signed, receipt = drill["secondsToPartnerSignature"], drill["secondsToReceipt"]
        print(f"{drill['drill']}: {drill['sales']} sales; to partner signature p50 {signed['p50']} p95 {signed['p95']} max {signed['max']} s; "
              f"to receipt p50 {receipt['p50']} p95 {receipt['p95']} max {receipt['max']} s")
    for function, counts in summary["coldStarts"].items():
        print(f"cold starts {function}: {counts}")
    for run, detail in k6_details.items():
        print(f"k6 {run}: {detail}")
    # FAILED sales are partner rejections, an expected outcome; everything else checked here is an invariant.
    broken = (lost or checked["receiptNumberDiffersFromJournal"] or gaps["tillsWithMissingNumbers"] or gaps["dynamoTillsWithMissingNumbers"]
              or gaps["dynamoRepeatedNumbers"] or vat["mismatchCount"] or stuck["pendingOlderThan10Minutes"]
              or stuck["signedWithoutReceiptOlderThan10Minutes"])
    sys.exit(1 if broken else 0)


if __name__ == "__main__":
    main()
