#!/usr/bin/env bash
set -Eeuo pipefail

APP_ID="${STEAM_APP_ID:-505460}"
GAME_DIR="${FOXHOLE_GAME_DIR:-/game}"
OUTPUT_DIR="${OUTPUT_DIR:-/output}"
STATE_DIR="${STATE_DIR:-/state}"
CONFIG="${EXTRACTION_CONFIG:-/app/config/extraction.json}"
MODS_DIR="${MODS_DIR:-/mods}"
STEAMCMD="${STEAMCMD:-/opt/steamcmd/steamcmd.sh}"
INTERVAL="${CHECK_INTERVAL_SECONDS:-21600}"

mkdir -p "$GAME_DIR" "$OUTPUT_DIR" "$STATE_DIR" "$MODS_DIR"

steam_login_args() {
  if [[ -n "${STEAM_USER:-}" ]]; then
    if [[ -z "${STEAM_PASSWORD:-}" ]]; then
      echo "ERROR: STEAM_USER is set but STEAM_PASSWORD is missing." >&2
      exit 2
    fi
    printf '%s\n' "+login" "$STEAM_USER" "$STEAM_PASSWORD"
  else
    printf '%s\n' "+login" "anonymous"
  fi
}

update_game() {
  echo "==> Updating Foxhole (Steam app $APP_ID)"
  if [[ -n "${STEAM_USER:-}" ]]; then
    echo "==> Steam account: $STEAM_USER"
    if [[ -t 0 ]]; then
      echo "==> Steam Guard: if SteamCMD requests a code, enter it below."
    else
      echo "==> No interactive stdin. If Steam Guard is required, run: docker compose run --rm extractor update" >&2
    fi
  else
    echo "==> No STEAM_USER configured; trying anonymous login. Foxhole normally requires an owning account."
  fi

  mapfile -t LOGIN < <(steam_login_args)
  "$STEAMCMD" \
    +@ShutdownOnFailedCommand 1 \
    +@sSteamCmdForcePlatformType windows \
    +force_install_dir "$GAME_DIR" \
    "${LOGIN[@]}" \
    +app_update "$APP_ID" validate \
    +quit
}

manifest_path() {
  find "$GAME_DIR" /opt/steamcmd -type f -name "appmanifest_${APP_ID}.acf" -print -quit 2>/dev/null || true
}

build_id() {
  local manifest
  manifest="$(manifest_path)"
  if [[ -n "$manifest" ]]; then
    awk -F'"' '/"buildid"/ { print $4; exit }' "$manifest"
    return
  fi
  find "$GAME_DIR" -type f -name '*.pak' -printf '%P|%s|%T@\n' 2>/dev/null \
    | sort | sha256sum | awk '{print "pak-"$1}'
}

extract_game() {
  local id="${1:-$(build_id)}"
  echo "==> Extracting Foxhole data (build: ${id:-unknown})"
  dotnet /app/FoxholeDataExtractor.dll extract \
    --game-dir "$GAME_DIR" \
    --output "$OUTPUT_DIR" \
    --config "$CONFIG" \
    --mods-dir "$MODS_DIR" \
    --build-id "${id:-unknown}"
}

run_once() {
  update_game
  local current previous
  current="$(build_id)"
  previous="$(cat "$STATE_DIR/last-build-id" 2>/dev/null || true)"

  if [[ "${FORCE:-0}" != "1" && -n "$current" && "$current" == "$previous" && -f "$OUTPUT_DIR/catalog.json" ]]; then
    echo "==> Build $current already extracted; nothing to do."
    return 0
  fi

  if [[ -f "$OUTPUT_DIR/catalog.json" ]]; then
    cp "$OUTPUT_DIR/catalog.json" "$STATE_DIR/catalog.previous.json"
  fi

  extract_game "$current"

  if [[ -f "$STATE_DIR/catalog.previous.json" && -f "$OUTPUT_DIR/catalog.json" ]]; then
    dotnet /app/FoxholeDataExtractor.dll diff \
      --old "$STATE_DIR/catalog.previous.json" \
      --new "$OUTPUT_DIR/catalog.json" \
      --output "$OUTPUT_DIR"
  fi

  printf '%s' "$current" > "$STATE_DIR/last-build-id"
  date -u +'%Y-%m-%dT%H:%M:%SZ' > "$STATE_DIR/last-success.txt"
  echo "==> Done. Output: $OUTPUT_DIR"
}

case "${1:-run}" in
  run|update-extract) run_once ;;
  force) FORCE=1 run_once ;;
  update) update_game ;;
  extract) extract_game ;;
  daemon)
    while true; do
      if ! run_once; then
        echo "WARN: update/extraction failed; retrying in ${INTERVAL}s" >&2
      fi
      sleep "$INTERVAL"
    done
    ;;
  steam-login)
    if [[ -z "${STEAM_USER:-}" || -z "${STEAM_PASSWORD:-}" ]]; then
      echo "ERROR: configure STEAM_USER and STEAM_PASSWORD in .env first." >&2
      exit 2
    fi
    echo "==> Interactive Steam login. Enter a Steam Guard code only if Steam asks for it."
    "$STEAMCMD" +@sSteamCmdForcePlatformType windows +login "$STEAM_USER" "$STEAM_PASSWORD"
    ;;
  info)
    echo "Game dir: $GAME_DIR"
    echo "Output:   $OUTPUT_DIR"
    echo "State:    $STATE_DIR"
    echo "Mods:     $MODS_DIR"
    echo "Build:    $(build_id)"
    dotnet /app/FoxholeDataExtractor.dll info --game-dir "$GAME_DIR" --output "$OUTPUT_DIR" || true
    ;;
  shell) exec /bin/bash ;;
  *) echo "Usage: $0 {run|force|update|extract|daemon|steam-login|info|shell}" >&2; exit 2 ;;
esac
