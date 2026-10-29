#!/usr/bin/env bash
# Deploy only TMailSharp. Application secrets stay in the server's environment file.
set -Eeuo pipefail

die() { printf '%s\n' "$*" >&2; exit 1; }
[[ $# == 2 ]] || die "Usage: deploy-linux.sh COMMIT_SHA BUNDLE_DIRECTORY"
REVISION=$1
[[ $REVISION =~ ^[a-f0-9]{40}$ ]] || die "Expected a full, lowercase Git commit SHA."
BUNDLE=$(cd -- "$2" && pwd)
ARCHIVE="$BUNDLE/tmailsharp-image.tar.gz"
CHECKSUM="$ARCHIVE.sha256"
IMAGE="tmailsharp:$REVISION"
CURRENT_IMAGE=tmailsharp:current
PREVIOUS_IMAGE=tmailsharp:previous
CONTAINER=TMailSharp
BACKUP_CONTAINER=TMailSharp-rollback
ENV_FILE=${TMAIL_DEPLOY_ENV_FILE:-/etc/tmailsharp/tmailsharp.env}

for tool in docker curl sha256sum flock; do
  command -v "$tool" >/dev/null 2>&1 || die "Required server command is missing: $tool"
done
docker info >/dev/null 2>&1 || die "Docker is unavailable to the deployment account."
[[ -r $ENV_FILE ]] || die "Required server environment file is unreadable: $ENV_FILE"
[[ -r $ARCHIVE && -r $CHECKSUM ]] || die "The image archive or checksum is missing."

# Read literal Docker env-file entries; never execute or print configuration values.
read_env() {
  awk -v prefix="$1=" 'index($0, prefix) == 1 { value=substr($0, length(prefix)+1); sub(/\r$/, "", value) } END { print value }' "$ENV_FILE"
}
read_container_env() {
  docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$CONTAINER" |
    awk -v prefix="$1=" 'index($0, prefix) == 1 { value=substr($0, length(prefix)+1) } END { print value }'
}

KEY_PATH=$(read_env DataProtection__KeyDirectory)
HTTP_PORT=$(read_env Deploy__HttpPort)
HEALTH_HOST=$(read_env Deploy__HealthHost)
HTTP_PORT=${HTTP_PORT:-5080}
HEALTH_HOST=${HEALTH_HOST:-localhost}
[[ $KEY_PATH =~ ^/[a-zA-Z0-9_./-]+$ && -d $KEY_PATH ]] || die "DataProtection__KeyDirectory must name an existing absolute directory without spaces, colons, or commas."
[[ $HTTP_PORT =~ ^[0-9]{1,5}$ ]] && ((10#$HTTP_PORT > 0 && 10#$HTTP_PORT <= 65535)) || die "Deploy__HttpPort must be a valid TCP port."
[[ $HEALTH_HOST =~ ^[a-zA-Z0-9.-]+(:[0-9]+)?$ ]] || die "Deploy__HealthHost must be a hostname allowed by the app."
[[ -n $(read_env Mongo__ConnectionString) ]] || die "Set Mongo__ConnectionString in the server environment file."
ADMIN_PASSWORD=$(read_env Admin__Password)
[[ ${#ADMIN_PASSWORD} -ge 20 ]] || die "Set Admin__Password to at least 20 characters in the server environment file."
unset ADMIN_PASSWORD

if id tmailsharp >/dev/null 2>&1; then
  APP_UID=$(id -u tmailsharp)
  APP_GID=$(id -g tmailsharp)
else
  APP_UID=$(stat -c '%u' "$KEY_PATH")
  APP_GID=$(stat -c '%g' "$KEY_PATH")
fi
[[ $APP_UID =~ ^[0-9]+$ && $APP_GID =~ ^[0-9]+$ && $APP_UID -ne 0 ]] || die "Create the tmailsharp service user and make it the owner of the key directory."

# A server-side lock also protects against deployments from a second repository.
exec 9>"$KEY_PATH/.deploy.lock"
flock -n 9 || die "Another TMailSharp deployment is in progress."
if docker container inspect "$BACKUP_CONTAINER" >/dev/null 2>&1; then
  die "A previous rollback container still exists. Resolve TMailSharp-rollback before deploying again."
fi

EXPECTED_HASH=$(awk 'NF == 2 { sub(/\r$/, "", $2); sub(/^\*/, "", $2); if ($2 == "tmailsharp-image.tar.gz") print $1 }' "$CHECKSUM")
[[ $EXPECTED_HASH =~ ^[a-f0-9]{64}$ ]] || die "The checksum file is invalid."
ACTUAL_HASH=$(sha256sum "$ARCHIVE")
[[ ${ACTUAL_HASH%% *} == "$EXPECTED_HASH" ]] || die "Image checksum mismatch; the current app has not been touched."

echo "Loading the verified TMailSharp image..."
docker load --input "$ARCHIVE"
docker image inspect "$IMAGE" >/dev/null
echo "Checking that the app user can write authentication keys..."
docker run --rm --user "${APP_UID}:${APP_GID}" \
  --mount "type=bind,source=${KEY_PATH},target=${KEY_PATH}" \
  --env "TMAIL_KEY_PATH=$KEY_PATH" --entrypoint /bin/sh "$IMAGE" -ec '
    probe=$(mktemp "$TMAIL_KEY_PATH/.write-check.XXXXXX")
    rm -- "$probe"
  '

OLD_EXISTS=0
OLD_RUNNING=false
OLD_IMAGE_ID=''
OLD_URL="http://127.0.0.1:$HTTP_PORT"
OLD_HEALTH_HOST=$HEALTH_HOST
OLD_STOPPED=0
OLD_RENAMED=0
NEW_ATTEMPTED=0
NATIVE_STOPPED=0
PROMOTED=0

if docker container inspect "$CONTAINER" >/dev/null 2>&1; then
  OLD_EXISTS=1
  OLD_REFERENCE=$(docker inspect --format '{{.Config.Image}}' "$CONTAINER")
  [[ $OLD_REFERENCE == tmailsharp:* ]] || die "Container TMailSharp belongs to another image; refusing to replace it."
  OLD_IMAGE_ID=$(docker inspect --format '{{.Image}}' "$CONTAINER")
  OLD_RUNNING=$(docker inspect --format '{{.State.Running}}' "$CONTAINER")
  OLD_URL=$(read_container_env ASPNETCORE_URLS)
  OLD_HEALTH_HOST=$(read_container_env Deploy__HealthHost)
  OLD_URL=${OLD_URL:-http://127.0.0.1:$HTTP_PORT}
  OLD_HEALTH_HOST=${OLD_HEALTH_HOST:-localhost}
  [[ $OLD_URL =~ ^http://127\.0\.0\.1:[0-9]{1,5}$ ]] || die "The previous container must have a loopback HTTP listener before automatic migration."
  [[ $OLD_HEALTH_HOST =~ ^[a-zA-Z0-9.-]+(:[0-9]+)?$ ]] || die "The previous container has an invalid health-check hostname."
fi

start_container() {
  docker run -d --name "$CONTAINER" --network host --restart unless-stopped \
    --init --stop-timeout 45 --user "${APP_UID}:${APP_GID}" \
    --label io.tmailsharp.managed=true --label "org.opencontainers.image.revision=$REVISION" \
    --log-opt max-size=10m --log-opt max-file=3 \
    --env-file "$ENV_FILE" \
    --env ASPNETCORE_ENVIRONMENT=Production \
    --env "ASPNETCORE_URLS=http://127.0.0.1:$HTTP_PORT" \
    --env "Deploy__HealthHost=$HEALTH_HOST" \
    --env "DataProtection__KeyDirectory=$KEY_PATH" \
    --env Demo__Enabled=false --env DOTNET_EnableDiagnostics=0 \
    --mount "type=bind,source=${KEY_PATH},target=${KEY_PATH}" "$IMAGE"
}

wait_until_ready() {
  local url=$1 hostname=$2 attempt
  for ((attempt=1; attempt<=30; attempt++)); do
    if ! docker ps --filter "name=^/${CONTAINER}$" --filter status=running -q | grep -q .; then
      return 1
    fi
    if curl --fail --silent --max-time 3 --header "Host: $hostname" "$url/health" |
      grep -q '"status"[[:space:]]*:[[:space:]]*"healthy"'; then
      if curl --fail --silent --max-time 3 --header "Host: $hostname" --header 'X-Forwarded-Proto: https' "$url/app.js" |
        grep -q 'initializeInbox'; then
        return 0
      fi
    fi
    sleep 2
  done
  return 1
}

finish() {
  local status=$?
  trap - EXIT INT TERM
  if (( status != 0 && PROMOTED == 0 )); then
    if (( NEW_ATTEMPTED == 1 )); then
      echo "Deployment failed; restoring the previous app." >&2
      docker logs "$CONTAINER" --tail 80 >&2 || true
      docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
    fi
    if (( OLD_RENAMED == 1 )); then
      docker rename "$BACKUP_CONTAINER" "$CONTAINER" || true
    fi
    if (( OLD_STOPPED == 1 )) && [[ $OLD_RUNNING == true ]]; then
      if docker start "$CONTAINER" && wait_until_ready "$OLD_URL" "$OLD_HEALTH_HOST"; then
        echo "The previous TMailSharp container is healthy again."
      else
        echo "Automatic container rollback failed; inspect TMailSharp and its logs." >&2
      fi
    fi
    if (( NATIVE_STOPPED == 1 )); then
      systemctl start tmailsharp.service || echo "Could not restore the previous tmailsharp systemd service." >&2
    fi
  fi
  # Remove only the files supplied by this run. No global image/container pruning.
  rm -f -- "$ARCHIVE" "$CHECKSUM"
  exit "$status"
}
trap finish EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

if command -v systemctl >/dev/null 2>&1 && systemctl is-active --quiet tmailsharp.service; then
  (( OLD_EXISTS == 0 )) || die "Both Docker and systemd are configured as active owners. Resolve this before deployment."
  systemctl stop tmailsharp.service
  NATIVE_STOPPED=1
fi
if (( OLD_EXISTS == 1 )); then
  if [[ $OLD_RUNNING == true ]]; then
    docker stop --time 45 "$CONTAINER" >/dev/null
    OLD_STOPPED=1
  fi
  docker rename "$CONTAINER" "$BACKUP_CONTAINER"
  OLD_RENAMED=1
fi

echo "Starting TMailSharp on loopback HTTP port $HTTP_PORT..."
NEW_ATTEMPTED=1
start_container
wait_until_ready "http://127.0.0.1:$HTTP_PORT" "$HEALTH_HOST" || die "The new container did not pass its MongoDB/SMTP and frontend health checks."
if [[ -n $OLD_IMAGE_ID ]]; then
  docker tag "$OLD_IMAGE_ID" "$PREVIOUS_IMAGE"
fi
docker tag "$IMAGE" "$CURRENT_IMAGE"
PROMOTED=1
if (( OLD_RENAMED == 1 )); then
  docker rm "$BACKUP_CONTAINER" >/dev/null || echo "The new app is ready, but the stopped rollback container needs manual cleanup." >&2
fi
if (( NATIVE_STOPPED == 1 )); then
  systemctl disable tmailsharp.service || echo "Disable the old tmailsharp systemd service before the next reboot." >&2
fi
echo "TMailSharp is ready. Current image: $IMAGE"
