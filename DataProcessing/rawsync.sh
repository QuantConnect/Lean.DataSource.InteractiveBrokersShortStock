#!/bin/bash

current_date=$(date +%Y%m%d)
mkdir -p /raw/equity/usa/shortable/interactivebrokers/$current_date

lftp -u shortstock,anonymous ftp://ftp2.interactivebrokers.com <<EOF
mirror --target-directory /raw/equity/usa/shortable/interactivebrokers/$current_date -v
exit
EOF
