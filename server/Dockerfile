FROM denoland/deno:bin-2.9.8 AS deno

FROM python:3.12-slim-trixie

RUN sed -i 's|http://deb.debian.org|https://deb.debian.org|g' /etc/apt/sources.list.d/debian.sources \
    && apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates ffmpeg nftables tini util-linux \
    && rm -rf /var/lib/apt/lists/* \
    && pip install --no-cache-dir --no-compile 'yt-dlp[default]==2026.11.23' \
    && groupadd --gid 10001 downloader \
    && useradd --uid 10001 --gid downloader --no-create-home downloader \
    && install -d -m 0700 -o downloader -g downloader /data

COPY --from=deno /deno /usr/local/bin/deno

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    DENO_DIR=/tmp/deno \
    VD_DATA_DIR=/data \
    VD_SERVER_HOST=0.0.0.0 \
    VD_SERVER_PORT=8080

WORKDIR /app
COPY --chmod=0444 server.py queue_worker.py egress.nft ./
COPY --chmod=0444 static/* ./static/
COPY --chmod=0555 entrypoint.sh ./

EXPOSE 8080
ENTRYPOINT ["/app/entrypoint.sh"]
