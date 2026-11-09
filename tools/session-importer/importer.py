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
MAX_SOURCES = 50_000
MAX_SOURCE_BYTES = 524_288_000
MAX_INGESTION_BYTES = 1_048_576_000
MAX_CHECKPOINT_BYTES = 5_000_000
VERIFICATION_BATCH_SIZE = 1_000


def encode(value):
    return json.dumps(value, ensure_ascii=True, separators=(",", ":"), allow_nan=False).encode()


def utcnow():
    return dt.datetime.now(UTC).isoformat()


class ImportFailure(Exception):
    """Messages must never include credentials, URLs, or attacker payloads."""


class AzureHttpFailure(ImportFailure):
    def __init__(self, status):
        self.status = status
        super().__init__("Azure HTTP status " + str(status))


class ScanLimit(ImportFailure):
    pass


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
    force: bool = False

    @classmethod
    def load(cls, env):
        automatic = env.get("AUTO_INGEST", "false").lower()
        if automatic not in ("true", "false"):
            raise ImportFailure("AUTO_INGEST must be true or false")
        if automatic == "true" and not env.get("START_DATE") and not env.get("END_DATE"):
            end = dt.datetime.now(UTC).date()
            start = end - dt.timedelta(days=positive(env, "LOOKBACK_DAYS", "7", 31) - 1)
        else:
            start = dt.date.fromisoformat(env.get("START_DATE", ""))
            end = dt.date.fromisoformat(env.get("END_DATE", ""))
        if end < start or (end - start).days > 30:
            raise ImportFailure("date range must be inclusive and at most 31 days")
        dry = env.get("DRY_RUN", "true").lower()
        if dry not in ("true", "false"):
            raise ImportFailure("DRY_RUN must be true or false")
        force = env.get("FORCE_REIMPORT", "false").lower()
        if force not in ("true", "false"):
            raise ImportFailure("FORCE_REIMPORT must be true or false")
        return cls(start, end,
                   positive(env, "MAX_BLOBS", "100", MAX_SOURCES),
                   positive(env, "MAX_SOURCE_BYTES", "10485760", MAX_SOURCE_BYTES),
                   positive(env, "MAX_BLOB_BYTES", "2097152", 8_388_608),
                   positive(env, "MAX_INGESTION_BYTES", "20971520", MAX_INGESTION_BYTES),
                   positive(env, "MAX_SECONDS", "240", 540), dry == "true", force == "true")


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
        self.heartbeat = lambda: None
        self.scan_cursor = {}
        self.next_cursor = {}
        self.attempted_ingestion_bytes = 0
        self.ingestion_limit = positive(env, "MAX_INGESTION_BYTES", "20971520", MAX_INGESTION_BYTES)
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
            self.heartbeat()
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
                    raise AzureHttpFailure(status) from None
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
        cursor = self.scan_cursor
        day = max(config.start, dt.date.fromisoformat(cursor.get("day", config.start.isoformat())))
        total_pages = 0
        while day <= config.end:
            marker = cursor.get("marker", "") if cursor.get("day") == day.isoformat() else ""
            after = cursor.get("after", "") if cursor.get("day") == day.isoformat() else ""
            while True:
                self.remaining()
                total_pages += 1
                if total_pages > 200:
                    raise ScanLimit("listing page limit reached")
                query = urllib.parse.urlencode(dict(restype="container", comp="list",
                    prefix=day.strftime("%Y/%m/%d/"), maxresults="1000", marker=marker))
                body, _ = self.request(self.storage + "/" + self.container + "?" + query,
                                       self.blob_headers(), max_bytes=5_000_000)
                # Azure-generated XML only; reject declarations with entities.
                if b"<!DOCTYPE" in body or b"<!ENTITY" in body:
                    raise ImportFailure("invalid listing XML")
                root = ET.fromstring(body)
                for blob in root.findall("./Blobs/Blob"):
                    name = blob.findtext("Name", "")
                    if name.endswith(".json") and name > after:
                        size = int(blob.findtext("Properties/Content-Length", "-1"))
                        etag = blob.findtext("Properties/Etag", "")
                        if size < 0 or not etag or not name.startswith(day.strftime("%Y/%m/%d/")):
                            raise ImportFailure("invalid blob metadata")
                        self.next_cursor = dict(day=day.isoformat(), marker=marker, after=after)
                        yield name, size, etag
                        after = name
                        self.next_cursor = dict(day=day.isoformat(), marker=marker, after=after)
                next_marker = root.findtext("NextMarker", "")
                if not next_marker:
                    break
                if next_marker == marker:
                    raise ImportFailure("listing did not advance")
                marker = next_marker
                after = ""
                self.next_cursor = dict(day=day.isoformat(), marker=marker, after="")
            day += dt.timedelta(days=1)
            self.next_cursor = dict(day=day.isoformat(), marker="", after="")

    def delivered(self, run_id, expected):
        return self.delivered_many([dict(runId=run_id, expected=expected)])

    def delivered_many(self, runs):
        if not runs:
            return True
        if len(runs) > MAX_SOURCES:
            raise ImportFailure("verification manifest exceeds limit")
        for offset in range(0, len(runs), VERIFICATION_BATCH_SIZE):
            if not self.delivered_batch(runs[offset:offset + VERIFICATION_BATCH_SIZE]):
                return False
        return True

    def delivered_batch(self, runs):
        workspace = str(uuid.UUID(self.env["WORKSPACE_CUSTOMER_ID"]))
        cells = ",".join("'" + str(uuid.UUID(r["runId"])) + "'," + str(int(r["expected"]) + 1) for r in runs)
        query = ("let expected=datatable(ImportRunId:string, Expected:long)[" + cells + "]; "
                 "union HoneypotSessions_CL, HoneypotLogins_CL, HoneypotCommands_CL, "
                 "HoneypotEvents_CL, HoneypotPayloadChunks_CL, HoneypotImportStatus_CL "
                 "| where TimeGenerated > ago(30d) and ImportRunId in (expected | project ImportRunId) "
                 "| summarize by ImportRunId, SourceBlob, SourceETag, EntityKind, RecordIndex, ChunkIndex "
                 "| summarize Rows=count() by ImportRunId "
                 "| join kind=inner expected on ImportRunId | summarize Complete=countif(Rows == Expected)")
        body, _ = self.request("https://api.loganalytics.azure.com/v1/workspaces/" + workspace + "/query",
            {"Authorization": "Bearer " + self.token("https://api.loganalytics.io"),
             "Content-Type": "application/json"}, encode({"query": query}), max_bytes=50_000)
        result = json.loads(body)
        if result.get("error"):
            raise ImportFailure("delivery verification query failed")
        return result["tables"][0]["rows"] == [[len(runs)]]

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


