# How to use the Dune Transactions API

This guide explains how a reconciliation process can use the Dune Transactions API to get the list of transactions created by a tenant's users, and how to catch up, hour after hour, with transactions that were recorded late.

The API has two endpoints:

| Endpoint | Purpose |
|---|---|
| `GET /v1/reconciliations/transactions` | Initial pass: transactions created in a date range |
| `GET /v1/reconciliations/transactions/missings` | Catch-up passes: transactions in the same date range that were recorded **after** the previous pass |

## Contents

1. [Before you start](#1-before-you-start)
2. [Conventions shared by both endpoints](#2-conventions-shared-by-both-endpoints)
3. [`GET /v1/reconciliations/transactions`](#3-get-v1reconciliationstransactions)
4. [`GET /v1/reconciliations/transactions/missings`](#4-get-v1reconciliationstransactionsmissings)
5. [Walkthrough: reconcile a day, then catch up every hour](#5-walkthrough-reconcile-a-day-then-catch-up-every-hour)
6. [Ready-to-use script](#6-ready-to-use-script)
7. [Errors](#7-errors)
8. [Pitfalls and FAQ](#8-pitfalls-and-faq)

---

## 1. Before you start

- **Base URL**: `http://localhost:8080` when the stack runs locally (`docker compose up --build`). The examples below use this URL.
- **Authentication**: none for now. Any caller can query any tenant.
- **Method and format**: both endpoints are `GET` requests. Every parameter goes in the query string, and responses are JSON (`application/json`).
- **Health check**: `GET /health` returns `200` when the API and its database are up, and `503` when the database cannot be reached.

## 2. Conventions shared by both endpoints

### Dates

- Dates use the ISO 8601 format **with an offset**, for example `2026-11-20T00:00:00Z` or `2026-11-20T01:00:00+01:00`. Any offset is accepted.
- **Always give an offset.** A date without one (`2026-11-20T00:00:00`) is read in the server's time zone, which you do not control.
- The API returns every date **in UTC** (`+00:00`). The original offset of a date is not stored.

### URL-encode the `+` sign

In a query string, `+` is decoded as a space. A date such as `2026-11-20T00:00:00+01:00` sent as is reaches the API as `2026-11-20T00:00:00 01:00` and is rejected with `400`. Choose one of these options:

- encode `+` as `%2B`: `fromCreatedAt=2026-11-20T00:00:00%2B01:00`;
- use UTC dates (`Z`), which have no `+`;
- let your HTTP client encode the parameters, for example `curl -G --data-urlencode` (used throughout this guide), or `Uri.EscapeDataString` in .NET.

### Date ranges: start included, end excluded

Every date range of the API includes its start and excludes its end:

| Range | Meaning |
|---|---|
| `[fromCreatedAt, toCreatedAt)` | `fromCreatedAt <= createdAt < toCreatedAt` |
| `asOf` (on `/transactions`) | `insertedAt < asOf` |
| `[missingsSince, asOf)` (on `/missings`) | `missingsSince <= insertedAt < asOf` |

Two consecutive ranges therefore neither overlap nor leave a gap. For example, `[Nov 19, Nov 20)` followed by `[Nov 20, Nov 21)` covers two full days, and a transaction created at exactly `2026-11-20T00:00:00Z` belongs to the second day only.

### Two different dates

| Date | Meaning | Filtered by |
|---|---|---|
| `createdAt` | When the transaction was created (business date), returned as `transactionCreatedAt` | `fromCreatedAt`, `toCreatedAt` |
| `insertedAt` | When the transaction was recorded in the database (set by the database clock, never returned by the API) | `asOf`, `missingsSince` |

A transaction can be recorded long after being created, for example when a batch arrives late. The `/missings` endpoint exists for these transactions.

### Pagination

- `page` starts at `1` (default `1`). `pageSize` is between `1` and `1000` (default `1000`).
- **A page with fewer than `pageSize` items, possibly empty, is the last one.** A page beyond the end returns `200` with an empty list, never `404`.
- Results are sorted by `createdAt`, then `offerId`. The second key only breaks ties between transactions created at the same instant (possibly by the same user). `offerId` is an opaque string, so this order has no business meaning.
- **Send the same `asOf` for every page of a pass.** Transactions recorded while you are paging then cannot shift the pages, so you never get a transaction twice or miss one.

### Response format

Both endpoints return the same format:

```json
{
  "transactions": [
    {
      "tenantUserId": "alice",
      "offerId": "offer-101",
      "buyOrSell": "Buy",
      "amount": "5000.00",
      "amountCcy": "EUR",
      "accountAlias": "123-abc-567",
      "transactionCreatedAt": "2026-11-19T08:00:00+00:00"
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `tenantUserId` | The tenant's user who made the transaction. A user can have several transactions. |
| `offerId` | Identifies the transaction. **Unique across the whole database**, whatever the tenant. |
| `buyOrSell` | `"Buy"` or `"Sell"`, nothing else. |
| `amount` | A **string**, returned exactly as recorded: `"5000.00"`, `"5000"` and `"1234.5"` come back unchanged. It is always a strictly positive decimal number with a `.` separator and at most 8 decimals, with no sign, exponent or thousands separator. Parse it with a decimal type, never with a floating-point type. |
| `amountCcy` | ISO 4217 currency code, in upper case (`EUR`). |
| `accountAlias` | Alias of the account. |
| `transactionCreatedAt` | `createdAt`, in UTC. |

An unknown tenant, or a range with no transaction, returns `200` with `"transactions": []`.

The examples below shorten some items to `{ "offerId": …, "transactionCreatedAt": … }` for readability. The API always returns all seven fields.

---

## 3. `GET /v1/reconciliations/transactions`

Returns the transactions of a tenant created in `[fromCreatedAt, toCreatedAt)` and recorded in the database **before** `asOf`.

### Parameters

| Parameter | Required | Default | Rules |
|---|---|---|---|
| `tenantId` | yes | — | Not empty, 100 characters max |
| `fromCreatedAt` | yes | — | ISO 8601 date with offset, **included** |
| `toCreatedAt` | yes | — | ISO 8601 date with offset, **excluded**, strictly after `fromCreatedAt` |
| `asOf` | yes | — | ISO 8601 date with offset, **excluded**: only transactions with `insertedAt < asOf` are returned |
| `page` | no | `1` | Between `1` and `1000000` |
| `pageSize` | no | `1000` | Between `1` and `1000` |

### Choosing `asOf`

`asOf` freezes the pass. Every transaction recorded from `asOf` onwards is left out, and the next `/missings` call will return it.

- **Use "now minus one minute"**, taken once at the start of the pass. The one-minute margin absorbs clock differences between your machine and the database, as well as insertions still in progress (see [Pitfalls](#8-pitfalls-and-faq)).
- **Use the same value for every page** of the pass.
- **Keep it**: it becomes the `missingsSince` of the next `/missings` call.

### Example: one day in UTC

```bash
curl -G "http://localhost:8080/v1/reconciliations/transactions" \
  --data-urlencode "tenantId=acme" \
  --data-urlencode "fromCreatedAt=2026-11-19T00:00:00Z" \
  --data-urlencode "toCreatedAt=2026-11-20T00:00:00Z" \
  --data-urlencode "asOf=2026-11-20T06:00:00Z"
```

This is the same request as a raw URL:

```
GET /v1/reconciliations/transactions?tenantId=acme&fromCreatedAt=2026-11-19T00:00:00Z&toCreatedAt=2026-11-20T00:00:00Z&asOf=2026-11-20T06:00:00Z
```

Response `200`:

```json
{
  "transactions": [
    { "tenantUserId": "alice", "offerId": "offer-101", "buyOrSell": "Buy",  "amount": "5000.00", "amountCcy": "EUR", "accountAlias": "123-abc-567", "transactionCreatedAt": "2026-11-19T08:00:00+00:00" },
    { "tenantUserId": "bob",   "offerId": "offer-102", "buyOrSell": "Sell", "amount": "1250.5",  "amountCcy": "EUR", "accountAlias": "234-bcd-678", "transactionCreatedAt": "2026-11-19T14:30:00+00:00" },
    { "tenantUserId": "carol", "offerId": "offer-103", "buyOrSell": "Buy",  "amount": "300",     "amountCcy": "USD", "accountAlias": "345-cde-789", "transactionCreatedAt": "2026-11-19T23:10:00+00:00" }
  ]
}
```

### Example: reading every page

With `pageSize=2` and the same `asOf` on every page:

```bash
# page 1 → 2 items: maybe more
curl -G "http://localhost:8080/v1/reconciliations/transactions" \
  --data-urlencode "tenantId=acme" \
  --data-urlencode "fromCreatedAt=2026-11-19T00:00:00Z" \
  --data-urlencode "toCreatedAt=2026-11-20T00:00:00Z" \
  --data-urlencode "asOf=2026-11-20T06:00:00Z" \
  --data-urlencode "page=1" \
  --data-urlencode "pageSize=2"
# {"transactions":[{"tenantUserId":"alice","offerId":"offer-101",...},{"tenantUserId":"bob","offerId":"offer-102",...}]}

# page 2 → 1 item (< pageSize): this is the last page
curl -G "http://localhost:8080/v1/reconciliations/transactions" \
  --data-urlencode "tenantId=acme" \
  --data-urlencode "fromCreatedAt=2026-11-19T00:00:00Z" \
  --data-urlencode "toCreatedAt=2026-11-20T00:00:00Z" \
  --data-urlencode "asOf=2026-11-20T06:00:00Z" \
  --data-urlencode "page=2" \
  --data-urlencode "pageSize=2"
# {"transactions":[{"tenantUserId":"carol","offerId":"offer-103",...}]}
```

If the last page is full, as with 4 transactions and `pageSize=2`, the next page comes back empty, and that empty page ends the pass.

### Example: a day in another time zone

To reconcile November 19 in Paris time (`+01:00`), express the range with that offset. `curl --data-urlencode` encodes the `+`:

```bash
curl -G "http://localhost:8080/v1/reconciliations/transactions" \
  --data-urlencode "tenantId=acme" \
  --data-urlencode "fromCreatedAt=2026-11-19T00:00:00+01:00" \
  --data-urlencode "toCreatedAt=2026-11-20T00:00:00+01:00" \
  --data-urlencode "asOf=2026-11-20T07:00:00+01:00"
```

The range is `[Nov 18 23:00Z, Nov 19 23:00Z)`, so `offer-103` (created at `23:10Z`) is now outside it. Returned dates are still in UTC:

```json
{
  "transactions": [
    { "offerId": "offer-101", "transactionCreatedAt": "2026-11-19T08:00:00+00:00", … },
    { "offerId": "offer-102", "transactionCreatedAt": "2026-11-19T14:30:00+00:00", … }
  ]
}
```

The same request as a raw URL, with `+` encoded by hand:

```
GET /v1/reconciliations/transactions?tenantId=acme&fromCreatedAt=2026-11-19T00:00:00%2B01:00&toCreatedAt=2026-11-20T00:00:00%2B01:00&asOf=2026-11-20T07:00:00%2B01:00
```

---

## 4. `GET /v1/reconciliations/transactions/missings`

Returns the transactions of a tenant created in `[fromCreatedAt, toCreatedAt)` and recorded in the database in `[missingsSince, asOf)`. These are the transactions a previous pass over the same range could not see, because they were recorded after its `asOf`.

### Parameters

The same parameters as `/transactions`, with the same rules, plus `missingsSince`:

| Parameter | Required | Default | Rules |
|---|---|---|---|
| `tenantId` | yes | — | Not empty, 100 characters max |
| `fromCreatedAt` | yes | — | ISO 8601 date with offset, **included** |
| `toCreatedAt` | yes | — | ISO 8601 date with offset, **excluded**, strictly after `fromCreatedAt` |
| `missingsSince` | yes | — | ISO 8601 date with offset, **included**: only transactions with `insertedAt >= missingsSince` are returned. Normally the `asOf` of the previous pass |
| `asOf` | yes | — | ISO 8601 date with offset, **excluded**: only transactions with `insertedAt < asOf` are returned. Strictly after `missingsSince` |
| `page` | no | `1` | Between `1` and `1000000` |
| `pageSize` | no | `1000` | Between `1` and `1000` |

### The chaining rule

> **The `missingsSince` of a call is the `asOf` of the previous call** (the initial pass, or the previous `/missings` call) **on the same tenant and the same date range.**

The recorded-at windows `[…, A1)`, `[A1, A2)`, `[A2, A3)`… then follow each other with no overlap and no gap. Taken together, the passes return every transaction of the range exactly once, up to the last `asOf`.

### Example

The initial pass used `asOf=2026-11-20T06:00:00Z`. One hour later:

```bash
curl -G "http://localhost:8080/v1/reconciliations/transactions/missings" \
  --data-urlencode "tenantId=acme" \
  --data-urlencode "fromCreatedAt=2026-11-19T00:00:00Z" \
  --data-urlencode "toCreatedAt=2026-11-20T00:00:00Z" \
  --data-urlencode "missingsSince=2026-11-20T06:00:00Z" \
  --data-urlencode "asOf=2026-11-20T07:00:00Z"
```

Response `200`:

```json
{
  "transactions": [
    { "tenantUserId": "alice", "offerId": "offer-105", "buyOrSell": "Sell", "amount": "2000.00",    "amountCcy": "EUR", "accountAlias": "123-abc-567", "transactionCreatedAt": "2026-11-19T08:00:00+00:00" },
    { "tenantUserId": "dave",  "offerId": "offer-104", "buyOrSell": "Buy",  "amount": "0.12345678", "amountCcy": "EUR", "accountAlias": "456-def-890", "transactionCreatedAt": "2026-11-19T11:00:00+00:00" }
  ]
}
```

Results are sorted by `createdAt`, like on `/transactions`, not by recording time. Pagination works the same way. Send the same `missingsSince` and `asOf` for every page of a call.

---

## 5. Walkthrough: reconcile a day, then catch up every hour

This scenario reconciles **November 19 (UTC)** for tenant `acme`:

- an initial pass on **November 20 at 06:01**;
- then one `/missings` call every hour, at 07:01, 08:01, 09:01 and 10:01.

Each call takes `asOf` = the full hour, which is "now minus one minute".

### The data

Here is what the database contains by 10:00. `insertedAt` is not returned by the API; it is shown here only to explain the results.

| offerId | tenantUserId | createdAt (UTC) | insertedAt (UTC) | Note |
|---|---|---|---|---|
| offer-101 | alice | Nov 19 08:00 | Nov 19 08:00:05 | |
| offer-102 | bob | Nov 19 14:30 | Nov 19 14:30:02 | |
| offer-103 | carol | Nov 19 23:10 | Nov 20 05:40 | Recorded late, but before the initial pass |
| offer-104 | dave | Nov 19 11:00 | Nov 20 06:20 | Late |
| offer-105 | alice | Nov 19 08:00 | Nov 20 06:45 | Late, same user and same `createdAt` as offer-101 |
| offer-106 | frank | **Nov 20** 07:30 | Nov 20 07:30:01 | Created on Nov 20: outside the range |
| offer-107 | grace | Nov 19 17:45 | Nov 20 08:30 | Late |
| offer-108 | heidi | Nov 19 21:00 | Nov 20 **09:00:00** | Late, recorded exactly on the hour |
| offer-109 | ivan | Nov 19 06:15 | Nov 20 09:20 | Late |

### The timeline

```
insertedAt (Nov 20, UTC)
 ──────────────────┬──────────────┬──────────────┬──────────────┬──────────────┬──▶
                 06:00          07:00          08:00          09:00          10:00
   101 102 103     │   104 105    │    (106)     │     107      │   108 109    │
 ◀── initial ─────▶│◀─ missings ─▶│◀─ missings ─▶│◀─ missings ─▶│◀─ missings ─▶│
     asOf=06:00    │ [06:00,07:00)│ [07:00,08:00)│ [08:00,09:00)│ [09:00,10:00)│
     run at 06:01  │ run at 07:01 │ run at 08:01 │ run at 09:01 │ run at 10:01 │
```

All the calls below use the same tenant and the same range:

```bash
API=http://localhost:8080/v1/reconciliations/transactions
RANGE=(--data-urlencode "tenantId=acme"
       --data-urlencode "fromCreatedAt=2026-11-19T00:00:00Z"
       --data-urlencode "toCreatedAt=2026-11-20T00:00:00Z")
```

### 06:01 — initial pass, `asOf=06:00`

```bash
curl -G "$API" "${RANGE[@]}" --data-urlencode "asOf=2026-11-20T06:00:00Z"
```

```json
{
  "transactions": [
    { "offerId": "offer-101", "transactionCreatedAt": "2026-11-19T08:00:00+00:00", … },
    { "offerId": "offer-102", "transactionCreatedAt": "2026-11-19T14:30:00+00:00", … },
    { "offerId": "offer-103", "transactionCreatedAt": "2026-11-19T23:10:00+00:00", … }
  ]
}
```

`offer-103` was recorded late (05:40), but before `asOf`, so the initial pass returns it. **Save `06:00`.**

### 07:01 — `/missings`, `missingsSince=06:00`, `asOf=07:00`

```bash
curl -G "$API/missings" "${RANGE[@]}" \
  --data-urlencode "missingsSince=2026-11-20T06:00:00Z" \
  --data-urlencode "asOf=2026-11-20T07:00:00Z"
```

```json
{
  "transactions": [
    { "offerId": "offer-105", "transactionCreatedAt": "2026-11-19T08:00:00+00:00", … },
    { "offerId": "offer-104", "transactionCreatedAt": "2026-11-19T11:00:00+00:00", … }
  ]
}
```

`offer-104` and `offer-105` were recorded at 06:20 and 06:45. **Save `07:00`.**

### 08:01 — `/missings`, `missingsSince=07:00`, `asOf=08:00`

```bash
curl -G "$API/missings" "${RANGE[@]}" \
  --data-urlencode "missingsSince=2026-11-20T07:00:00Z" \
  --data-urlencode "asOf=2026-11-20T08:00:00Z"
```

```json
{ "transactions": [] }
```

`offer-106` was recorded in this window, but it was created on November 20, outside the range. An empty answer is normal. **Save `08:00` anyway**, so the next window starts at 08:00.

### 09:01 — `/missings`, `missingsSince=08:00`, `asOf=09:00`

```bash
curl -G "$API/missings" "${RANGE[@]}" \
  --data-urlencode "missingsSince=2026-11-20T08:00:00Z" \
  --data-urlencode "asOf=2026-11-20T09:00:00Z"
```

```json
{
  "transactions": [
    { "offerId": "offer-107", "transactionCreatedAt": "2026-11-19T17:45:00+00:00", … }
  ]
}
```

`offer-108` was recorded at exactly 09:00:00. Because `asOf` is excluded, it is **not** in this window. **Save `09:00`.**

### 10:01 — `/missings`, `missingsSince=09:00`, `asOf=10:00`

```bash
curl -G "$API/missings" "${RANGE[@]}" \
  --data-urlencode "missingsSince=2026-11-20T09:00:00Z" \
  --data-urlencode "asOf=2026-11-20T10:00:00Z"
```

```json
{
  "transactions": [
    { "offerId": "offer-109", "transactionCreatedAt": "2026-11-19T06:15:00+00:00", … },
    { "offerId": "offer-108", "transactionCreatedAt": "2026-11-19T21:00:00+00:00", … }
  ]
}
```

`offer-108` appears here, because `missingsSince` is included. Each transaction shows up in exactly one window. **Save `10:00`.**

### Check

The five calls returned 3 + 2 + 0 + 1 + 2 = **8 transactions**, with no duplicates. A single pass with `asOf=10:00` returns the same 8 transactions:

```bash
curl -G "$API" "${RANGE[@]}" --data-urlencode "asOf=2026-11-20T10:00:00Z"
# offer-109, offer-101, offer-105, offer-104, offer-102, offer-107, offer-108, offer-103
```

### Rules to remember

1. **One saved `asOf` per (tenant, date range).** Each range has its own chain.
2. **Save `asOf` only once the call is fully processed**: every page read, and the results stored on your side. If a call fails halfway, run it again with the same `missingsSince`. Do not skip to the next window.
3. **Save `asOf` even when the answer is empty**, otherwise the next window starts too early and returns transactions you already have.
4. **Calls do not need to be exactly one hour apart.** A late or skipped run is not a problem: the next call simply covers a larger window, `[last saved asOf, new asOf)`.
5. **If a run happens less than one minute after the previous one**, the new `asOf` can be equal to or earlier than `missingsSince`, and the API answers `400`. Skip the run instead. The script below does this.

---

## 6. Ready-to-use script

This bash script (requires `curl` and `jq`) follows the rules above. It saves the last `asOf` of each (tenant, range) in a file.

- **On the first run for a range**, it reads every page of `/transactions`.
- **On every later run**, it reads every page of `/missings` since the previous run.

Results are printed on stdout, one JSON object per line.

```bash
#!/usr/bin/env bash
# Usage: ./reconcile.sh <tenantId> <fromCreatedAt> <toCreatedAt>
#
# First run for a range : full traversal of /transactions.
# Every following run   : only the transactions inserted since the previous run (/missings).
# Prints one JSON object per transaction (NDJSON) on stdout.
set -euo pipefail

API="${API:-http://localhost:8080}"
PAGE_SIZE="${PAGE_SIZE:-1000}"
STATE_DIR="${STATE_DIR:-.}"

TENANT="$1"
FROM="$2"
TO="$3"
STATE_FILE="$STATE_DIR/as-of.$TENANT.$FROM.$TO"

# asOf = now minus one minute (safety margin), unless provided by the caller.
now_minus_one_minute() {
  date -u -v-1M +%Y-%m-%dT%H:%M:%SZ 2>/dev/null ||  # BSD / macOS
    date -u -d '-1 minute' +%Y-%m-%dT%H:%M:%SZ      # GNU / Linux
}
AS_OF="${AS_OF:-$(now_minus_one_minute)}"

# Reads every page of an endpoint. Extra curl arguments (the asOf / missingsSince parameters) are passed through.
fetch_all() {
  local path="$1"; shift
  local page=1 body count
  while :; do
    body=$(curl -sS --fail-with-body -G "$API$path" \
      --data-urlencode "tenantId=$TENANT" \
      --data-urlencode "fromCreatedAt=$FROM" \
      --data-urlencode "toCreatedAt=$TO" \
      --data-urlencode "page=$page" \
      --data-urlencode "pageSize=$PAGE_SIZE" \
      "$@")
    jq -c '.transactions[]' <<<"$body"
    count=$(jq '.transactions | length' <<<"$body")
    (( count < PAGE_SIZE )) && break   # a page shorter than pageSize is the last one
    page=$((page + 1))
  done
}

if [[ -f "$STATE_FILE" ]]; then
  SINCE=$(cat "$STATE_FILE")
  if [[ ! "$AS_OF" > "$SINCE" ]]; then
    echo "Nothing to do: asOf ($AS_OF) is not after the previous asOf ($SINCE)." >&2
    exit 0
  fi
  echo "missings [$SINCE, $AS_OF)" >&2
  fetch_all /v1/reconciliations/transactions/missings \
    --data-urlencode "missingsSince=$SINCE" \
    --data-urlencode "asOf=$AS_OF"
else
  echo "initial traversal, asOf=$AS_OF" >&2
  fetch_all /v1/reconciliations/transactions \
    --data-urlencode "asOf=$AS_OF"
fi

# Saved only once every page has been read: the next run starts exactly where this one stopped.
echo "$AS_OF" > "$STATE_FILE"
```

> In this script, "processed" means "every page printed". In a real job, save `asOf` only after the results are stored on your side (rule 2).

### Running it every hour

The `AS_OF` variable overrides "now minus one minute". Here it replays the walkthrough's schedule with `pageSize=2` (output shortened to `offerId`):

```console
$ for h in 06 07 08 09 10; do
>   AS_OF=2026-11-20T$h:00:00Z PAGE_SIZE=2 ./reconcile.sh acme 2026-11-19T00:00:00Z 2026-11-20T00:00:00Z
> done
initial traversal, asOf=2026-11-20T06:00:00Z
{"tenantUserId":"alice","offerId":"offer-101",...}
{"tenantUserId":"bob","offerId":"offer-102",...}
{"tenantUserId":"carol","offerId":"offer-103",...}
missings [2026-11-20T06:00:00Z, 2026-11-20T07:00:00Z)
{"tenantUserId":"alice","offerId":"offer-105",...}
{"tenantUserId":"dave","offerId":"offer-104",...}
missings [2026-11-20T07:00:00Z, 2026-11-20T08:00:00Z)
missings [2026-11-20T08:00:00Z, 2026-11-20T09:00:00Z)
{"tenantUserId":"grace","offerId":"offer-107",...}
missings [2026-11-20T09:00:00Z, 2026-11-20T10:00:00Z)
{"tenantUserId":"ivan","offerId":"offer-109",...}
{"tenantUserId":"heidi","offerId":"offer-108",...}

$ cat as-of.acme.2026-11-19T00:00:00Z.2026-11-20T00:00:00Z
2026-11-20T10:00:00Z
```

In production, let the script compute `asOf` and schedule it hourly, for example with cron at minute 1:

```cron
1 * * * * /opt/reconcile/reconcile.sh acme 2026-11-19T00:00:00Z 2026-11-20T00:00:00Z >> /var/log/reconcile-acme.ndjson
```

---

## 7. Errors

An invalid request returns `400` with one entry per invalid parameter. Keys and messages use the parameter names exactly as they appear in the query string, and messages are always in English:

```json
{
  "statusCode": 400,
  "message": "One or more errors occurred!",
  "errors": {
    "toCreatedAt": ["'toCreatedAt' must be after 'fromCreatedAt'."],
    "page": ["'page' must be between 1 and 1000000. You entered 0."]
  }
}
```

| Cause | Message |
|---|---|
| Missing parameter, or date parameter present but empty (`asOf=`) | `'tenantId' must not be empty.` (same for `fromCreatedAt`, `toCreatedAt`, `asOf`, `missingsSince`) |
| `tenantId` too long | `The length of 'tenantId' must be 100 characters or fewer. You entered 101 characters.` |
| Unreadable date, including an unencoded `+` | `'fromCreatedAt' must be a valid DateTimeOffset. You entered '2026-11-20T00:00:00 01:00'.` |
| `toCreatedAt` not after `fromCreatedAt` | `'toCreatedAt' must be after 'fromCreatedAt'.` |
| `asOf` not after `missingsSince` (`/missings`) | `'asOf' must be after 'missingsSince'.` |
| `page` out of range | `'page' must be between 1 and 1000000. You entered 0.` |
| `pageSize` out of range | `'pageSize' must be between 1 and 1000. You entered 5000.` |

Unknown parameters are ignored. A misspelled parameter name, such as `missingSince` instead of `missingsSince`, therefore shows up as a missing parameter.

---

## 8. Pitfalls and FAQ

**Why "now minus one minute" for `asOf`, and not "now"?**
`insertedAt` is set by the database at the **start** of the inserting transaction, not when it is committed. A transaction whose insertion started just before your `asOf` but was committed after your pass has `insertedAt < asOf`. Your pass did not see it, and the next `/missings` call (`insertedAt >= asOf`) will not see it either. The margin also covers clock differences between your machine and the database. With "now minus one minute", this can only happen to an insertion that takes more than one minute.

**Can I make the windows overlap to be safe?**
Yes. If you start `missingsSince` a few minutes before the previous `asOf`, you trade the guarantee of no duplicates for extra safety. You must then deduplicate on `offerId`, which is unique across the whole database.

**How long should I keep calling `/missings` for a given day?**
That is up to you: the API sets no limit. Keep calling as long as late records are possible for that day, then stop the chain (delete the state file). A transaction recorded after you stop will only show up if you resume the chain from the last saved `asOf`.

**A transaction was recorded late with a `createdAt` in an older range. Will I see it?**
Only on the chain of the range that contains its `createdAt`. Each range has its own chain, as `offer-106` showed in the walkthrough: it is not part of the Nov 19 chain.

**Is the order between transactions created at the same instant meaningful?**
No. Transactions with the same `createdAt` are sorted by `offerId` only to keep pagination stable. That order does not reflect creation or recording order.

**How should I read `amount`?**
As a string, then with a decimal type (`decimal` in .NET, `BigDecimal` in Java, `Decimal` in Python) if you need to compute with it. The API returns it exactly as recorded, so `"5000"` and `"5000.00"` are both possible for the same value: compare amounts as numbers, not as strings.

**What if a transaction's details change after my pass?**
Neither endpoint reports changes or deletions. They only report transactions recorded in the database within the given window.

**Can I page with a different `pageSize` from one page to the next?**
No. Keep `pageSize`, like `asOf` (and `missingsSince`), identical for every page of a pass, otherwise pages overlap or leave gaps.
