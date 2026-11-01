# Query captured sessions yourself

After deploying the updated analysis resources, use the retained Workbook or
run KQL directly in the Log Analytics workspace. Neither needs the importer
runtime to remain available. To apply the updated functions and Workbook and
import new captures, run the usual command from the repository root:

```bash
python3 infra/scripts/analysis-once.py
```

## Use the Workbook

Find its URL with `terraform -chdir=infra output -json analysis` (the
`workbook_portal_url` field), or open **Azure Portal → Monitor → Workbooks →
Honeypot session analysis**. All these filters work together:

| Filter | What to enter |
| --- | --- |
| Session start / end | Original session time in UTC; start inclusive, end exclusive |
| Protocol | All, Telnet or MQTT |
| Session ID | Exact session GUID; blank for all |
| Source IP | Exact IPv4 or IPv6 address without brackets or port; blank for all |
| Country | Full country name, such as `Poland`, or `Unknown`; blank for all |
| City | Full city name, or `Unknown`; blank for all |
| Session authentication | All, successful, failed, or no recorded outcome |

Country and city match whole names, ignoring case. Copy the spelling from the
source table. Equivalent IPv6 spellings and IPv4-mapped IPv6 addresses match
the same IP. CIDR ranges and partial IP/name searches are not Workbook filters;
use KQL for those. An invalid IP matches no sessions.

The filters select **whole sessions**. Selecting successful sessions also narrows
the traffic, usernames, commands, MQTT events and detail panels to those sessions.
The existing time, protocol and session ID filters remain available. The
**Import delivery status** panel stays unfiltered so failed/partial imports
remain visible even when session metadata is missing.

Successful means `AuthenticationSucceeded == true`, failed means `false`, and
no recorded outcome means `null`. A connection closed before authentication is
not counted as a failed login. An accepted anonymous MQTT connection may count
as successful; this describes the emulated service's outcome.

## Open the query editor

1. Open the workspace shown by `workspace_id` in the Terraform analysis output.
2. Select **Logs**, switch to KQL mode if needed, and set the time picker to
   **Last 30 days** to cover retained imports.
3. Paste one query below and select **Run**. Replace example IPs, locations and
   session IDs with values from your results.

`TimeGenerated` is **import time**. `StartedAt` is the original capture time.
The portal time picker limits imports before the function runs; the function's
start/end arguments select capture times. For an old capture imported today,
keep today's import in the picker and pass the old capture dates to the function.
Tables retain 30 days of imports by default. Data not yet imported will not appear.

`HoneypotRows()` selects complete imports and deduplicates delivery retries.
Querying the raw `Honeypot*_CL` tables can include duplicate and incomplete runs.
The function accepts these optional **positional** arguments; existing zero- and
four-argument calls still work:

```kusto
HoneypotRows(SessionStart, SessionEnd, SelectedProtocol, SelectedSession,
             SelectedIp, SelectedCountry, SelectedCity, SelectedAuthentication)
```

Defaults are `2000-01-01`, `2100-01-01`, `'all'`, `''`, `''`, `''`, `''`, `'all'`.
The new `SourceAddress`, `Country`, `City`, `Latitude`, `Longitude` and
`AuthenticationSucceeded` columns are available on every returned entity,
including commands, logins, events and payload chunks. The original `Payload`
and provenance fields remain available.

## Queries to copy

Recent sessions, with location and login outcome:

```kusto
HoneypotRows(ago(7d), now())
| where EntityKind == 'Sessions'
| project StartedAt, SessionId, Protocol, SourceAddress, Country, City,
          Latitude, Longitude, AuthenticationSucceeded,
          Username=tostring(Payload.Username),
          DisconnectReason=tostring(Payload.DisconnectReason)
| order by StartedAt desc
| take 500
```

Everything captured from one IP across both protocols (replace the example IP):

```kusto
HoneypotRows(ago(7d), now(), 'all', '', '203.0.113.42')
| project StartedAt, SessionId, Protocol, SourceAddress, Country, City,
          EntityKind, RecordIndex, HasChunks
| order by StartedAt desc, EntityKind asc, RecordIndex asc
```

Successful Telnet sessions from one country/city in a specific time window
(use empty strings to leave IP, country or city unrestricted):

```kusto
HoneypotRows(datetime(2026-10-01), datetime(2026-11-01),
             'telnet', '', '', 'Poland', 'Warsaw', 'successful')
| where EntityKind == 'Sessions'
| project StartedAt, SessionId, SourceAddress, Country, City,
          AuthenticationSucceeded
| order by StartedAt desc
```

Commands and responses from successfully authenticated sessions:

