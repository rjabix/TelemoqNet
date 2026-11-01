"""Optional live KQL compilation/behavior checks over synthetic datatables only.

Usage: python3 tools/session-importer/validate_queries.py WORKSPACE_CUSTOMER_ID [WORKBOOK_JSON]
Requires Azure CLI login and workspace query permission. Writes no Azure data.
"""
import datetime as dt
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import uuid

import importer


def main():
    workspace = str(uuid.UUID(sys.argv[1]))
    root = Path(__file__).resolve().parents[2]
    schema = {
        "TimeGenerated": "datetime", "SessionId": "string", "StartedAt": "datetime",
        "Protocol": "string", "SourceBlob": "string", "SourceETag": "string",
        "ImportRunId": "string", "RecordIndex": "long", "EntityKind": "string",
        "ChunkIndex": "long", "ChunkData": "string", "PayloadHash": "string",
        "Payload": "dynamic", "HasChunks": "bool", "ExpectedRows": "long", "State": "string",
    }
    records = {table: [] for table in importer.TABLES}
    row_counts = {}
    now = dt.datetime.now(dt.timezone.utc)
    fixtures = [
        ('telnet', 'telnet', '192.0.2.1:12345', False),
        ('mqtt', 'mqtt', '[2001:db8::1]:12345', True),
        ('mapped', 'telnet', '[::ffff:192.0.2.1]:23456', True),
        ('bare-v6', 'telnet', '2001:db8::1', None),
        ('private', 'telnet', '10.0.0.1:12345', False),
        ('missing', 'telnet', None, None),
        ('bare-v4', 'telnet', '192.0.2.2', True),
        ('public', 'telnet', '20.53.203.50:12345', True),
    ]
    for name, protocol, endpoint, authenticated in fixtures:
        session = {
            "SchemaVersion": 2, "SessionId": str(uuid.uuid5(uuid.NAMESPACE_URL, name)),
            "StartedAt": now.isoformat(), "Protocol": protocol,
            "RemoteEndpoint": endpoint, "Username": "synthetic",
            "AuthenticationSucceeded": authenticated,
            "LoginAttempts": [] if authenticated is None else [{"Username": "synthetic", "Successful": authenticated}],
            "Commands": [{"Command": "echo synthetic", "Response": "🙂" * 3000 if name == 'telnet' else 'synthetic'}],
            "Events": [{"Type": "synthetic.event", "Detail": "test"}],
        }
        rows = importer.normalize(session, name + ".json", "synthetic-etag",
                                  "complete-" + name, now.isoformat())
        row_counts[name] = sum(len(values) for table, values in rows.items() if table != 'ImportStatus')
        for table, values in rows.items():
            records[table].extend(values)
            records[table].extend(values)  # Simulate retry duplicates.
        validation = importer.normalize(session, '__analysis_validation__/' + name + '.json',
                                        'synthetic-etag', 'validation-' + name, now.isoformat())
        for table, values in validation.items():
            records[table].extend(values)  # Validation captures must not enter charts.
        partial = importer.normalize(dict(session, RemoteEndpoint='198.51.100.99:9999',
                                          AuthenticationSucceeded=False), name + ".json", "synthetic-etag",
                                     "partial-" + name, (now + dt.timedelta(seconds=1)).isoformat())
        for table, values in partial.items():
            if table != "Commands":  # Newer but incomplete run must be excluded.
                records[table].extend(values)

    def literal(value, kind):
        if kind == "datetime":
            return "datetime(" + value + ")"
        if kind == "dynamic":
            return "dynamic(" + json.dumps(value, ensure_ascii=True) + ")"
        return json.dumps(value, ensure_ascii=True)

    preamble = ""
    for table, values in records.items():
        columns = ",".join(name + ":" + kind for name, kind in schema.items())
        cells = ",\n".join(",".join(literal(row[name], kind) for name, kind in schema.items()) for row in values)
        preamble += f"let Honeypot{table}_CL=datatable({columns})[\n{cells}\n];\n"
    queries = root / "infra/analysis/queries"
    # Use a distinct name so this validator also works after the real saved
    # HoneypotRows function has been deployed in the workspace.
    preamble += "let TelemoqValidationRows = (SessionStart:datetime=datetime(2000-01-01), SessionEnd:datetime=datetime(2100-01-01), SelectedProtocol:string='all', SelectedSession:string='', SelectedIp:string='', SelectedCountry:string='', SelectedCity:string='', SelectedAuthentication:string='all') {\n" + (queries / "rows.kql").read_text() + "\n};\n"
    checks = [("completeness", "HoneypotRows() | where EntityKind == 'Sessions' | summarize Sessions=count()")]
    expected = {'completeness': [[len(fixtures)]]}
    # Count ALL matching entities, so filters cannot accidentally drop child rows
    # or apply a newer incomplete import's endpoint/authentication metadata.
    for name, ip, authentication, selected in [
        ('ipv4', '192.0.2.1', 'all', ['telnet', 'mapped']),
        ('mapped-ip-filter', '::ffff:192.0.2.1', 'all', ['telnet', 'mapped']),
        ('ipv6', '2001:0db8:0:0:0:0:0:1', 'all', ['mqtt', 'bare-v6']),
        ('bare-ipv4', '192.0.2.2', 'all', ['bare-v4']),
        ('successful', '', 'successful', ['mqtt', 'mapped', 'bare-v4', 'public']),
        ('failed', '', 'failed', ['telnet', 'private']),
        ('no-authentication', '', 'unknown', ['bare-v6', 'missing']),
        ('combined', '192.0.2.1', 'successful', ['mapped']),
        ('invalid-ip', 'invalid', 'all', []),
        ('ip-with-port', '192.0.2.1:12345', 'all', []),
    ]:
        checks.append((name, "HoneypotRows(datetime(2000-01-01), datetime(2100-01-01), 'all', '', '" + ip +
                       "', '', '', '" + authentication + "') | summarize Rows=count() by SourceBlob | order by SourceBlob asc"))
        expected[name] = sorted([[key + '.json', row_counts[key]] for key in selected])
    mqtt_session = str(uuid.uuid5(uuid.NAMESPACE_URL, 'mqtt'))
    for name, arguments, selected in [
        ('legacy-protocol', "datetime(2000-01-01), datetime(2100-01-01), 'mqtt'", ['mqtt']),
        ('legacy-session-id', "datetime(2000-01-01), datetime(2100-01-01), 'all', '" + mqtt_session + "'", ['mqtt']),
        ('time-excluded', "datetime(1999-01-01), datetime(2000-01-01)", []),
        ('time-start-inclusive', "datetime(" + now.isoformat() + "), datetime(2100-01-01)", [row[0] for row in fixtures]),
        ('time-end-exclusive', "datetime(2000-01-01), datetime(" + now.isoformat() + ")", []),
        ('all-filters', "datetime(2000-01-01), datetime(2100-01-01), 'mqtt', '" + mqtt_session +
         "', '2001:db8::1', '', '', 'successful'", ['mqtt']),
    ]:
        checks.append((name, 'HoneypotRows(' + arguments +
                       ') | summarize Rows=count() by SourceBlob | order by SourceBlob asc'))
        expected[name] = sorted([[key + '.json', row_counts[key]] for key in selected])
    # Compare location-filtered entities with independently filtered output. Use
    # the lookup's current result instead of assuming a permanent GeoIP mapping.
    for field, country, city in [('Country', 'selected', "''"), ('City', "''", 'selected')]:
        name = field.lower() + '-filter'
        checks.append((name,
            "let selected=tolower(toscalar(HoneypotRows() | where SourceBlob == 'public.json' | take 1 | project " + field + "));\n"
            "let expected=HoneypotRows() | where " + field + " =~ selected | summarize Expected=count() by SourceBlob;\n"
            "HoneypotRows(datetime(2000-01-01), datetime(2100-01-01), 'all', '', '', " + country + ", " + city + ")\n"
            "| summarize Actual=count() by SourceBlob | join kind=fullouter expected on SourceBlob\n"
            "| where isnull(Actual) or isnull(Expected) or Actual != Expected | summarize Mismatches=count()"))
        expected[name] = [[0]]
    checks += [
        ('unmatched-country', "HoneypotRows(datetime(2000-01-01), datetime(2100-01-01), 'all', '', '', '__no_country__') | count"),
        ('unknown-location', "HoneypotRows() | where SourceBlob in ('private.json', 'missing.json') | where Country != 'Unknown' or City != 'Unknown' | count"),
        ('endpoint-parsing', "HoneypotRows() | where EntityKind == 'Sessions' | project SourceBlob, SourceAddress | order by SourceBlob asc"),
    ]
    expected.update({'unmatched-country': [[0]], 'unknown-location': [[0]],
        'endpoint-parsing': sorted([[name + '.json', address] for name, address in [
            ('telnet', '192.0.2.1'), ('mqtt', '2001:db8::1'), ('mapped', '192.0.2.1'),
            ('bare-v6', '2001:db8::1'), ('private', '10.0.0.1'), ('missing', ''),
            ('bare-v4', '192.0.2.2'), ('public', '20.53.203.50')]])})
    checks += [(path.stem, path.read_text()) for path in sorted(queries.glob("*.kql")) if path.stem != "rows"]
    if len(sys.argv) > 2:
        workbook = json.loads(Path(sys.argv[2]).read_text())
        for item in workbook['items']:
            if item['type'] != 3:
                continue
            query = item['content']['query']
            for key, value in {'Start': '2000-01-01T00:00:00Z', 'End': '2100-01-01T00:00:00Z',
                               'Protocol': 'all', 'SessionId': '', 'SourceIp': '',
                               'Country': '', 'City': '', 'Authentication': 'all'}.items():
                query = query.replace('{' + key + ':escape}', value)
            checks.append(('workbook: ' + item['content']['title'], query))
    for name, query in checks:
        with tempfile.NamedTemporaryFile(mode="w", suffix=".json") as body:
            json.dump({"query": preamble + query.replace('HoneypotRows(', 'TelemoqValidationRows(')}, body)
            body.flush()
            result = subprocess.run([
                "az", "rest", "--method", "post", "--url",
                "https://api.loganalytics.azure.com/v1/workspaces/" + workspace + "/query",
                "--resource", "https://api.loganalytics.io", "--body", "@" + body.name,
                "--output", "json"], capture_output=True, text=True, timeout=90)
        if result.returncode:
            print(name + " FAILED: " + result.stderr, file=sys.stderr)
            return 1
        response = json.loads(result.stdout)
        if response.get("error"):
            print(name + " FAILED: " + json.dumps(response["error"]), file=sys.stderr)
            return 1
        table = response["tables"][0]
        if name in expected and table["rows"] != expected[name]:
            raise AssertionError(name + ': expected ' + repr(expected[name]) + ', got ' + repr(table['rows']))
        if name == "chunks":
            hash_index = next(i for i, col in enumerate(table["columns"]) if col["name"] == "HashMatches")
            if not table["rows"] or not all(row[hash_index] for row in table["rows"]):
                raise AssertionError("Chunk reconstruction failed")
        print(name + ": passed (synthetic records only)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