class Checkpoints:
    """Mutable state lives in its own container, never in source evidence.

    One finite renewable lease serializes all manual and scheduled executions.
    Receipts only become Complete after checking delivery, not API acceptance.
    """
    def __init__(self, azure, config):
        self.azure = azure
        container = azure.env["CHECKPOINT_CONTAINER"]
        if container == azure.container or not container or any(
                c not in "abcdefghijklmnopqrstuvwxyz0123456789-" for c in container):
            raise ImportFailure("invalid checkpoint container")
        self.base = azure.storage + "/" + container + "/"
        self.lock = None
        self.renew_at = float("inf")
        scope = f"{config.start.isoformat()}:{config.end.isoformat()}"
        self.cursor_key = "scans/" + hashlib.sha256(scope.encode()).hexdigest() + ".json"

    def get(self, key):
        try:
            body, _ = self.azure.request(self.base + key, self.azure.blob_headers(), max_bytes=MAX_CHECKPOINT_BYTES)
            return json.loads(body)
        except AzureHttpFailure as error:
            if error.status == 404:
                return None
            raise

    def put(self, key, value, create=False):
        headers = dict(self.azure.blob_headers(), **{"x-ms-blob-type": "BlockBlob",
                       "Content-Type": "application/json"})
        if create:
            headers["If-None-Match"] = "*"
        self.azure.request(self.base + key, headers, encode(value), method="PUT")

    def acquire(self):
        try:
            self.put("execution-lock.json", {}, create=True)
        except AzureHttpFailure as error:
            if error.status not in (409, 412):
                raise
        proposed = str(uuid.uuid4())
        try:
            self.azure.request(self.base + "execution-lock.json?comp=lease",
                dict(self.azure.blob_headers(), **{"x-ms-lease-action": "acquire",
                    "x-ms-lease-duration": "60", "x-ms-proposed-lease-id": proposed}),
                b"", method="PUT")
        except AzureHttpFailure as error:
            if error.status == 409:
                raise ImportFailure("another import holds the execution lease") from None
            raise
        self.lock = proposed
        self.renew_at = time.monotonic() + 20
        self.azure.heartbeat = self.renew

    def renew(self):
        if time.monotonic() < self.renew_at:
            return
        self.renew_at = float("inf")  # Avoid recursion through Azure.request.
        self.azure.request(self.base + "execution-lock.json?comp=lease",
            dict(self.azure.blob_headers(), **{"x-ms-lease-action": "renew", "x-ms-lease-id": self.lock}),
            b"", method="PUT")
        self.renew_at = time.monotonic() + 20

    def release(self):
        self.azure.heartbeat = lambda: None
        if self.lock:
            try:
                self.azure.request(self.base + "execution-lock.json?comp=lease",
                    dict(self.azure.blob_headers(), **{"x-ms-lease-action": "release", "x-ms-lease-id": self.lock}),
                    b"", method="PUT")
            except ImportFailure:
                # A terminated process cannot keep the finite lease forever.
                print(json.dumps(dict(event="lease_release_deferred")))

    @staticmethod
    def receipt_key(name, etag):
        return "receipts/" + hashlib.sha256(encode([name, etag])).hexdigest() + ".json"


