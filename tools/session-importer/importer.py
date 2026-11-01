"""Bounded, read-only blob import. No external dependencies or payload execution."""
import base64
import datetime as dt
import hashlib
import json
import os
import signal
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET
from dataclasses import dataclass

UTC = dt.timezone.utc
TABLES = ("Sessions", "Logins", "Commands", "Events", "PayloadChunks", "ImportStatus")
FIELD_BYTES = 20_000
BATCH_BYTES = 450_000
MAX_NORMALIZED_ROWS = 20_000


def encode(value):
    return json.dumps(value, ensure_ascii=True, separators=(",", ":"), allow_nan=False).encode()


def utcnow():
    return dt.datetime.now(UTC).isoformat()


class ImportFailure(Exception):
    """Messages must never include credentials, URLs, or attacker payloads."""


def positive(env, name, default, ceiling):
    value = int(env.get(name, default))
    if not 0 < value <= ceiling:
        raise ImportFailure("invalid limit: " + name)
    return value


@dataclass
class Config:
    start: dt.date
    end: dt.date
    max_blobs: int
    max_source: int
    max_blob: int
    max_ingestion: int
    max_seconds: int
    dry_run: bool

    @classmethod
    def load(cls, env):
        start = dt.date.fromisoformat(env.get("START_DATE", ""))
        end = dt.date.fromisoformat(env.get("END_DATE", ""))
        if end < start or (end - start).days > 30:
            raise ImportFailure("date range must be inclusive and at most 31 days")
        dry = env.get("DRY_RUN", "true").lower()
        if dry not in ("true", "false"):
            raise ImportFailure("DRY_RUN must be true or false")
        return cls(start, end,
                   positive(env, "MAX_BLOBS", "100", 10_000),
                   positive(env, "MAX_SOURCE_BYTES", "10485760", 104_857_600),
                   positive(env, "MAX_BLOB_BYTES", "2097152", 8_388_608),
                   positive(env, "MAX_INGESTION_BYTES", "20971520", 209_715_200),
                   positive(env, "MAX_SECONDS", "240", 540), dry == "true")


