"""Offline SQLite export tests. All workspace responses are synthetic."""
import contextlib
import datetime as dt
import hashlib
import io
import json
from pathlib import Path
import sqlite3
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import importer
import test_importer as fixtures
from test_operations import operations

exporter = operations.sqlite_export
START = dt.date(2026, 10, 1)
END = dt.date(2026, 10, 7)


def export_rows():
    session = fixtures.sample()
    session['Commands'] = [
        {'Command': 'echo small', 'Response': 'small'},
        {'Command': 'echo large', 'Response': '🙂' * 6000, 'UnknownField': {'kept': True}},
    ]
    session['Events'] = [{'Type': 'payload.download_attempt', 'DownloadAttempt': {
        'Url': 'http://example.invalid/sample', 'Executed': False, 'Downloaded': False}}]
    records = importer.normalize(session, '2026/10/01/session.json', 'etag', 'run', '2026-10-02T00:00:00Z')
    rows = []
    for table, values in records.items():
        if table == 'ImportStatus':
            continue
        for value in values:
            row = {name: value.get(name) for name in exporter.COLUMNS}
            row.update(SourceAddress='203.0.113.42', Country='Unknown', City='Unknown',
                       Latitude=None, Longitude=None, AuthenticationSucceeded=True)
            key = [row[name] for name in exporter.KEY + ('ChunkIndex',)]
            row['ExportKey'] = hashlib.sha256(importer.encode(key)).hexdigest()
            rows.append(row)
    return sorted(rows, key=lambda row: row['ExportKey'])


class ExportTests(unittest.TestCase):
    def export(self, destination, rows, **overrides):
        queries = []

        def query(text):
            queries.append(text)
            if text.endswith('| count'):
                return [{'Count': len(rows)}]
            after = text.split("| where ExportKey > '")[1].split("'")[0]
            limit = int(text.rsplit('| take ', 1)[1])
            return [row for row in rows if row['ExportKey'] > after][:limit]

        with contextlib.redirect_stdout(io.StringIO()):
            exporter.export_sqlite('workspace', destination, START, END, query=overrides.get('query', query))
        return queries

    def test_paged_database_is_queryable_and_reconstructs_large_unicode_payloads(self):
        rows = export_rows()
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / 'sessions.sqlite'
            with patch.object(exporter, 'PAGE_SIZE', 2):
                queries = self.export(destination, rows)
            self.assertGreater(len(queries), 2)
            with contextlib.closing(sqlite3.connect(destination)) as connection:
                self.assertEqual(connection.execute('SELECT count(*) FROM records').fetchone()[0], len(rows))
                commands = connection.execute('SELECT Command,Response FROM commands ORDER BY RecordIndex').fetchall()
                self.assertEqual(commands, [('echo small', 'small'), ('echo large', '🙂' * 6000)])
                self.assertEqual(connection.execute(
                    "SELECT json_extract(Payload, '$.UnknownField.kept') FROM commands WHERE RecordIndex=1"
                ).fetchone()[0], 1)
                self.assertEqual(connection.execute('SELECT SourceAddress FROM sessions').fetchone()[0], '203.0.113.42')
                self.assertEqual(connection.execute(
                    "SELECT json_extract(DownloadAttempt, '$.Downloaded') FROM events"
                ).fetchone()[0], 0)
                self.assertEqual(connection.execute(
                    "SELECT Value FROM export_metadata WHERE Name='EndDateInclusive'"
                ).fetchone()[0], END.isoformat())
            self.assertEqual(destination.stat().st_mode & 0o777, 0o600)
            self.assertEqual(list(Path(directory).iterdir()), [destination])
        self.assertIn('datetime(2026-10-08)', queries[0])
        self.assertIn('HoneypotImportStatus_CL | where ingestion_time() <= datetime(', queries[0])
        self.assertIn("SourceBlob !startswith '__analysis_validation__/'", queries[0])

    def test_chunk_hash_failure_does_not_publish_or_leave_temp_data(self):
        rows = export_rows()
        next(row for row in rows if row['EntityKind'].endswith('PayloadChunks'))['ChunkData'] = 'corrupted'
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / 'sessions.sqlite'
            with self.assertRaisesRegex(RuntimeError, 'reconstruction|hash verification'):
                self.export(destination, rows)
            self.assertEqual(list(Path(directory).iterdir()), [])

    def test_truncated_page_does_not_publish_database(self):
        rows = export_rows()
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / 'sessions.sqlite'
            with self.assertRaisesRegex(RuntimeError, 'truncated'):
                self.export(destination, rows, query=lambda text: [{'Count': len(rows)}] if text.endswith('| count') else [])
            self.assertFalse(destination.exists())

    def test_empty_export_still_has_queryable_views(self):
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / 'sessions.sqlite'
            self.export(destination, [])
            with contextlib.closing(sqlite3.connect(destination)) as connection:
                self.assertEqual(connection.execute('SELECT count(*) FROM commands').fetchone()[0], 0)

    def test_existing_file_is_preserved_without_querying(self):
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / 'sessions.sqlite'
            destination.write_bytes(b'existing')
            with patch.object(exporter, 'query_workspace') as query:
                with self.assertRaisesRegex(RuntimeError, 'already exists'):
                    self.export(destination, [])
            query.assert_not_called()
            self.assertEqual(destination.read_bytes(), b'existing')

    def test_row_and_disk_limits_do_not_publish_database(self):
        for limit, value in [('MAX_ROWS', 1), ('MAX_BYTES', 1)]:
            with self.subTest(limit=limit), tempfile.TemporaryDirectory() as directory:
                destination = Path(directory) / 'sessions.sqlite'
                with patch.object(exporter, limit, value), self.assertRaisesRegex(RuntimeError, 'limit'):
                    self.export(destination, export_rows())
                self.assertEqual(list(Path(directory).iterdir()), [])

    def test_api_partial_errors_are_not_exported(self):
        response = {'tables': [{'columns': [{'name': 'Count'}], 'rows': [[1]]}],
                    'error': {'code': 'PartialError'}}
        result = subprocess.CompletedProcess([], 0, json.dumps(response), '')
        with patch.object(exporter.subprocess, 'run', return_value=result):
            with self.assertRaisesRegex(RuntimeError, 'partial result'):
                exporter.query_workspace('00000000-0000-0000-0000-000000000001', '| count')

    def test_api_columns_are_mapped_and_operator_authentication_is_used(self):
        response = {'tables': [{'columns': [{'name': 'Count', 'type': 'long'}], 'rows': [[12]]}]}
        result = subprocess.CompletedProcess([], 0, json.dumps(response), '')
        workspace = '00000000-0000-0000-0000-000000000001'
        with patch.object(exporter.subprocess, 'run', return_value=result) as request:
            self.assertEqual(exporter.query_workspace(workspace, '| count'), [{'Count': 12}])
        arguments = request.call_args.args[0]
        self.assertIn('https://api.loganalytics.azure.com/v1/workspaces/' + workspace + '/query', arguments)
        self.assertEqual(arguments[arguments.index('--resource') + 1], 'https://api.loganalytics.io')


