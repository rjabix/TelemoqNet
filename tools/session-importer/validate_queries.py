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
    now = dt.datetime.now(dt.timezone.utc)
    for index, protocol in enumerate(("telnet", "mqtt")):
        session = {
            "SchemaVersion": 2, "SessionId": str(uuid.uuid5(uuid.NAMESPACE_URL, protocol)),
            "StartedAt": now.isoformat(), "Protocol": protocol,
            "RemoteEndpoint": "192.0.2.1:12345", "Username": "synthetic",
            "LoginAttempts": [{"Username": "synthetic", "Successful": False}],
            "Commands": [{"Command": "echo synthetic", "Response": "🙂" * 3000}],
            "Events": [{"Type": "synthetic.event", "Detail": "test"}],
        }
        rows = importer.normalize(session, protocol + ".json", "synthetic-etag",
                                  "complete-" + protocol, now.isoformat())
        for table, values in rows.items():
            records[table].extend(values)
            records[table].extend(values)  # Simulate retry duplicates.
        validation = importer.normalize(session, '__analysis_validation__/' + protocol + '.json',
                                        'synthetic-etag', 'validation-' + protocol, now.isoformat())
        for table, values in validation.items():
            records[table].extend(values)  # Validation captures must not enter charts.
        partial = importer.normalize(session, protocol + ".json", "synthetic-etag",
                                     "partial-" + protocol, (now + dt.timedelta(seconds=1)).isoformat())
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
    preamble += "let TelemoqValidationRows = (SessionStart:datetime=datetime(2000-01-01), SessionEnd:datetime=datetime(2100-01-01), SelectedProtocol:string='all', SelectedSession:string='') {\n" + (queries / "rows.kql").read_text() + "\n};\n"
    checks = [("completeness", "HoneypotRows() | where EntityKind == 'Sessions' | summarize Sessions=count()")]
    checks += [(path.stem, path.read_text()) for path in sorted(queries.glob("*.kql")) if path.stem != "rows"]
    if len(sys.argv) > 2:
        workbook = json.loads(Path(sys.argv[2]).read_text())
        for item in workbook['items']:
            if item['type'] != 3:
                continue
            query = item['content']['query']
            for key, value in {'Start': '2000-01-01T00:00:00Z', 'End': '2100-01-01T00:00:00Z',
                               'Protocol': 'all', 'SessionId': ''}.items():
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
        if name == "completeness" and table["rows"] != [[2]]:
            raise AssertionError("Complete session selection/deduplication failed")
        if name == "chunks":
            hash_index = next(i for i, col in enumerate(table["columns"]) if col["name"] == "HashMatches")
            if not table["rows"] or not all(row[hash_index] for row in table["rows"]):
                raise AssertionError("Chunk reconstruction failed")
        print(name + ": passed (synthetic records only)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
