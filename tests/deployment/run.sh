#!/usr/bin/env bash
# Exercise deployment decisions without SSH, Docker, systemd, or a real server.
set -euo pipefail
REPO=$(cd -- "$(dirname -- "$0")/../.." && pwd)
TEST_ROOT=$(mktemp -d /tmp/tmailsharp-deploy.XXXXXXXX)
cleanup() {
  local status=$?
  if (( status != 0 )) && [[ -n ${CASE_DIR:-} && -f $CASE_DIR/output.log ]]; then
    echo "Deployment test failed: ${CASE_DIR##*/}" >&2
    cat "$CASE_DIR/output.log" >&2
  fi
  case "$TEST_ROOT" in /tmp/tmailsharp-deploy.*) rm -rf -- "$TEST_ROOT" ;; *) exit 99 ;; esac
}
trap cleanup EXIT
mkdir "$TEST_ROOT/bin"
for tool in docker curl id flock sleep systemctl; do
  cp "$REPO/tests/deployment/mock-command.sh" "$TEST_ROOT/bin/$tool"
done
chmod +x "$TEST_ROOT/bin/"*
export PATH="$TEST_ROOT/bin:$PATH"
[[ $(command -v docker) == "$TEST_ROOT/bin/docker" ]] || exit 99
REVISION=0123456789abcdef0123456789abcdef01234567
PASSED=0

setup_case() {
  local name=$1
  CASE_DIR="$TEST_ROOT/$name"
  mkdir -p "$CASE_DIR/containers" "$CASE_DIR/bundle" "$CASE_DIR/keys"
  export FAKE_DOCKER_STATE=$CASE_DIR
  export TMAIL_DEPLOY_ENV_FILE="$CASE_DIR/app.env"
  export FAKE_LOCK_BUSY=0 FAKE_UNHEALTHY_NEW=0 FAKE_WRITE_FAILURE=0 FAKE_START_FAILURE=0 FAKE_PROMOTE_FAILURE=0
  echo 0 > "$CASE_DIR/native"
  echo 'persistent key material' > "$CASE_DIR/keys/existing-key.xml"
  printf 'image bytes\n' > "$CASE_DIR/bundle/tmailsharp-image.tar.gz"
  (cd "$CASE_DIR/bundle" && sha256sum tmailsharp-image.tar.gz > tmailsharp-image.tar.gz.sha256)
  cat > "$CASE_DIR/app.env" <<EOF
Mongo__ConnectionString=mongodb://127.0.0.1:27017/tmailsharp
Admin__Password=mock-password-that-must-not-be-logged
DataProtection__KeyDirectory=$CASE_DIR/keys
Deploy__HttpPort=5080
Deploy__HealthHost=localhost
EOF
}

old_container() {
  mkdir "$CASE_DIR/containers/TMailSharp"
  echo tmailsharp:old > "$CASE_DIR/containers/TMailSharp/image"
  echo true > "$CASE_DIR/containers/TMailSharp/running"
  # Deliberately differ from the new port to verify rollback reuses the old setup.
  echo http://127.0.0.1:5070 > "$CASE_DIR/containers/TMailSharp/url"
  echo old.example.com > "$CASE_DIR/containers/TMailSharp/health-host"
  echo tmailsharp:old > "$CASE_DIR/tag-current"
}

run_deploy() {
  bash "$REPO/scripts/deploy-linux.sh" "$REVISION" "$CASE_DIR/bundle" > "$CASE_DIR/output.log" 2>&1
}

pass() {
  ! grep -q 'mock-password-that-must-not-be-logged' "$CASE_DIR/output.log"
  [[ $(cat "$CASE_DIR/keys/existing-key.xml") == 'persistent key material' ]]
  PASSED=$((PASSED + 1))
  echo "PASS: $1"
}

setup_case first-deploy
run_deploy
[[ $(cat "$CASE_DIR/tag-current") == "tmailsharp:$REVISION" ]]
[[ $(cat "$CASE_DIR/containers/TMailSharp/running") == true ]]
grep -q -- '--network host' "$CASE_DIR/commands.log"
grep -q -- 'ASPNETCORE_URLS=http://127.0.0.1:5080' "$CASE_DIR/commands.log"
[[ ! -e $CASE_DIR/bundle/tmailsharp-image.tar.gz ]]
pass 'first deployment promotes only after health and asset checks'

