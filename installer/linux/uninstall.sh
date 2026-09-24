#!/usr/bin/env bash
# Removes a LunaDrome install made with install.sh (use --system if you installed with it).
# Your settings/login in ~/.config/LunaDrome-Uno are kept; delete that folder to remove them too.
set -euo pipefail

if [[ "${1:-}" == "--system" ]]; then
  prefix=/opt/lunadrome; bindir=/usr/local/bin; appdir=/usr/share/applications
  icondir=/usr/share/icons/hicolor/128x128/apps
else
  prefix="$HOME/.local/share/lunadrome"; bindir="$HOME/.local/bin"; appdir="$HOME/.local/share/applications"
  icondir="$HOME/.local/share/icons/hicolor/128x128/apps"
fi

rm -rf "$prefix"
rm -f "$bindir/lunadrome" "$appdir/lunadrome.desktop" "$icondir/lunadrome.png"
command -v update-desktop-database >/dev/null && update-desktop-database "$appdir" >/dev/null 2>&1 || true
echo "LunaDrome removed."
