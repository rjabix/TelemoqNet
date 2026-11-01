#!/usr/bin/env bash
# Optional Cloud Shell fallback. Portal Run now supports the same overrides.
# Reads only the job definition through ARM; never reads captured blobs.
set -euo pipefail
if [[ $# -lt 4 || $# -gt 5 ]]; then
  echo "Usage: bash start-analysis.sh RESOURCE_GROUP JOB START_DATE END_DATE [true|false]" >&2
  exit 1
fi
resource_group=$1
job_name=$2
start_date=$3
end_date=$4
dry_run=${5:-true}
if [[ ! "$start_date" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ || ! "$end_date" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ || ! "$dry_run" =~ ^(true|false)$ ]]; then
  echo "Dates must be YYYY-MM-DD and dry-run must be true or false." >&2
  exit 1
fi
task_dir=$(mktemp -d)
trap 'rm -f "$task_dir/job.json" "$task_dir/template.json"; rmdir "$task_dir"' EXIT
# A read-only check helps avoid overlap, but is not a distributed lock.
active=$(az containerapp job execution list --resource-group "$resource_group" --name "$job_name" \
  --query "length([?properties.status=='Running' || properties.status=='Processing'])" -o tsv)
if [[ "$active" != "0" ]]; then
  echo "An execution is already active. Wait for it to finish." >&2
  exit 1
fi
az containerapp job show --resource-group "$resource_group" --name "$job_name" -o json > "$task_dir/job.json"
python3 - "$task_dir/job.json" "$task_dir/template.json" "$start_date" "$end_date" "$dry_run" <<'PY'
import datetime, json, os, sys
source, destination, start, end, dry = sys.argv[1:]
first, last = datetime.date.fromisoformat(start), datetime.date.fromisoformat(end)
if not 0 <= (last-first).days <= 30:
    raise SystemExit('Range must be inclusive and at most 31 days')
job = json.load(open(source))
template = job.get('properties', job)['template']
if len(template['containers']) != 1:
    raise SystemExit('Expected exactly one importer container')
env = template['containers'][0]['env']
updates = {'START_DATE': start, 'END_DATE': end, 'DRY_RUN': dry}
for name in ('OPERATION_ID', 'VERIFY_OPERATION', 'FORCE_REIMPORT'):
    if 'ANALYSIS_' + name in os.environ:
        updates[name] = os.environ['ANALYSIS_' + name]
for name in ('MAX_BLOBS', 'MAX_SOURCE_BYTES', 'MAX_BLOB_BYTES', 'MAX_INGESTION_BYTES', 'MAX_SECONDS'):
    if 'ANALYSIS_' + name in os.environ:
        updates[name] = os.environ['ANALYSIS_' + name]
for key, value in updates.items():
    matches = [item for item in env if item['name'] == key]
    if len(matches) != 1:
        raise SystemExit('Required importer setting missing')
    matches[0]['value'] = value
# JSON is valid YAML, so the CLI template reader accepts it without PyYAML here.
with open(destination, 'w') as handle:
    json.dump(template, handle)
PY
az containerapp job start --resource-group "$resource_group" --name "$job_name" --yaml "$task_dir/template.json"
