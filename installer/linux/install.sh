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
rm -f "$bindir/lunadrome"
sed "s|@APPDIR@|$prefix|" "$here/lunadrome-launcher.sh" > "$bindir/lunadrome"
chmod +x "$bindir/lunadrome"
cp "$here/lunadrome.png" "$icondir/lunadrome.png"
sed "s|^Exec=.*|Exec=$bindir/lunadrome|" "$here/lunadrome.desktop" > "$appdir/lunadrome.desktop"

# The app asks for plain "libvlc" (libvlc.so), but most distros' runtime packages only ship the
# versioned file (libvlc.so.5). Link it next to the app so playback works without libvlc-dev.
# (The app also handles this itself at runtime; this is a fallback.)
link_lib() {
  local target
  target=$(ldconfig -p 2>/dev/null | awk -v n="$1" '$1 == n { print $NF; exit }')
  [[ -n "$target" ]] && ln -sf "$target" "$prefix/$2"
  return 0
}
link_lib libvlc.so.5 libvlc.so
link_lib libvlccore.so.9 libvlccore.so

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
