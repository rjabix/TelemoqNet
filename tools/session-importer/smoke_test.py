"""Administrative job execution override for end-to-end deployment validation.

Reads at most one existing source without printing its contents, then uploads
only synthetic records. Does not write to Blob Storage. Run via a job template
override, never through the honeypot VM. Not the normal importer entry point.
"""
import datetime as dt
import json
import os
import signal
import time
import uuid

import importer


def main():
    config = importer.Config.load(os.environ)
    signal.alarm(120)
    azure = importer.Azure(os.environ, time.monotonic() + 120)
    first = next(azure.blobs(config), None)
    if first:
        name, size, etag = first
        if size > config.max_blob:
            raise importer.ImportFailure('first source exceeds validation read limit')
        body = azure.read_blob(name, etag, min(size, config.max_blob))
        importer.normalize(json.loads(body), name, etag, 'validation-read-only', importer.utcnow())
    print(json.dumps(dict(event='private_storage_verified', sourceRead=first is not None)), flush=True)
    run_id = 'validation-' + str(uuid.uuid4())
    now = importer.utcnow()
    for protocol in ('telnet', 'mqtt'):
        session = dict(SchemaVersion=2, Protocol=protocol, DeviceProfile='AnalysisSelfTest',
            SessionId=str(uuid.uuid5(uuid.NAMESPACE_URL, run_id + protocol)), StartedAt=now,
            RemoteEndpoint='192.0.2.1:12345', Username='synthetic', AuthenticationSucceeded=False,
            LoginAttempts=[dict(Username='synthetic', Password='fake-example', Timestamp=now, Successful=False)],
            Commands=[dict(Command='echo synthetic', Timestamp=now, Response='🙂' * 3000)],
            Events=[dict(Sequence=1, Type='synthetic.validation', Timestamp=now, Detail='inert test')])
        rows = importer.normalize(session, '__analysis_validation__/' + run_id + '/' + protocol + '.json',
                                  'synthetic', run_id, now)
        for table in importer.TABLES:
            for batch in importer.batches(rows[table]):
                azure.send(table, batch)
    print(json.dumps(dict(event='validation_accepted', runId=run_id,
                          attemptedIngestionBytes=azure.attempted_ingestion_bytes)), flush=True)


if __name__ == '__main__':
    main()
