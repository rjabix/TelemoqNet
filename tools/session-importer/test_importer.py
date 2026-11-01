import base64
import contextlib
import datetime as dt
import hashlib
import io
import json
import unittest
from unittest.mock import patch
import urllib.error

import importer as app


def sample(protocol="telnet"):
    return {"SchemaVersion": 2, "SessionId": "a167b2db-dc44-4bf4-b347-4416c5f026a1",
            "StartedAt": "2026-10-31T22:00:00+00:00", "Protocol": protocol,
            "Username": "admin", "RemoteEndpoint": "192.0.2.4:12345",
            "LoginAttempts": [{"Username": "admin", "Password": "fake", "Successful": False}],
            "Commands": [{"Command": "uname -a", "Response": "Fake Linux"}],
            "Events": [{"Sequence": 1, "Type": "mqtt.publish", "Detail": "example"}]}


def config(**overrides):
    values = dict(start=dt.date(2026, 10, 31), end=dt.date(2026, 10, 31),
                  max_blobs=100, max_source=1_000_000, max_blob=500_000,
                  max_ingestion=2_000_000, max_seconds=20, dry_run=False)
    return app.Config(**dict(values, **overrides))


class FakeAzure:
    def __init__(self, sessions):
        self.sessions = [app.encode(value) if not isinstance(value, bytes) else value for value in sessions]
        self.sent = []
        self.reads = 0

    def remaining(self):
        return 10

    def blobs(self, _):
        for index, data in enumerate(self.sessions):
            yield "2026/10/31/" + str(index) + ".json", len(data), '"etag"'

    def read_blob(self, name, etag, limit):
        self.reads += 1
        return self.sessions[int(name.split("/")[-1].split(".")[0])]

    def send(self, table, data):
        self.sent.append((table, json.loads(data)))


class NormalizationTests(unittest.TestCase):
    def test_telnet_and_mqtt_keep_children_and_original_time(self):
        for protocol in ("telnet", "mqtt"):
            session = sample(protocol)
            rows = app.normalize(session, "blob", '"e"', "run", "2026-11-01T00:00:00Z")
            self.assertEqual(rows["Sessions"][0]["StartedAt"], session["StartedAt"])
            self.assertEqual(rows["Logins"][0]["Payload"], session["LoginAttempts"][0])
            self.assertEqual(rows["Events"][0]["Payload"], session["Events"][0])
            self.assertNotIn("Commands", rows["Sessions"][0]["Payload"])
            self.assertEqual(rows["Commands"][0]["RecordIndex"], 0)
            self.assertEqual(rows["ImportStatus"][0]["ExpectedRows"], 4)

    def test_large_unicode_entity_roundtrips_unknown_fields_and_nulls(self):
        session = sample()
        original = {"Command": "echo", "Response": "🙂\n\\\"" * 15_000,
                    "UnknownNested": {"new": [None, "", 3]}}
        session["Commands"] = [original]
        rows = app.normalize(session, "blob", "e", "run", "now")
        command = rows["Commands"][0]
        self.assertTrue(command["HasChunks"])
        chunks = sorted(rows["PayloadChunks"], key=lambda row: row["ChunkIndex"])
        raw = base64.b64decode("".join(row["ChunkData"] for row in chunks))
        self.assertEqual(json.loads(raw), original)
        self.assertEqual(hashlib.sha256(raw).hexdigest(), command["PayloadHash"])
        for row in chunks:
            self.assertLessEqual(len(row["ChunkData"].encode()), app.FIELD_BYTES)
        self.assertEqual(rows["ImportStatus"][0]["ExpectedRows"],
                         sum(len(values) for key, values in rows.items() if key != "ImportStatus"))

    def test_unknown_session_fields_are_preserved(self):
        session = sample()
        session["NewFirmwareField"] = {"empty": [], "nil": None}
        rows = app.normalize(session, "blob", "e", "run", "now")
        self.assertEqual(rows["Sessions"][0]["Payload"]["NewFirmwareField"], session["NewFirmwareField"])

    def test_chunk_keys_do_not_collide_between_child_tables(self):
        session = sample()
        large = {"Detail": "x" * 50_000}
        session["Commands"] = [large]
        session["Events"] = [large]
        rows = app.normalize(session, "b", "e", "r", "t")
        keys = [(r["EntityKind"], r["RecordIndex"], r["ChunkIndex"]) for r in rows["PayloadChunks"]]
        self.assertEqual(len(keys), len(set(keys)))
        self.assertEqual({r["EntityKind"] for r in rows["PayloadChunks"]},
                         {"CommandsPayloadChunks", "EventsPayloadChunks"})

    def test_normalized_row_limit_bounds_expansion(self):
        session = sample()
        session["Commands"] = [{}] * (app.MAX_NORMALIZED_ROWS + 1)
        with self.assertRaisesRegex(app.ImportFailure, "record limit"):
            app.normalize(session, "b", "e", "r", "t")

    def test_empty_and_invalid_collections(self):
        session = sample()
        session["Commands"] = []
        self.assertEqual(app.normalize(session, "b", "e", "r", "t")["Commands"], [])
        session["Commands"] = "untrusted"
        with self.assertRaises(app.ImportFailure):
            app.normalize(session, "b", "e", "r", "t")

    def test_schema_and_timestamp_validation(self):
        for field, value in (("SessionId", "bad"), ("StartedAt", "2026-01-01"),
                             ("Protocol", "http"), ("SchemaVersion", 999)):
            with self.assertRaises(app.ImportFailure):
                app.normalize(dict(sample(), **{field: value}), "b", "e", "r", "t")

    def test_batches_respect_wire_size_and_preserve_order(self):
        rows = [{"text": "\"🙂" * 5000, "index": index} for index in range(30)]
        output = list(app.batches(rows))
        self.assertGreater(len(output), 1)
        self.assertTrue(all(len(batch) <= app.BATCH_BYTES for batch in output))
        self.assertEqual([row for batch in output for row in json.loads(batch)], rows)