setup_case replace
old_container
run_deploy
[[ $(cat "$CASE_DIR/tag-previous") == sha256:previous-image ]]
[[ ! -e $CASE_DIR/containers/TMailSharp-rollback ]]
[[ $(cat "$CASE_DIR/containers/TMailSharp/image") == "tmailsharp:$REVISION" ]]
pass 'successful update preserves previous image and keys'

setup_case failed-start
old_container
export FAKE_START_FAILURE=1
if run_deploy; then echo 'Expected start failure' >&2; exit 1; fi
[[ $(cat "$CASE_DIR/tag-current") == tmailsharp:old ]]
[[ $(cat "$CASE_DIR/containers/TMailSharp/image") == tmailsharp:old ]]
[[ $(cat "$CASE_DIR/containers/TMailSharp/running") == true ]]
grep -q 'previous TMailSharp container is healthy again' "$CASE_DIR/output.log"
pass 'failed start restores the previous container and its original port'

setup_case unhealthy
old_container
export FAKE_UNHEALTHY_NEW=1
if run_deploy; then echo 'Expected readiness failure' >&2; exit 1; fi
[[ $(cat "$CASE_DIR/tag-current") == tmailsharp:old ]]
[[ $(cat "$CASE_DIR/containers/TMailSharp/image") == tmailsharp:old ]]
grep -q 'previous TMailSharp container is healthy again' "$CASE_DIR/output.log"
pass 'unhealthy replacement rolls back and remains a failed deployment'

setup_case promotion-failure
old_container
export FAKE_PROMOTE_FAILURE=1
if run_deploy; then echo 'Expected promotion failure' >&2; exit 1; fi
[[ $(cat "$CASE_DIR/containers/TMailSharp/image") == tmailsharp:old ]]
[[ $(cat "$CASE_DIR/tag-current") == tmailsharp:old ]]
pass 'promotion failure restores the original container'

setup_case missing-config
old_container
export TMAIL_DEPLOY_ENV_FILE="$CASE_DIR/not-found.env"
if run_deploy; then echo 'Expected configuration failure' >&2; exit 1; fi
! grep -q 'docker stop' "$CASE_DIR/commands.log"
[[ $(cat "$CASE_DIR/containers/TMailSharp/running") == true ]]
pass 'missing server configuration leaves the running app untouched'

setup_case corrupt-archive
old_container
echo corrupted >> "$CASE_DIR/bundle/tmailsharp-image.tar.gz"
if run_deploy; then echo 'Expected checksum failure' >&2; exit 1; fi
! grep -q 'docker load\|docker stop' "$CASE_DIR/commands.log"
pass 'corrupt image is rejected before replacement'

setup_case key-permissions
old_container
export FAKE_WRITE_FAILURE=1
if run_deploy; then echo 'Expected key permissions failure' >&2; exit 1; fi
! grep -q 'docker stop' "$CASE_DIR/commands.log"
[[ $(cat "$CASE_DIR/containers/TMailSharp/running") == true ]]
pass 'unwritable keys do not interrupt the existing app'

setup_case busy
old_container
export FAKE_LOCK_BUSY=1
if run_deploy; then echo 'Expected lock failure' >&2; exit 1; fi
! grep -q 'docker load\|docker stop' "$CASE_DIR/commands.log"
pass 'server lock rejects overlapping deployments'

setup_case native-fallback
echo 1 > "$CASE_DIR/native"
export FAKE_START_FAILURE=1
if run_deploy; then echo 'Expected first-container failure' >&2; exit 1; fi
[[ $(cat "$CASE_DIR/native") == 1 ]]
[[ ! -e $CASE_DIR/native-disabled ]]
[[ ! -e $CASE_DIR/containers/TMailSharp ]]
pass 'failed migration restores the previously running systemd service'

echo "$PASSED deployment checks passed. No real Docker or server commands were run."