class EntryPointTests(unittest.TestCase):
    def test_export_only_skips_setup_lock_refresh_and_import(self):
        with tempfile.TemporaryDirectory() as directory:
            destination = str(Path(directory) / 'sessions.sqlite')
            with patch.object(operations.sys, 'argv', ['analysis-once.py', '--sqlite', destination, '--sqlite-only',
                                                     '--var-file', '/missing/settings.tfvars']), \
                    patch.object(operations, 'setup') as setup, \
                    patch.object(operations, 'ControlLock') as lock, \
                    patch.object(operations, 'refresh_state') as refresh, \
                    patch.object(operations, 'execute') as execute, \
                    patch.object(operations, 'terraform', return_value='{"workspace_customer_id":"workspace"}'), \
                    patch.object(exporter, 'export_sqlite') as export:
                operations.main()
            setup.assert_not_called()
            lock.assert_not_called()
            refresh.assert_not_called()
            execute.assert_not_called()
            self.assertEqual(export.call_args.args[:2], ('workspace', destination))

    def test_regular_export_runs_after_runtime_cleanup(self):
        events = []
        with tempfile.NamedTemporaryFile() as settings, tempfile.TemporaryDirectory() as directory:
            with patch.object(operations.sys, 'argv', ['analysis-once.py', '--skip-setup', '--var-file', settings.name,
                                                     '--sqlite', str(Path(directory) / 'sessions.sqlite')]), \
                    patch.object(operations, 'ControlLock'), \
                    patch.object(operations, 'refresh_state'), \
                    patch.object(operations, 'recover_pending_cleanup'), \
                    patch.object(operations, 'execute', side_effect=lambda _: events.append('import-and-cleanup')), \
                    patch.object(operations, 'download_sqlite', side_effect=lambda _: events.append('export')):
                operations.main()
        self.assertEqual(events, ['import-and-cleanup', 'export'])

    def test_export_is_not_attempted_after_import_failure(self):
        with tempfile.NamedTemporaryFile() as settings, tempfile.TemporaryDirectory() as directory:
            with patch.object(operations.sys, 'argv', ['analysis-once.py', '--skip-setup', '--var-file', settings.name,
                                                     '--sqlite', str(Path(directory) / 'sessions.sqlite')]), \
                    patch.object(operations, 'ControlLock'), \
                    patch.object(operations, 'refresh_state'), \
                    patch.object(operations, 'recover_pending_cleanup'), \
                    patch.object(operations, 'execute', side_effect=RuntimeError('import failed')), \
                    patch.object(operations, 'download_sqlite') as export:
                with self.assertRaisesRegex(RuntimeError, 'import failed'):
                    operations.main()
            export.assert_not_called()

    def test_invalid_flag_combinations_stop_before_azure_calls(self):
        for flags in [ ['--sqlite-only'], ['--sqlite', '--dry-run-only'], ['--sqlite', '--cleanup-only'] ]:
            with self.subTest(flags=flags), patch.object(operations.sys, 'argv', ['analysis-once.py', *flags]), \
                    patch.object(operations, 'setup') as setup, \
                    patch.object(operations, 'terraform') as terraform, contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit):
                    operations.main()
                setup.assert_not_called()
                terraform.assert_not_called()
