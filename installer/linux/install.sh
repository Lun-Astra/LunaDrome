#!/usr/bin/env bash
# LunaDrome installer for the .tar.gz release (any x64 distro).
#   ./install.sh            install for the current user (~/.local), no root needed
#   sudo ./install.sh --system   install for all users (/opt + /usr/local)
# Remove again with ./uninstall.sh (or sudo ./uninstall.sh --system).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"

if [[ "${1:-}" == "--system" ]]; then
  prefix=/opt/lunadrome
  bindir=/usr/local/bin
  appdir=/usr/share/applications
  icondir=/usr/share/icons/hicolor/128x128/apps
else
  prefix="$HOME/.local/share/lunadrome"
  bindir="$HOME/.local/bin"
  appdir="$HOME/.local/share/applications"
  icondir="$HOME/.local/share/icons/hicolor/128x128/apps"
fi

echo "Installing LunaDrome to $prefix"
rm -rf "$prefix"
mkdir -p "$prefix" "$bindir" "$appdir" "$icondir"
cp -r "$here/app/." "$prefix/"
chmod +x "$prefix/LunaDrome"
ln -sf "$prefix/LunaDrome" "$bindir/lunadrome"
cp "$here/lunadrome.png" "$icondir/lunadrome.png"
sed "s|^Exec=.*|Exec=$prefix/LunaDrome|" "$here/lunadrome.desktop" > "$appdir/lunadrome.desktop"

command -v update-desktop-database >/dev/null && update-desktop-database "$appdir" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q "$(dirname "$(dirname "$icondir")")" >/dev/null 2>&1 || true

# Playback uses the system's VLC libraries; the visualizer uses parec (PulseAudio/PipeWire).
missing=()
ldconfig -p 2>/dev/null | grep -q 'libvlc\.so' || missing+=("libvlc (Debian/Ubuntu: libvlc5 vlc-plugin-base, Fedora: vlc-libs, Arch: vlc)")
command -v parec >/dev/null || missing+=("parec for the visualizer (Debian/Ubuntu: pulseaudio-utils, Fedora: pulseaudio-utils, Arch: libpulse)")
if (( ${#missing[@]} )); then
  echo
  echo "Also install:"
  printf '  - %s\n' "${missing[@]}"
fi

echo
echo "Done. Start LunaDrome from your app menu, or run: lunadrome"
