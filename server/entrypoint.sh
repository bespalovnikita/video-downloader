#!/bin/sh
set -eu

nft -f /app/egress.nft
exec setpriv --reuid=downloader --regid=downloader --clear-groups \
    --no-new-privs --bounding-set=-all --inh-caps=-all --ambient-caps=-all \
    tini -- python /app/server.py
