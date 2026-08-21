#!/usr/bin/env bash
# tizen-dev.sh — sdb connect/install/run helpers (Task 19.1).
# Usage:
#   tizen-dev.sh connect [ip]        connect the TV (default 192.168.0.152)
#   tizen-dev.sh install <tpk>       sdb install a built package
#   tizen-dev.sh run                 launch NuvioTVN01.NuvioTV
#   tizen-dev.sh uninstall           remove the package
#   tizen-dev.sh logs                tail dlog for NuvioTV
set -euo pipefail

TV_IP="${2:-192.168.0.152}"
TPK_PATH="$(pwd)/native/src/NuvioTV.Tizen/bin/Release/tizen70/NuvioTVN01-*.tpk"
SDB="${TIZEN_SDB:-$HOME/tizen-studio/tools/sdb}"
APP_ID="NuvioTVN01.NuvioTV"
PACKAGE_ID="NuvioTVN01"

cmd="${1:-help}"

case "$cmd" in
  connect)
    "$SDB" connect "$TV_IP:26101"
    "$SDB" devices
    ;;
  install)
    tpk="${3:-}"
    if [[ -z "$tpk" ]]; then
      tpk=$(ls $TPK_PATH 2>/dev/null | head -n 1)
    fi
    if [[ -z "$tpk" || ! -f "$tpk" ]]; then
      echo "no .tpk found (build first: npm run native:build)" >&2
      exit 1
    fi
    "$SDB" install "$tpk"
    ;;
  run)
    # tizen CLI drives the install API directly when shell is blocked.
    TIZEN_CLI="${TIZEN_CLI:-$HOME/tizen-studio/tools/ide/bin/tizen}"
    "$TIZEN_CLI" run -p "$APP_ID" || echo "[tizen-dev] visual confirmation required on TV" >&2
    ;;
  uninstall)
    "$SDB" uninstall "$PACKAGE_ID" 2>/dev/null || \
      "$SDB" shell "rm -rf /opt/usr/apps/$PACKAGE_ID" 2>/dev/null || true
    ;;
  logs)
    "$SDB" dlog | grep -i nuvio || true
    ;;
  *)
    sed -n '2,9p' "$0"
    ;;
esac
