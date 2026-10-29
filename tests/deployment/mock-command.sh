#!/usr/bin/env bash
set -euo pipefail
tool=${0##*/}
state=${FAKE_DOCKER_STATE:?}
printf '%s ' "$tool" >> "$state/commands.log"
printf '%q ' "$@" >> "$state/commands.log"
printf '\n' >> "$state/commands.log"

container_path() {
  case "$1" in TMailSharp|TMailSharp-rollback) printf '%s/containers/%s' "$state" "$1" ;; *) exit 90 ;; esac
}

case "$tool" in
  id)
    case "${1:-}" in -u|-g) echo 10001 ;; *) echo 'uid=10001(tmailsharp) gid=10001(tmailsharp)' ;; esac
    ;;
  flock) [[ ${FAKE_LOCK_BUSY:-0} != 1 ]] ;;
  sleep) : ;;
  systemctl)
    case "$1" in
      is-active) [[ $(cat "$state/native") == 1 ]] ;;
      stop) echo 0 > "$state/native" ;;
      start) echo 1 > "$state/native" ;;
      disable) touch "$state/native-disabled" ;;
      *) exit 91 ;;
    esac
    ;;
  curl)
    current=$(container_path TMailSharp)
    [[ -d $current && $(cat "$current/running") == true ]] || exit 7
    url=${!#}
    expected=$(cat "$current/url")
    [[ $url == "$expected/health" || $url == "$expected/app.js" ]] || exit 7
    if [[ ${FAKE_UNHEALTHY_NEW:-0} == 1 && $(cat "$current/image") != tmailsharp:old ]]; then exit 22; fi
    if [[ $url == */health ]]; then printf '{"status":"healthy"}\n'; else echo 'function initializeInbox() {}'; fi
    ;;
  docker)
    command=$1
    shift
    case "$command" in
      info) : ;;
      container)
        [[ $1 == inspect && -d $(container_path "$2") ]]
        ;;
      image)
        [[ $1 == inspect ]]
        ;;
      inspect)
        [[ $1 == --format ]]
        format=$2
        current=$(container_path "$3")
        [[ -d $current ]] || exit 1
        case "$format" in
          '{{.Config.Image}}') cat "$current/image" ;;
          '{{.Image}}') echo 'sha256:previous-image' ;;
          '{{.State.Running}}') cat "$current/running" ;;
          *) printf 'ASPNETCORE_URLS=%s\nDeploy__HealthHost=%s\n' "$(cat "$current/url")" "$(cat "$current/health-host")" ;;
        esac
        ;;
      load) : ;;
      run)
        if [[ $1 == --rm ]]; then [[ ${FAKE_WRITE_FAILURE:-0} != 1 ]]; exit; fi
        name='' url='' health_host=''
        while (($# > 1)); do
          case "$1" in
            --name) name=$2; shift 2 ;;
            --env)
              case "$2" in
                ASPNETCORE_URLS=*) url=${2#*=} ;;
                Deploy__HealthHost=*) health_host=${2#*=} ;;
              esac
              shift 2 ;;
            --env-file|--network|--restart|--stop-timeout|--user|--mount|--label|--log-opt) shift 2 ;;
            *) shift ;;
          esac
        done
        current=$(container_path "$name")
        [[ ! -e $current ]] || exit 125
        mkdir "$current"
        printf '%s\n' "$1" > "$current/image"
        printf '%s\n' "$url" > "$current/url"
        printf '%s\n' "$health_host" > "$current/health-host"
        echo true > "$current/running"
        if [[ ${FAKE_START_FAILURE:-0} == 1 ]]; then exit 125; fi
        echo fake-new-container
        ;;
      ps)
        current=$(container_path TMailSharp)
        if [[ -d $current && $(cat "$current/running") == true ]]; then echo fake-running-container; fi
        ;;
      stop)
        current=$(container_path "${!#}")
        echo false > "$current/running"
        ;;
      rename)
        from=$(container_path "$1")
        to=$(container_path "$2")
        [[ ! -e $to ]] || exit 1
        mv -- "$from" "$to"
        ;;
      start)
        current=$(container_path "$1")
        echo true > "$current/running"
        ;;
      rm)
        current=$(container_path "${!#}")
        if [[ -d $current ]]; then
          rm -f -- "$current/image" "$current/url" "$current/health-host" "$current/running"
          rmdir -- "$current"
        fi
        ;;
      tag)
        if [[ ${FAKE_PROMOTE_FAILURE:-0} == 1 && $2 == tmailsharp:current ]]; then exit 1; fi
        printf '%s\n' "$1" > "$state/tag-${2#*:}"
        ;;
      logs) echo 'mock container diagnostics' ;;
      *) echo "Unexpected docker command: $command" >&2; exit 92 ;;
    esac
    ;;
  *) exit 93 ;;
esac
