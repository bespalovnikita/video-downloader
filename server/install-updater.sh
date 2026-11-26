#!/usr/bin/env bash
set -euo pipefail
umask 077

usage() {
    printf '%s\n' 'Использование: sudo ./install-updater.sh vX.Y.Z из каталога server опубликованной версии.'
}

fail() {
    printf '%s\n' "$*" >&2
    exit 1
}

if [[ $* == --help ]]; then usage; exit 0; fi
[[ $# == 1 ]] || { usage >&2; exit 2; }
version=$1
[[ $version =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ && ${#version} -le 32 ]] || fail 'Укажи точный тег стабильного выпуска.'
[[ $EUID == 0 ]] || fail 'Для однократной установки запусти команду через sudo.'

export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
for tool in docker git flock install systemctl systemd-tmpfiles; do
    command -v "$tool" >/dev/null || fail "На сервере нужна команда $tool."
done
[[ -x /usr/bin/python3 ]] || fail 'На сервере нужен /usr/bin/python3.'
[[ -d /run/systemd/system ]] || fail 'Для службы обновления нужен systemd.'
docker compose version >/dev/null

server_dir=$(cd -- "$(dirname -- "$0")" && pwd -P)
[[ -f $server_dir/.env && ! -L $server_dir/.env ]] || fail 'В каталоге server нужен существующий файл .env.'
caller_uid=${SUDO_UID:-0}
caller_gid=${SUDO_GID:-0}
[[ $caller_uid =~ ^[0-9]+$ && $caller_gid =~ ^[0-9]+$ ]] || fail 'Не удалось определить пользователя исходного каталога.'
git_as_caller=()
if [[ $caller_uid != 0 ]]; then
    command -v setpriv >/dev/null || fail 'На сервере нужен setpriv из util-linux.'
    git_as_caller=(setpriv --reuid "$caller_uid" --regid "$caller_gid" --clear-groups --no-new-privs)
fi
source_git() {
    "${git_as_caller[@]}" env -i PATH="$PATH" HOME=/nonexistent GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null \
        git -C "$server_dir" -c core.fsmonitor=false -c core.hooksPath=/dev/null "$@"
}
repo_dir=$(source_git rev-parse --show-toplevel)
[[ $server_dir == "$repo_dir/server" ]] || fail 'Запусти установку из каталога server существующей копии репозитория.'
head=$(source_git rev-parse --verify 'HEAD^{commit}')
tagged=$(source_git rev-parse --verify "refs/tags/$version^{commit}")
[[ $head == "$tagged" ]] || fail "Переключи исходники на тег $version перед установкой."
changes=$(source_git status --porcelain=v1 --untracked-files=all -- .)
[[ -z $changes ]] || fail 'Сохрани чистые исходники server опубликованной версии. Файл .env сохраняется отдельно.'

config=/etc/video-downloader-updater.json
unit_name=video-downloader-updater.service
unit_path=/etc/systemd/system/$unit_name
tmpfiles_path=/etc/tmpfiles.d/video-downloader-updater.conf
libexec=/usr/local/libexec/video-downloader-updater
control=/run/video-downloader-updater
exec 9>/run/lock/video-downloader-updater-install.lock
flock -n 9 || fail 'Установка обновлений сервера уже выполняется.'
for destination in "$config" "$unit_path" "$tmpfiles_path" "$libexec"; do
    [[ ! -e $destination && ! -L $destination ]] || fail 'Служба обновления уже установлена или её файлы заняты. Сохрани текущую конфигурацию.'
done
[[ $(systemctl show -p LoadState --value "$unit_name") == not-found ]] || fail 'Имя службы обновления уже используется.'

/usr/bin/python3 -I - "$version" "$head" <<'PY'
import json
import sys
import urllib.request

version, commit = sys.argv[1:]
base = 'https://api.github.com/repos/bespalovnikita/video-downloader'

def get(path):
    url = base + path
    request = urllib.request.Request(url, headers={'Accept': 'application/vnd.github+json',
                                                   'User-Agent': 'Video-Downloader-Updater'})
    with urllib.request.urlopen(request, timeout=15) as response:
        if response.geturl() != url:
            raise ValueError('Unexpected release endpoint')
        payload = response.read(1024 * 1024 + 1)
    if len(payload) > 1024 * 1024:
        raise ValueError('Release response is too large')
    return json.loads(payload)

try:
    release = get('/releases/tags/' + version)
    tagged = get('/git/ref/tags/' + version)['object']
    if (release['tag_name'] != version or release['draft'] is not False or release['prerelease'] is not False
            or release['target_commitish'] != commit or tagged['type'] != 'commit' or tagged['sha'] != commit):
        raise ValueError('Release identity differs')
except (OSError, ValueError, KeyError, TypeError):
    sys.exit('Не удалось подтвердить соответствие исходников официальному стабильному выпуску GitHub.')
PY

downloader_id() {
    docker ps --quiet --filter label=com.docker.compose.project=video-downloader \
        --filter label=com.docker.compose.service=downloader
}
old_container=$(downloader_id)
[[ $old_container =~ ^[0-9a-f]{12,64}$ ]] || fail 'Нужен один запущенный контейнер downloader проекта video-downloader.'
base_files=$server_dir/compose.yaml
https_files=$base_files,$server_dir/compose.https.yaml
active_files=$(docker inspect --format '{{ index .Config.Labels "com.docker.compose.project.config_files" }}' "$old_container")
https_enabled=false
compose=(docker compose --project-name video-downloader --project-directory "$server_dir" --env-file "$server_dir/.env" -f "$base_files")
case "$active_files" in
    "$base_files") ;;
    "$https_files") https_enabled=true; compose+=(-f "$server_dir/compose.https.yaml") ;;
    *) fail 'Текущая конфигурация Docker Compose отличается от поддерживаемой. Очередь сохранена.' ;;
esac
old_image=$(docker inspect --format '{{.Image}}' "$old_container")
old_image_name=$(docker inspect --format '{{.Config.Image}}' "$old_container")
[[ $old_image =~ ^sha256:[0-9a-f]{64}$ && $old_image_name =~ ^video-downloader-downloader(:latest)?$ ]] ||
    fail 'Образ текущего контейнера отличается от штатной сборки проекта.'
overlay=("${compose[@]}" -f "$server_dir/compose.updater.yaml")
export VD_SERVER_VERSION=$version
"${overlay[@]}" config --quiet >/dev/null 2>&1 || fail 'Не удалось проверить конфигурацию Docker Compose.'

probe_downloader() {
    local container=$1 mode=$2
    /usr/bin/python3 -I - "$mode" "$version" <(docker inspect --format '{{json .}}' "$container" 2>/dev/null) <<'PY' || return
import base64
import http.client
import json
import sys

mode, version, inspection = sys.argv[1:]
try:
    with open(inspection, 'rb') as details:
        data = details.read(1024 * 1024 + 1)
    if len(data) > 1024 * 1024:
        raise ValueError('Inspection exceeds the size limit')
    container = json.loads(data)
    port_info, = container['NetworkSettings']['Ports']['8080/tcp']
    port = int(port_info['HostPort'])
    if port_info['HostIp'] != '127.0.0.1' or not 0 < port < 65536:
        raise ValueError('Expected only a published loopback port')
    env = dict(value.split('=', 1) for value in container['Config']['Env'])
    user, password = env['VD_SERVER_USER'], env['VD_SERVER_PASSWORD']
    if not user or not password:
        raise ValueError('Container credentials are absent')
    authorization = 'Basic ' + base64.b64encode(f'{user}:{password}'.encode('utf-8')).decode('ascii')

    def get(path, limit):
        connection = http.client.HTTPConnection('127.0.0.1', port, timeout=2)
        try:
            connection.request('GET', path, headers={'Authorization': authorization})
            response = connection.getresponse()
            payload = response.read(limit + 1)
            if response.status != 200 or len(payload) > limit:
                raise ValueError('Invalid response from the queue')
            return json.loads(payload)
        finally:
            connection.close()

    if mode == 'idle':
        if get('/api/jobs', 16 * 1024 * 1024).get('running') is not False:
            sys.exit('Очередь в браузере работает. Дождись её завершения перед установкой.')
    else:
        if get('/api/update/version', 4096) != {'version': version}:
            raise ValueError('Unexpected container version')
        updater = get('/api/update', 16 * 1024)
        if updater.get('supported') is not True or updater.get('currentVersion') != version:
            raise ValueError('Updater bridge is unavailable')
except (OSError, ValueError, KeyError, TypeError, AttributeError, http.client.HTTPException):
    sys.exit('Не удалось подтвердить состояние серверной очереди.')
PY
    if [[ $mode == idle ]]; then
        docker exec -i --user 10001:10001 "$container" python -I - <<'PY'
import sqlite3
import sys

try:
    with sqlite3.connect('file:/data/queue.sqlite3?mode=ro', uri=True, timeout=1) as db:
        if db.execute("SELECT 1 FROM jobs WHERE status = 'downloading' LIMIT 1").fetchone():
            sys.exit('Сервер скачивает файлы. Дождись завершения перед установкой.')
        if db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='remote_jobs'").fetchone():
            if db.execute("SELECT 1 FROM remote_jobs WHERE status IN ('queued', 'downloading') LIMIT 1").fetchone():
                sys.exit('Сервер обрабатывает задания Windows. Дождись их завершения перед установкой.')
except sqlite3.Error:
    sys.exit('Не удалось проверить активные задания серверной очереди.')
PY
    fi
}
probe_downloader "$old_container" idle

work=$(mktemp -d /tmp/video-downloader-updater.XXXXXXXX)
image_prepared=false
files_installed=false
service_requested=false
switch_attempted=false
cleanup() {
    status=$?
    trap - EXIT
    if ((status != 0)); then
        rollback_ok=true
        if $image_prepared; then
            docker image tag "$old_image" "$old_image_name" || rollback_ok=false
        fi
        if $switch_attempted && $rollback_ok; then
            "${compose[@]}" up -d --no-deps --no-build --pull never downloader >/dev/null 2>&1 || rollback_ok=false
        fi
        if $service_requested && $rollback_ok; then
            systemctl stop "$unit_name" >/dev/null 2>&1 || rollback_ok=false
        fi
        if $files_installed && $rollback_ok; then
            systemctl disable "$unit_name" >/dev/null 2>&1 || true
            rm -f -- "$config" "$unit_path" "$tmpfiles_path" "$libexec/host_updater.py"
            rmdir -- "$libexec" || true
            systemctl daemon-reload || true
        elif ! $rollback_ok; then
            printf '%s\n' 'Не удалось восстановить прежний контейнер. Данные очереди и файлы службы сохранены.' >&2
        fi
    fi
    rm -rf -- "$work"
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

source_git show "$head:server/host_updater.py" > "$work/host_updater.py"
source_git show "$head:server/video-downloader-updater.service" > "$work/updater.service"
source_git show "$head:server/video-downloader-updater.tmpfiles" > "$work/updater.conf"
/usr/bin/python3 -I - "$server_dir" "$https_enabled" "$version" > "$work/config.json" <<'PY'
import json
import sys

directory, https_enabled, version = sys.argv[1:]
print(json.dumps({'serverDirectory': directory, 'httpsEnabled': https_enabled == 'true', 'currentVersion': version}))
PY

image_prepared=true
printf '%s\n' "Собираю серверный образ $version."
"${overlay[@]}" build downloader >/dev/null 2>&1 || fail 'Не удалось собрать новый серверный образ.'
[[ $(downloader_id) == "$old_container" ]] || fail 'Во время подготовки изменился контейнер downloader. Повтори установку из исходного каталога.'
probe_downloader "$old_container" idle

files_installed=true
install -d -m 0755 "$libexec"
install -m 0644 "$work/host_updater.py" "$libexec/host_updater.py"
install -m 0644 "$work/updater.service" "$unit_path"
if [[ ! -d /etc/tmpfiles.d ]]; then install -d -m 0755 /etc/tmpfiles.d; fi
install -m 0644 "$work/updater.conf" "$tmpfiles_path"
install -m 0600 "$work/config.json" "$config"
systemd-tmpfiles --create "$tmpfiles_path"
systemctl daemon-reload
service_requested=true
systemctl enable --now "$unit_name"
for ((attempt=0; attempt<20; attempt++)); do
    if [[ -S $control/control.sock && -f $control/work.lock ]]; then break; fi
    sleep 1
done
[[ -S $control/control.sock && -f $control/work.lock ]] || fail 'Служба обновления не подготовила канал связи.'
exec 8<"$control/work.lock"
flock -n 8 || fail 'Служба обновления занята. Повтори установку после завершения операции.'

# The pre-updater process does not participate in work.lock. Recheck its queue
# immediately before replacing it, after the slow image build and service start.
[[ $(downloader_id) == "$old_container" ]] || fail 'Перед переключением изменился контейнер downloader.'
probe_downloader "$old_container" idle
switch_attempted=true
"${overlay[@]}" up -d --no-deps --no-build --pull never downloader >/dev/null 2>&1 || fail 'Не удалось переключить контейнер downloader.'
for ((attempt=0; attempt<30; attempt++)); do
    new_container=$(downloader_id)
    if [[ $new_container =~ ^[0-9a-f]{12,64}$ && $new_container != "$old_container" ]] &&
        probe_downloader "$new_container" ready 2>/dev/null; then
        printf '%s\n' "Служба обновления установлена. Веб-интерфейс сервера $version готов к проверке стабильных выпусков."
        exit 0
    fi
    sleep 1
done
fail 'После переключения не удалось подтвердить готовность сервера.'