def run(config, azure, stamp=None, run_id=None, checkpoints=None):
    stamp, run_id = stamp or utcnow(), run_id or str(uuid.uuid4())
    print(json.dumps(dict(event="run_started", runId=run_id, dryRun=config.dry_run,
                          startDate=config.start.isoformat(), endDate=config.end.isoformat())))
    totals = dict(runId=run_id, dryRun=config.dry_run, blobs=0, sourceBytes=0,
                  ingestionBytes=0, accepted=0, failed=0, skipped=0, continuation=False)
    verification = {}
    try:
        if checkpoints:
            checkpoints.acquire()
            if not config.force and not config.dry_run:
                azure.scan_cursor = checkpoints.get(checkpoints.cursor_key) or {}
                azure.next_cursor = azure.scan_cursor
        for name, size, etag in azure.blobs(config):
            # Finish a bounded batch before the hard alarm interrupts an upload.
            # Cursor and manifest then let the operator continue in the same runtime.
            if azure.remaining() < min(30, max(1, config.max_seconds // 4)):
                totals["continuation"] = True
                break
            if totals["blobs"] >= config.max_blobs:
                totals["continuation"] = True
                break
            totals["blobs"] += 1
            receipt_key = Checkpoints.receipt_key(name, etag)
            receipt = checkpoints.get(receipt_key) if checkpoints and not config.force else None
            if receipt:
                age = (dt.datetime.now(UTC) - dt.datetime.fromisoformat(receipt["stamp"])).total_seconds()
                if ((receipt["state"] == "Complete" and age < 25 * 86400) or
                    (receipt["state"] == "Rejected" and age < 86400)):
                    totals["skipped"] += 1
                    continue
                if receipt["state"] in ("Pending", "Submitted") and age < 25 * 86400:
                    verification[receipt["runId"]] = receipt["expected"]
                    if azure.delivered(receipt["runId"], receipt["expected"]):
                        if not config.dry_run:
                            checkpoints.put(receipt_key, dict(receipt, state="Complete"))
                        totals["skipped"] += 1
                        continue
                    if receipt["state"] == "Submitted" and age < 900:
                        totals["skipped"] += 1
                        continue
                else:
                    receipt = None
            if size > config.max_blob or size > config.max_source:
                totals["failed"] += 1
                print(json.dumps(dict(event="source_failed", runId=run_id,
                    sourceId=hashlib.sha256(name.encode()).hexdigest(), reason="source exceeds byte limit")))
                if checkpoints and not config.dry_run:
                    checkpoints.put(receipt_key, dict(state="Rejected", stamp=utcnow()))
                continue
            if totals["sourceBytes"] + size > config.max_source:
                totals["continuation"] = True
                break
            totals["sourceBytes"] += size
            # Download/normalize in dry-run too, so expansion estimates are real.
            # No source content or attacker-controlled paths are printed.
            body = azure.read_blob(name, etag, min(size, config.max_blob))
            try:
                session = json.loads(body, parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
                source_run = receipt["runId"] if receipt else str(uuid.uuid4()) if checkpoints else run_id
                source_stamp = receipt["stamp"] if receipt else stamp
                rows = normalize(session, name, etag, source_run, source_stamp)
                pending = [(table, batch) for table in TABLES for batch in batches(rows[table])]
                generated = sum(len(batch) for _, batch in pending)
            except (ValueError, TypeError, RecursionError, ImportFailure) as error:
                totals["failed"] += 1
                reason = str(error) if isinstance(error, ImportFailure) else "invalid source JSON or metadata"
                print(json.dumps(dict(event="source_failed", runId=run_id,
                                      sourceId=hashlib.sha256(name.encode()).hexdigest(), reason=reason)))
                if checkpoints and not config.dry_run:
                    checkpoints.put(receipt_key, dict(state="Rejected", stamp=utcnow()))
                continue
            if totals["ingestionBytes"] + generated > config.max_ingestion:
                if generated > config.max_ingestion:
                    raise ImportFailure("one source exceeds ingestion budget; increase bounded limit")
                totals["continuation"] = True
                break
            totals["ingestionBytes"] += generated
            if not config.dry_run:
                receipt = dict(state="Pending", stamp=source_stamp, runId=source_run,
                               expected=rows["ImportStatus"][0]["ExpectedRows"])
                if checkpoints:
                    checkpoints.put(receipt_key, receipt)
                # Status is submitted last, but queries also count actual child rows
                # because cross-stream delivery is asynchronous and can be partial.
                for table, batch in pending:
                    azure.send(table, batch)
                totals["accepted"] += 1
                if checkpoints:
                    checkpoints.put(receipt_key, dict(receipt, state="Submitted"))
                    verification[source_run] = receipt["expected"]
        else:
            azure.next_cursor = {}  # Rescan for late arrivals; receipts skip delivered versions.
        if checkpoints and not config.dry_run:
            checkpoints.put(checkpoints.cursor_key, azure.next_cursor)
    except ScanLimit:
        totals["continuation"] = True
        if checkpoints and not config.dry_run:
            checkpoints.put(checkpoints.cursor_key, azure.next_cursor)
    except BaseException:
        totals["terminalStatus"] = "Failed"
        raise
    finally:
        try:
            if checkpoints and not config.dry_run and totals.get("terminalStatus") != "Failed":
                checkpoints.put("operations/" + run_id + ".json", dict(state="Finished", **totals,
                    runs=[dict(runId=key, expected=value) for key, value in verification.items()]))
        finally:
            print(json.dumps(dict(event="summary", attemptedIngestionBytes=getattr(azure, "attempted_ingestion_bytes", 0), **totals)))
            if checkpoints:
                checkpoints.release()
    return 2 if totals["failed"] else 0


def main():
    try:
        config = Config.load(os.environ)
        # Platform replica timeout is the second, independent enforcement layer.
        signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
        signal.signal(signal.SIGALRM, lambda *_: (_ for _ in ()).throw(ImportFailure("runtime limit reached")))
        signal.alarm(config.max_seconds)
        azure = Azure(os.environ, time.monotonic() + config.max_seconds)
        checkpoints = Checkpoints(azure, config) if os.environ.get("CHECKPOINT_CONTAINER") else None
        if os.environ.get("VERIFY_OPERATION"):
            operation = str(uuid.UUID(os.environ["VERIFY_OPERATION"]))
            if not checkpoints:
                raise ImportFailure("verification requires checkpoints")
            manifest = checkpoints.get("operations/" + operation + ".json")
            if not manifest or manifest.get("state") != "Finished":
                raise ImportFailure("operation manifest is missing or incomplete")
            while not azure.delivered_many(manifest["runs"]):
                time.sleep(min(15, azure.remaining()))
            print(json.dumps(dict(event="delivery_verified", operationId=operation,
                                  sources=len(manifest["runs"]), continuation=manifest["continuation"])))
            return 0
        operation = str(uuid.UUID(os.environ["OPERATION_ID"])) if os.environ.get("OPERATION_ID") else None
        return run(config, azure, run_id=operation, checkpoints=checkpoints)
    except (ImportFailure, ValueError, KeyError, TypeError, RecursionError, ET.ParseError) as error:
        # Never print arbitrary exceptions: HTTP/JSON errors may embed secrets.
        message = str(error) if isinstance(error, ImportFailure) else "invalid configuration or source metadata"
        print(json.dumps(dict(event="failed", reason=message)), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