```kusto
HoneypotRows(ago(7d), now(), 'all', '', '', '', '', 'successful')
| where EntityKind == 'Commands'
| project StartedAt, SessionId, Protocol, SourceAddress, Country, City,
          Timestamp=todatetime(Payload.Timestamp),
          Command=tostring(Payload.Command), Response=tostring(Payload.Response),
          HasChunks, PayloadHash
| order by Timestamp asc
| take 500
```

Attempted payload retrieval (these events never represent downloaded samples):

```kusto
HoneypotRows(ago(7d), now(), 'telnet')
| where EntityKind == 'Events' and tostring(Payload.Type) == 'payload.download_attempt'
| project Timestamp=todatetime(Payload.Timestamp), SessionId, SourceAddress,
          Tool=tostring(Payload.DownloadAttempt.Tool),
          Url=tostring(Payload.DownloadAttempt.Url),
          Executed=tobool(Payload.DownloadAttempt.Executed),
          Downloaded=tobool(Payload.DownloadAttempt.Downloaded)
| order by Timestamp desc
```

Read one session's timeline (replace the example GUID). This view omits captured
passwords:

```kusto
HoneypotRows(datetime(2000-01-01), datetime(2100-01-01),
             'all', '00000000-0000-0000-0000-000000000000')
| where EntityKind in ('Logins', 'Commands', 'Events')
| project Timestamp=todatetime(Payload.Timestamp), EntityKind, RecordIndex,
          SourceAddress, Country, City,
          Username=tostring(Payload.Username), Successful=tobool(Payload.Successful),
          Command=tostring(Payload.Command), Response=tostring(Payload.Response),
          EventType=tostring(Payload.Type), Detail=tostring(Payload.Detail),
          HasChunks, PayloadHash
| order by Timestamp asc, RecordIndex asc
```

Countries and cities producing the most sessions:

```kusto
HoneypotRows(ago(7d), now())
| where EntityKind == 'Sessions'
| summarize Sessions=count(), SourceIPs=dcount(SourceAddress),
            SuccessfulSessions=countif(AuthenticationSucceeded == true)
    by Country, City
| order by Sessions desc
```

Telnet sources in an IPv4 subnet, or locations containing part of a name:

```kusto
HoneypotRows(ago(7d), now(), 'telnet')
| where EntityKind == 'Sessions'
| where ipv4_is_in_range(SourceAddress, '203.0.113.0/24')
| project StartedAt, SessionId, SourceAddress, Country, City
```

```kusto
HoneypotRows(ago(7d), now())
| where EntityKind == 'Sessions'
| where City contains 'York'
| summarize Sessions=count() by Country, City
```

Use `| where Country == 'Unknown'` or `| where City == 'Unknown'` to inspect
unresolved locations. Use `EntityKind == 'Sessions'` when counting sessions;
counting all function rows also counts child records and payload chunks.

## Run a query from your terminal

Azure CLI uses the workspace customer GUID, rather than the ARM resource ID.
After `az login`, from the repository root:

```bash
task_workspace_id=$(terraform -chdir=infra output -json analysis |
  python3 -c 'import json,sys; print(json.load(sys.stdin)["workspace_customer_id"])')

az monitor log-analytics query \
  --workspace "$task_workspace_id" \
  --timespan P30D \
  --analytics-query "HoneypotRows(ago(7d), now()) | where EntityKind == 'Sessions' | summarize Sessions=count() by Country, City" \
  --output table
```

Use `--output json` for structured results. The
[Azure CLI query reference](https://learn.microsoft.com/en-us/cli/azure/monitor/log-analytics#az-monitor-log-analytics-query)
describes query and time-range options.

## Location accuracy and full evidence

Location is resolved at query time by Azure Monitor's built-in
[`geo_info_from_ip_address()`](https://learn.microsoft.com/en-us/kusto/query/geo-info-from-ip-address-function),
using MaxMind GeoLite2-City data. There is no API key or additional importer/VM
egress. It applies to existing imports as soon as the function is updated.
Results can change as the lookup database changes; they are not a historical
record of an IP's location at capture time. Private, missing or unresolved IPs
remain in results with `Unknown` locations and nullable coordinates.

Coordinates describe an approximate network location, which may be a VPN,
proxy or hosting provider. They cannot pinpoint an attacker's physical address.

When `HasChunks` is true, text in `Payload` is a preview. Use the saved
**Reconstruct large records** query with the session/source version selected,
and verify `HashMatches`, or inspect the original read-only source blob.
See the [analysis guide](session-analysis.md#schema-and-exact-payloads) for exact
reconstruction and delivery semantics. Raw login payloads can contain captured
passwords; the default views above omit them.
