"""Download complete analysis records into a private, portable SQLite database.

Uses the operator's Azure CLI login and the retained workspace, without source
storage access or an importer runtime. No third-party Python packages required.
"""
import base64
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import subprocess
import tempfile
import uuid

PAGE_SIZE = 200
MAX_ROWS = 1_000_000
MAX_BYTES = 1_073_741_824
# Canonical ASCII JSON can expand Unicode from an 8 MiB source blob.
MAX_PAYLOAD_BYTES = 33_554_432
COLUMNS = (
    'TimeGenerated', 'SessionId', 'StartedAt', 'Protocol', 'SourceBlob',
    'SourceETag', 'ImportRunId', 'EntityKind', 'RecordIndex', 'ChunkIndex',
    'ChunkData', 'PayloadHash', 'Payload', 'HasChunks', 'SourceAddress',
    'Country', 'City', 'Latitude', 'Longitude', 'AuthenticationSucceeded',
)
KEY = ('SourceBlob', 'SourceETag', 'ImportRunId', 'EntityKind', 'RecordIndex')


def query_workspace(workspace, query):
    """Reject partial API responses, even when Azure returns HTTP 200."""
    with tempfile.NamedTemporaryFile(mode='w', suffix='.json') as body:
        json.dump({'query': query}, body)
        body.flush()
        try:
            result = subprocess.run([
                'az', 'rest', '--method', 'post', '--url',
                'https://api.loganalytics.azure.com/v1/workspaces/' + str(uuid.UUID(workspace)) + '/query',
                '--resource', 'https://api.loganalytics.io', '--body', '@' + body.name,
                '--output', 'json', '--only-show-errors',
            ], capture_output=True, text=True, timeout=90)
        except subprocess.TimeoutExpired:
            raise RuntimeError('SQLite export query timed out; retry with a smaller date range') from None
    if result.returncode:
        # Do not echo response bodies: they can contain captured credentials.
        raise RuntimeError('SQLite export query failed; check Azure login and workspace query access')
    response = json.loads(result.stdout)
    if response.get('error') or len(response.get('tables', [])) != 1:
        raise RuntimeError('SQLite export query returned an error or partial result')
    table = response['tables'][0]
    names = [column['name'] for column in table['columns']]
    if len(names) != len(set(names)) or any(len(row) != len(names) for row in table['rows']):
        raise RuntimeError('SQLite export query returned invalid columns')
    return [dict(zip(names, row)) for row in table['rows']]


def query_prefix(start, end, cutoff):
    # Reuse the deployed selection semantics, with a fixed ingestion cutoff on
    # EVERY underlying table. Later imports must not move the paging cursor.
    source = (Path(__file__).resolve().parents[1] / 'analysis/queries/rows.kql').read_text()
    source = re.sub(r'\bHoneypot(?:Sessions|Logins|Commands|Events|PayloadChunks|ImportStatus)_CL\b',
                    lambda match: '(' + match[0] + ' | where ingestion_time() <= datetime(' + cutoff + '))', source)
    exclusive_end = end + dt.timedelta(days=1)
    return (
        'let ExportRows = () {\n'
        'let SessionStart=datetime(' + start.isoformat() + ');\n'
        'let SessionEnd=datetime(' + exclusive_end.isoformat() + ');\n'
        "let SelectedProtocol='all'; let SelectedSession=''; let SelectedIp='';\n"
        "let SelectedCountry=''; let SelectedCity=''; let SelectedAuthentication='all';\n"
        + source + '\n};\nExportRows()\n'
    )


def create_schema(connection):
    integers = {'RecordIndex', 'ChunkIndex', 'HasChunks', 'AuthenticationSucceeded'}
    reals = {'Latitude', 'Longitude'}
    definitions = [name + (' INTEGER' if name in integers else ' REAL' if name in reals else ' TEXT')
                   for name in COLUMNS]
    connection.execute('CREATE TABLE records (' + ','.join(definitions) +
                       ', PRIMARY KEY (' + ','.join(KEY + ('ChunkIndex',)) + '))')
    connection.execute('CREATE INDEX records_session ON records (SessionId, EntityKind, RecordIndex)')
    connection.execute('CREATE INDEX records_source ON records (SourceAddress, StartedAt)')
    connection.execute('CREATE TABLE export_metadata (Name TEXT PRIMARY KEY, Value TEXT NOT NULL)')
    fields = {
        'Sessions': ('RemoteEndpoint', 'Username', 'DeviceProfile', 'DisconnectReason', 'EndedAt'),
        'Logins': ('Timestamp', 'Username', 'Password', 'Successful'),
        'Commands': ('Timestamp', 'Command', 'Response'),
        'Events': ('Timestamp', 'Type', 'Detail', 'DownloadAttempt'),
    }
    for kind, names in fields.items():
        projection = ','.join("json_extract(Payload, '$." + name + "') AS " + name for name in names)
        connection.execute('CREATE VIEW ' + kind.lower() + ' AS SELECT records.*,' + projection +
                           " FROM records WHERE EntityKind='" + kind + "'")


