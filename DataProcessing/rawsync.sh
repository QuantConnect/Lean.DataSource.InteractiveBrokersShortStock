#!/bin/bash
set -euo pipefail

today=$(date +%Y%m%d)
deployment_date=${QC_DATAFLEET_DEPLOYMENT_DATE:-$today}
raw_directory=/raw/equity/usa/shortable/interactivebrokers/$deployment_date
mkdir -p "$raw_directory"

# IB serves only the current snapshot, so a past date can only be restored from the archive
if [[ "$deployment_date" != "$today" ]]; then
    AWS_ACCESS_KEY_ID=$ARCHIVE_AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY=$ARCHIVE_AWS_SECRET_ACCESS_KEY \
        aws s3 sync "s3://archive.quantconnect.com/equity/usa/shortable/interactivebrokers/$deployment_date/" "$raw_directory"
    exit 0
fi

# A miss is recoverable only today, so retry before failing. The converter verifies usa.txt against usa.txt.md5
attempts=5
for attempt in $(seq 1 $attempts); do
    # lftp retries forever by default; cap retries/timeouts and add a hard timeout so a dead server can't hang the job
    if timeout 300 lftp -u shortstock,anonymous ftp://ftp2.interactivebrokers.com -e "set net:timeout 30; set net:max-retries 2; set net:persist-retries 1; set net:reconnect-interval-base 5; set net:reconnect-interval-max 10; mirror --target-directory $raw_directory -v; exit" \
        && [[ -s "$raw_directory/usa.txt" ]]; then
        exit 0
    fi
    echo "rawsync.sh: attempt $attempt of $attempts failed to fetch a valid usa.txt" >&2
    if (( attempt < attempts )); then
        sleep $(( attempt * 60 ))
    fi
done
exit 1