class ExecutionTests(unittest.TestCase):
    def run_quietly(self, cfg, azure):
        with contextlib.redirect_stdout(io.StringIO()):
            return app.run(cfg, azure, "2026-11-01T00:00:00Z", "test-run")

    def test_dry_run_reads_and_estimates_but_does_not_ingest(self):
        azure = FakeAzure([sample()])
        self.assertEqual(self.run_quietly(config(dry_run=True), azure), 0)
        self.assertEqual(azure.reads, 1)
        self.assertEqual(azure.sent, [])

    def test_malformed_source_is_counted_and_payload_never_logged(self):
        azure = FakeAzure([b'{"secret":"SENSITIVE"', sample()])
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            result = app.run(config(), azure)
        self.assertEqual(result, 2)
        self.assertNotIn("SENSITIVE", out.getvalue())
        self.assertEqual(sum(t == "ImportStatus" for t, _ in azure.sent), 1)

    def test_status_is_last_and_partial_submission_does_not_mark_complete(self):
        azure = FakeAzure([sample()])
        self.run_quietly(config(), azure)
        self.assertEqual(azure.sent[-1][0], "ImportStatus")
        azure = FakeAzure([sample()])
        original_send = azure.send
        def fail(table, body):
            if table == "Commands":
                raise app.ImportFailure("synthetic API failure")
            original_send(table, body)
        azure.send = fail
        with self.assertRaises(app.ImportFailure):
            self.run_quietly(config(), azure)
        self.assertFalse(any(t == "ImportStatus" for t, _ in azure.sent))

    def test_limits_fail_before_unbounded_read_or_ingestion(self):
        azure = FakeAzure([sample(), sample()])
        with self.assertRaises(app.ImportFailure):
            self.run_quietly(config(max_blobs=1), azure)
        self.assertEqual(azure.reads, 1)
        for limits in (dict(max_source=1), dict(max_blob=1), dict(max_ingestion=1)):
            azure = FakeAzure([sample()])
            with self.assertRaises(app.ImportFailure):
                self.run_quietly(config(**limits), azure)
            self.assertEqual(azure.sent, [])

    def test_replays_have_stable_source_indices(self):
        first = app.normalize(sample(), "blob", "etag", "run1", "t1")
        second = app.normalize(sample(), "blob", "etag", "run2", "t2")
        for table in ("Sessions", "Commands", "Events", "Logins"):
            self.assertEqual([(r["SourceBlob"], r["SourceETag"], r["RecordIndex"], r["PayloadHash"]) for r in first[table]],
                             [(r["SourceBlob"], r["SourceETag"], r["RecordIndex"], r["PayloadHash"]) for r in second[table]])

    def test_configuration_requires_dates_and_hard_limits(self):
        with self.assertRaises(ValueError):
            app.Config.load({})
        env = {"START_DATE": "2026-10-01", "END_DATE": "2026-10-31"}
        self.assertTrue(app.Config.load(env).dry_run)
        for changes in ({"END_DATE": "2026-11-01"}, {"MAX_BLOBS": "0"}, {"MAX_SECONDS": "99999"}, {"DRY_RUN": "yes"}):
            with self.assertRaises(app.ImportFailure):
                app.Config.load(dict(env, **changes))


