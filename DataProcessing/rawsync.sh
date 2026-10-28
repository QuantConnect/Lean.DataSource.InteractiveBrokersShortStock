#!/bin/bash
set -euo pipefail

today=$(date +%Y%m%d)
deployment_date=${QC_DATAFLEET_DEPLOYMENT_DATE:-$today}
raw_directory=/raw/equity/usa/shortable/interactivebrokers/$deployment_date
mkdir -p "$raw_directory"

# IB serves only the current snapshot, so a past date can only be restored from the archive
if [[ "$deployment_date" != "$today" ]]; then
    export AWS_ACCESS_KEY_ID=$ARCHIVE_AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY=$ARCHIVE_AWS_SECRET_ACCESS_KEY
    bucket=archive.quantconnect.com
    prefix=equity/usa/shortable/interactivebrokers/$deployment_date

    # Prints READY, RESTORING, ARCHIVED or MISSING
    archive_state() {
        local head
        if ! head=$(aws s3api head-object --bucket "$bucket" --key "$1" --query '[StorageClass, Restore]' --output text 2>/dev/null); then
            echo MISSING
        elif [[ "$head" != GLACIER* && "$head" != DEEP_ARCHIVE* ]] || [[ "$head" == *'ongoing-request="false"'* ]]; then
            echo READY
        elif [[ "$head" == *'ongoing-request="true"'* ]]; then
            echo RESTORING
        else
            echo ARCHIVED
        fi
    }

    restore() {
        # Expedited takes minutes but is not offered for DEEP_ARCHIVE and can be refused for capacity
        aws s3api restore-object --bucket "$bucket" --key "$1" --restore-request '{"Days":7,"GlacierJobParameters":{"Tier":"Expedited"}}' \
            || aws s3api restore-object --bucket "$bucket" --key "$1" --restore-request '{"Days":7,"GlacierJobParameters":{"Tier":"Standard"}}'
    }

    # Older snapshots sit in Glacier: request a restore, wait a few minutes, else fail so the date is re-run later
    for attempt in $(seq 1 10); do
        pending=0
        for file in usa.txt usa.txt.md5; do
            [[ -s "$raw_directory/$file" ]] && continue
            case $(archive_state "$prefix/$file") in
                MISSING)
                    if [[ "$file" == usa.txt ]]; then
                        echo "rawsync.sh: s3://$bucket/$prefix/usa.txt does not exist, $deployment_date cannot be recovered" >&2
                        exit 1
                    fi ;;
                READY)
                    aws s3 cp --force-glacier-transfer "s3://$bucket/$prefix/$file" "$raw_directory/$file" ;;
                ARCHIVED)
                    restore "$prefix/$file"
                    pending=1 ;;
                RESTORING)
                    pending=1 ;;
            esac
        done
        (( pending == 0 )) && exit 0
        sleep 60
    done
    echo "rawsync.sh: s3://$bucket/$prefix/ is still being restored from Glacier, re-run $deployment_date later" >&2
    exit 1
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