def insert_rows(connection, rows):
    for row in rows:
        if any(name not in row for name in COLUMNS):
            raise RuntimeError('SQLite export is missing required record columns')
        payload = row['Payload']
        if isinstance(payload, str):
            payload = json.loads(payload)
        if not isinstance(payload, dict):
            raise RuntimeError('SQLite export has an invalid record payload')
        values = [json.dumps(payload, ensure_ascii=True, separators=(',', ':'), allow_nan=False)
                  if name == 'Payload' else row[name] for name in COLUMNS]
        connection.execute('INSERT INTO records VALUES (' + ','.join('?' for _ in COLUMNS) + ')', values)


def reconstruct_payloads(connection):
    """Replace previews with hash-verified full JSON, retaining original chunks."""
    cursor = connection.execute('SELECT ' + ','.join(KEY) + ',PayloadHash FROM records WHERE HasChunks=1')
    for *key, digest in cursor:
        chunk_key = key[:]
        chunk_key[3] += 'PayloadChunks'
        chunks = connection.execute('SELECT ChunkIndex,ChunkData,PayloadHash FROM records WHERE ' +
                                    ' AND '.join(name + '=?' for name in KEY) + ' ORDER BY ChunkIndex', chunk_key)
        encoded = []
        length = 0
        for index, (chunk_index, data, chunk_hash) in enumerate(chunks):
            length += len(data)
            if chunk_index != index or chunk_hash != digest or length > MAX_PAYLOAD_BYTES * 4 // 3 + 4:
                raise RuntimeError('SQLite export has invalid or oversized payload chunks')
            encoded.append(data)
        try:
            raw = base64.b64decode(''.join(encoded), validate=True)
            payload = json.loads(raw)
        except (ValueError, UnicodeError):
            raise RuntimeError('SQLite export payload reconstruction failed') from None
        if len(raw) > MAX_PAYLOAD_BYTES or hashlib.sha256(raw).hexdigest() != digest or not isinstance(payload, dict):
            raise RuntimeError('SQLite export payload hash verification failed')
        connection.execute('UPDATE records SET Payload=? WHERE ' + ' AND '.join(name + '=?' for name in KEY),
                           [raw.decode('utf-8'), *key])
        check_size(connection)


def export_sqlite(workspace, destination, start, end, query=None):
    query = query or (lambda text: query_workspace(workspace, text))
    destination = Path(destination).expanduser().absolute()
    if destination.exists():
        raise RuntimeError('SQLite destination already exists; choose a new filename')
    cutoff = dt.datetime.now(dt.timezone.utc).isoformat()
    prefix = query_prefix(start, end, cutoff)
    count = query(prefix + '| count')
    if len(count) != 1 or type(count[0].get('Count')) is not int or count[0]['Count'] < 0:
        raise RuntimeError('SQLite export returned an invalid row count')
    expected = count[0]['Count']
    if expected > MAX_ROWS:
        raise RuntimeError('SQLite export exceeds the row limit; use a smaller date range')
    destination.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    # Build beside the destination so publication is atomic and never replaces
    # an existing database. Failed or interrupted exports leave no final file.
    with tempfile.TemporaryDirectory(prefix='.sqlite-export-', dir=destination.parent) as directory:
        temporary = Path(directory) / 'sessions.sqlite'
        temporary.touch(mode=0o600)
        connection = sqlite3.connect(temporary)
        try:
            create_schema(connection)
            total, after = 0, ''
            while total < expected:
                page = query(prefix + '| extend ExportKey=hash_sha256(tostring(pack_array(' +
                             ','.join(KEY + ('ChunkIndex',)) + ')))\n' +
                             "| where ExportKey > '" + after + "'\n" +
                             '| order by ExportKey asc\n| take ' + str(min(PAGE_SIZE, expected - total)))
                if not page or len(page) > min(PAGE_SIZE, expected - total):
                    raise RuntimeError('SQLite export row count changed or results were truncated; retry')
                for row in page:
                    key = row.get('ExportKey', '')
                    if not re.fullmatch('[0-9a-f]{64}', key) or key <= after:
                        raise RuntimeError('SQLite export paging did not advance')
                    after = key
                insert_rows(connection, page)
                total += len(page)
                check_size(connection)
                print('SQLite export: ' + str(total) + '/' + str(expected) + ' records.', flush=True)
            reconstruct_payloads(connection)
            metadata = dict(SchemaVersion='1', WorkspaceCustomerId=workspace, ExportedAt=cutoff,
                            StartDate=start.isoformat(), EndDateInclusive=end.isoformat(), RowCount=str(total))
            connection.executemany('INSERT INTO export_metadata VALUES (?,?)', metadata.items())
            check_size(connection)
            connection.commit()
        finally:
            connection.close()
        os.link(temporary, destination)
    print('SQLite database saved to ' + str(destination), flush=True)
    return destination


def check_size(connection):
    pages = connection.execute('PRAGMA page_count').fetchone()[0]
    size = connection.execute('PRAGMA page_size').fetchone()[0]
    if pages * size > MAX_BYTES:
        raise RuntimeError('SQLite export exceeds the size limit; use a smaller date range')