class TransportTests(unittest.TestCase):
    def azure(self):
        return app.Azure({"BLOB_ENDPOINT": "https://example.blob.core.windows.net",
                          "INGESTION_ENDPOINT": "https://example.ingest.monitor.azure.com",
                          "BLOB_CONTAINER": "sessions"}, app.time.monotonic() + 100)

    def test_redirects_never_forward_bearer_token(self):
        self.assertIsNone(app.NoRedirect().redirect_request(None, None, 302, "", {}, "https://elsewhere"))

    def test_wrong_endpoint_rejected(self):
        with self.assertRaises(app.ImportFailure):
            app.Azure({"BLOB_ENDPOINT": "https://attacker.invalid",
                       "INGESTION_ENDPOINT": "https://example.ingest.monitor.azure.com",
                       "BLOB_CONTAINER": "sessions"}, 1)

    def test_throttling_retries_but_authorization_does_not(self):
        azure = self.azure()
        for status, attempts in ((429, 5), (403, 1), (500, 5)):
            error = urllib.error.HTTPError("https://redacted", status, "redacted", {"Retry-After": "0"}, None)
            with patch.object(azure.opener, "open", side_effect=error) as request, patch.object(app.time, "sleep"):
                with self.assertRaisesRegex(app.ImportFailure, "Azure HTTP status"):
                    azure.request("https://example", {})
                self.assertEqual(request.call_count, attempts)

    def test_runtime_budget_is_enforced(self):
        azure = self.azure()
        azure.deadline = app.time.monotonic() - 1
        with self.assertRaisesRegex(app.ImportFailure, "runtime limit"):
            azure.remaining()

    def test_ambiguous_upload_retries_also_consume_byte_budget(self):
        azure = self.azure()
        azure.ingestion_limit = 5
        error = urllib.error.HTTPError('https://redacted', 500, 'redacted', {}, None)
        with patch.object(azure.opener, 'open', side_effect=error) as request, patch.object(app.time, 'sleep'):
            with self.assertRaisesRegex(app.ImportFailure, 'upload budget'):
                azure.request(azure.ingestion + '/dataCollectionRules/dcr-test', {}, b'[]')
        self.assertEqual(request.call_count, 2)
        self.assertEqual(azure.attempted_ingestion_bytes, 4)

    def test_listing_follows_pagination_and_preserves_etag(self):
        azure = self.azure()
        first = b'<EnumerationResults><Blobs><Blob><Name>2026/10/31/a.json</Name><Properties><Content-Length>12</Content-Length><Etag>&quot;one&quot;</Etag></Properties></Blob></Blobs><NextMarker>next+page</NextMarker></EnumerationResults>'
        second = b'<EnumerationResults><Blobs><Blob><Name>2026/10/31/b.json</Name><Properties><Content-Length>20</Content-Length><Etag>&quot;two&quot;</Etag></Properties></Blob></Blobs></EnumerationResults>'
        with patch.object(azure, "request", side_effect=[(first, {}), (second, {})]) as request, patch.object(azure, "blob_headers", return_value={}):
            blobs = list(azure.blobs(config()))
        self.assertEqual(blobs, [('2026/10/31/a.json', 12, '"one"'), ('2026/10/31/b.json', 20, '"two"')])
        self.assertIn('marker=next%2Bpage', request.call_args_list[1].args[0])

    def test_blob_read_is_conditional_on_listed_version(self):
        azure = self.azure()
        with patch.object(azure, "request", return_value=(b'{}', {})) as request, patch.object(azure, "blob_headers", return_value={}):
            self.assertEqual(azure.read_blob('2026/10/31/a #.json', '"etag"', 100), b'{}')
        self.assertEqual(request.call_args.args[1]['If-Match'], '"etag"')
        self.assertIn('a%20%23.json', request.call_args.args[0])
        self.assertEqual(request.call_args.kwargs['max_bytes'], 100)

    def test_ingestion_uses_correct_audience_and_fixed_stream(self):
        azure = self.azure()
        azure.env['DCR_IMMUTABLE_ID'] = 'dcr-' + 'a' * 32
        with patch.object(azure, 'token', return_value='synthetic') as token, patch.object(azure, 'request') as request:
            azure.send('Commands', b'[]')
        token.assert_called_once_with('https://monitor.azure.com')
        self.assertIn('/streams/Custom-HoneypotCommands_CL?', request.call_args.args[0])


if __name__ == "__main__":
    unittest.main()