def normalize(session, blob, etag, run_id, stamp):
    """Return normalized records; large entities have a preview plus lossless chunks.

    Chunks contain slices of one base64 string of canonical JSON, not raw bytes
    independently encoded. Reassemble ChunkData by ChunkIndex before decoding.
    """
    if not isinstance(session, dict):
        raise ImportFailure("session is not an object")
    try:
        session_id = str(uuid.UUID(session["SessionId"]))
        started = session["StartedAt"]
        parsed = dt.datetime.fromisoformat(started.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            raise ValueError()
        protocol = session.get("Protocol", "telnet")
        if protocol not in ("telnet", "mqtt"):
            raise ValueError()
        if session.get("SchemaVersion", 1) not in (1, 2):
            raise ValueError()
    except (KeyError, TypeError, ValueError, AttributeError):
        raise ImportFailure("invalid session metadata") from None
    common = dict(TimeGenerated=stamp, SessionId=session_id, StartedAt=started,
                  Protocol=protocol, SourceBlob=blob, SourceETag=etag, ImportRunId=run_id)
    rows = {table: [] for table in TABLES}
    row_count = 0

    def append(table, row):
        nonlocal row_count
        row_count += 1
        if row_count > MAX_NORMALIZED_ROWS:
            raise ImportFailure("normalized record limit reached")
        rows[table].append(row)

    def add(table, index, entity):
        if not isinstance(entity, dict):
            raise ImportFailure("invalid child record")
        raw = encode(entity)
        digest = hashlib.sha256(raw).hexdigest()
        chunked = len(raw) > FIELD_BYTES
        payload = entity
        if chunked:
            # Preview is explicitly lossy; chunks preserve the entire entity,
            # including unknown fields. Never use preview as a raw export.
            payload = {k: (v[:256] if isinstance(v, str) else v)
                       for k, v in entity.items()
                       if k in ("Command", "Response", "Type", "Detail", "Username",
                                "Timestamp", "Successful", "RemoteEndpoint",
                                "AuthenticationSucceeded", "DeviceProfile", "SchemaVersion")
                       and (isinstance(v, (str, bool, int, float)) or v is None)}
            payload["PreviewOnly"] = True
            encoded = base64.b64encode(raw).decode("ascii")
            for chunk_index, offset in enumerate(range(0, len(encoded), FIELD_BYTES)):
                append("PayloadChunks", dict(common, RecordIndex=index,
                    EntityKind=table + "PayloadChunks", ChunkIndex=chunk_index,
                    ChunkData=encoded[offset:offset + FIELD_BYTES], PayloadHash=digest,
                    Payload={}, HasChunks=False, ExpectedRows=0, State=""))
        append(table, dict(common, RecordIndex=index, EntityKind=table,
            ChunkIndex=-1, ChunkData="", PayloadHash=digest,
            Payload=payload, HasChunks=chunked, ExpectedRows=0, State=""))

    children = {"LoginAttempts": "Logins", "Commands": "Commands", "Events": "Events"}
    add("Sessions", 0, {k: v for k, v in session.items() if k not in children})
    for field, table in children.items():
        values = session.get(field, [])
        if not isinstance(values, list):
            raise ImportFailure("invalid child collection")
        for index, entity in enumerate(values):
            add(table, index, entity)
    expected = sum(len(items) for items in rows.values())
    rows["ImportStatus"].append(dict(common, RecordIndex=0, EntityKind="ImportStatus",
        ChunkIndex=-1, ChunkData="", PayloadHash="", Payload={}, HasChunks=False,
        ExpectedRows=expected, State="Accepted"))
    return rows


def batches(rows):
    batch = []
    size = 2
    for row in rows:
        encoded = encode(row)
        extra = len(encoded) + (1 if batch else 0)
        if len(encoded) + 2 > BATCH_BYTES:
            raise ImportFailure("record exceeds batch bound")
        if size + extra > BATCH_BYTES:
            yield b"[" + b",".join(batch) + b"]"
            batch, size = [], 2
            extra = len(encoded)
        batch.append(encoded)
        size += extra
    if batch:
        yield b"[" + b",".join(batch) + b"]"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        # Never forward authorization headers to another host.
        return None


class Azure:
    def __init__(self, env, deadline):
        self.env, self.deadline = env, deadline
        self.opener = urllib.request.build_opener(NoRedirect())
        self.tokens = {}
        self.attempted_ingestion_bytes = 0
        self.ingestion_limit = positive(env, "MAX_INGESTION_BYTES", "20971520", 209_715_200)
        self.storage = env["BLOB_ENDPOINT"].rstrip("/")
        self.ingestion = env["INGESTION_ENDPOINT"].rstrip("/")
        self.container = env["BLOB_CONTAINER"]
        for endpoint, suffix in ((self.storage, ".blob.core.windows.net"),
                                 (self.ingestion, ".ingest.monitor.azure.com")):
            parts = urllib.parse.urlsplit(endpoint)
            if (parts.scheme != "https" or not parts.hostname or
                not parts.hostname.endswith(suffix) or parts.port is not None or
                parts.username or parts.password or parts.path or parts.query or parts.fragment):
                raise ImportFailure("invalid configured Azure endpoint")
        if not self.container or any(c not in "abcdefghijklmnopqrstuvwxyz0123456789-" for c in self.container):
            raise ImportFailure("invalid container")

    def remaining(self):
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise ImportFailure("runtime limit reached")
        return remaining

    def request(self, url, headers, data=None, max_bytes=1_000_000, method=None):
        for attempt in range(5):
            # Count ambiguous retries too: Azure may already have accepted them.
            if data is not None and url.startswith(self.ingestion + "/dataCollectionRules/"):
                if self.attempted_ingestion_bytes + len(data) > self.ingestion_limit:
                    raise ImportFailure("ingestion upload budget reached")
                self.attempted_ingestion_bytes += len(data)
            try:
                req = urllib.request.Request(url, data=data, headers=headers, method=method)
                with self.opener.open(req, timeout=min(20, self.remaining())) as response:
                    body = response.read(max_bytes + 1)
                    if len(body) > max_bytes:
                        raise ImportFailure("response exceeds byte limit")
                    return body, response.headers
            except urllib.error.HTTPError as error:
                status = error.code
                retry_after = error.headers.get("Retry-After", "")
                error.close()
                if status not in (408, 429, 500, 502, 503, 504) or attempt == 4:
                    raise ImportFailure("Azure HTTP status " + str(status)) from None
                wait = min(15, int(retry_after)) if retry_after.isdigit() else 2 ** attempt
            except (urllib.error.URLError, TimeoutError):
                if attempt == 4:
                    raise ImportFailure("Azure network request failed") from None
                wait = 2 ** attempt
            time.sleep(min(wait, self.remaining()))
        raise ImportFailure("retry limit reached")

    def token(self, resource):
        cached = self.tokens.get(resource)
        if cached and cached[1] > time.time() + 120:
            return cached[0]
        endpoint = self.env["IDENTITY_ENDPOINT"]
        # This endpoint/header are platform-injected, never taken from blob data.
        parts = urllib.parse.urlsplit(endpoint)
        if parts.scheme not in ("http", "https") or parts.username or parts.fragment:
            raise ImportFailure("invalid managed identity endpoint")
        query = urllib.parse.urlencode(dict(resource=resource, **{
            "api-version": "2019-08-01", "client_id": self.env["AZURE_CLIENT_ID"]}))
        body, _ = self.request(endpoint + ("&" if "?" in endpoint else "?") + query,
            {"X-IDENTITY-HEADER": self.env["IDENTITY_HEADER"]}, max_bytes=50_000)
        result = json.loads(body)
        value = result["access_token"]
        expiry = float(result["expires_on"])
        self.tokens[resource] = value, expiry
        return value

    def blob_headers(self):
        return {"Authorization": "Bearer " + self.token("https://storage.azure.com/"),
                "x-ms-version": "2023-11-03"}

    def blobs(self, config):
        day = config.start
        while day <= config.end:
            marker = ""
            pages = 0
            while True:
                self.remaining()
                pages += 1
                if pages > config.max_blobs + 1:
                    raise ImportFailure("listing page limit reached")
                query = urllib.parse.urlencode(dict(restype="container", comp="list",
                    prefix=day.strftime("%Y/%m/%d/"), maxresults="100", marker=marker))
                body, _ = self.request(self.storage + "/" + self.container + "?" + query,
                                       self.blob_headers(), max_bytes=500_000)
                # Azure-generated XML only; reject declarations with entities.
                if b"<!DOCTYPE" in body or b"<!ENTITY" in body:
                    raise ImportFailure("invalid listing XML")
                root = ET.fromstring(body)
                for blob in root.findall("./Blobs/Blob"):
                    name = blob.findtext("Name", "")
                    if name.endswith(".json"):
                        size = int(blob.findtext("Properties/Content-Length", "-1"))
                        etag = blob.findtext("Properties/Etag", "")
                        if size < 0 or not etag or not name.startswith(day.strftime("%Y/%m/%d/")):
                            raise ImportFailure("invalid blob metadata")
                        yield name, size, etag
                next_marker = root.findtext("NextMarker", "")
                if not next_marker:
                    break
                if next_marker == marker:
                    raise ImportFailure("listing did not advance")
                marker = next_marker
            day += dt.timedelta(days=1)

    def read_blob(self, name, etag, limit):
        url = self.storage + "/" + self.container + "/" + urllib.parse.quote(name, safe="/")
        body, _ = self.request(url, dict(self.blob_headers(), **{"If-Match": etag}), max_bytes=limit)
        return body

    def send(self, table, body):
        immutable_id = self.env["DCR_IMMUTABLE_ID"]
        if not immutable_id.startswith("dcr-") or not immutable_id[4:].isalnum():
            raise ImportFailure("invalid DCR identifier")
        stream = "Custom-Honeypot" + table + "_CL"
        url = (self.ingestion + "/dataCollectionRules/" + immutable_id +
               "/streams/" + stream + "?api-version=2023-01-01")
        self.request(url, {"Authorization": "Bearer " + self.token("https://monitor.azure.com"),
                          "Content-Type": "application/json"}, body, max_bytes=50_000)


def run(config, azure, stamp=None, run_id=None):
    stamp, run_id = stamp or utcnow(), run_id or str(uuid.uuid4())
    print(json.dumps(dict(event="run_started", runId=run_id, dryRun=config.dry_run,
                          startDate=config.start.isoformat(), endDate=config.end.isoformat())))
    totals = dict(runId=run_id, dryRun=config.dry_run, blobs=0, sourceBytes=0,
                  ingestionBytes=0, accepted=0, failed=0)
    for name, size, etag in azure.blobs(config):
        azure.remaining()
        if (totals["blobs"] >= config.max_blobs or
                totals["sourceBytes"] + size > config.max_source or size > config.max_blob):
            raise ImportFailure("source count or byte limit reached; narrow the date range")
        totals["blobs"] += 1
        totals["sourceBytes"] += size
        # Download/normalize in dry-run too, so expansion estimates are real.
        # No source content or attacker-controlled paths are printed.
        try:
            body = azure.read_blob(name, etag, min(size, config.max_blob))
            session = json.loads(body, parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
            rows = normalize(session, name, etag, run_id, stamp)
            pending = [(table, batch) for table in TABLES for batch in batches(rows[table])]
            generated = sum(len(batch) for _, batch in pending)
        except (ValueError, TypeError, RecursionError, ImportFailure) as error:
            totals["failed"] += 1
            reason = str(error) if isinstance(error, ImportFailure) else "invalid source JSON or metadata"
            print(json.dumps(dict(event="source_failed", runId=run_id,
                                  sourceId=hashlib.sha256(name.encode()).hexdigest(), reason=reason)))
            continue
        if totals["ingestionBytes"] + generated > config.max_ingestion:
            raise ImportFailure("ingestion byte limit reached; narrow the date range")
        totals["ingestionBytes"] += generated
        if not config.dry_run:
            # Status is submitted last, but queries also count actual child rows
            # because cross-stream delivery is asynchronous and can be partial.
            for table, batch in pending:
                azure.send(table, batch)
            totals["accepted"] += 1
    print(json.dumps(dict(event="summary", attemptedIngestionBytes=getattr(azure, "attempted_ingestion_bytes", 0), **totals)))
    return 2 if totals["failed"] else 0


def main():
    try:
        config = Config.load(os.environ)
        # Platform replica timeout is the second, independent enforcement layer.
        signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
        signal.signal(signal.SIGALRM, lambda *_: (_ for _ in ()).throw(ImportFailure("runtime limit reached")))
        signal.alarm(config.max_seconds)
        return run(config, Azure(os.environ, time.monotonic() + config.max_seconds))
    except (ImportFailure, ValueError, KeyError, TypeError, RecursionError, ET.ParseError) as error:
        # Never print arbitrary exceptions: HTTP/JSON errors may embed secrets.
        message = str(error) if isinstance(error, ImportFailure) else "invalid configuration or source metadata"
        print(json.dumps(dict(event="failed", reason=message)), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
