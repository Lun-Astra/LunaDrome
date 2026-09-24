#!/usr/bin/env bash
# Builds the Linux release artifacts from a linux-x64 publish folder (run on Linux, e.g. CI):
#   installer/linux/build-packages.sh <publish-dir> <version> <out-dir>
# Produces:
#   lunadrome_<version>_amd64.deb         Debian/Ubuntu package (apt install ./file.deb)
#   LunaDrome-<version>-linux-x64.tar.gz  any distro: extract, run ./install.sh
set -euo pipefail

publish="$(realpath "$1")"; version="$2"; out="$(realpath -m "$3")"
here="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$out"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

chmod +x "$publish/LunaDrome"

# ---- .deb ----
deb="$work/deb"
mkdir -p "$deb/DEBIAN" "$deb/opt/lunadrome" "$deb/usr/bin" "$deb/usr/share/applications" \
         "$deb/usr/share/icons/hicolor/128x128/apps"
cp -r "$publish/." "$deb/opt/lunadrome/"
sed "s|@APPDIR@|/opt/lunadrome|" "$here/lunadrome-launcher.sh" > "$deb/usr/bin/lunadrome"
chmod 755 "$deb/usr/bin/lunadrome"
cp "$here/lunadrome.desktop" "$deb/usr/share/applications/lunadrome.desktop"
cp "$here/lunadrome.png" "$deb/usr/share/icons/hicolor/128x128/apps/lunadrome.png"
size_kb=$(du -sk "$deb" | cut -f1)
cat > "$deb/DEBIAN/control" <<EOF
Package: lunadrome
Version: $version
Section: sound
Priority: optional
Architecture: amd64
Maintainer: LunAstra <https://github.com/Lun-Astra/LunaDrome>
Installed-Size: $size_kb
Depends: libvlc5, vlc-plugin-base, libx11-6, libfontconfig1, libgl1
Recommends: pulseaudio-utils
Homepage: https://github.com/Lun-Astra/LunaDrome
Description: Music player for your Navidrome server
 LunaDrome is a desktop client for Navidrome (Subsonic API) with a
 Feishin-style interface, playlists, lyrics and an audio visualizer.
EOF

# Fallback for the libvlc name problem (the app also maps it at runtime, see Program.cs):
# LibVLCSharp asks for plain "libvlc", i.e. libvlc.so, but libvlc5 only ships libvlc.so.5.
# Link the system's versioned libs next to the app, where .NET probes first.
cat > "$deb/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
link_lib() {
  target=$(ldconfig -p 2>/dev/null | awk -v n="$1" '$1 == n { print $NF; exit }')
  [ -n "$target" ] && ln -sf "$target" "/opt/lunadrome/$2" || true
}
link_lib libvlc.so.5 libvlc.so
link_lib libvlccore.so.9 libvlccore.so
exit 0
EOF
cat > "$deb/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = "remove" ] || [ "$1" = "purge" ]; then
  rm -f /opt/lunadrome/libvlc.so /opt/lunadrome/libvlccore.so
  rmdir /opt/lunadrome 2>/dev/null || true
fi
exit 0
EOF
chmod 755 "$deb/DEBIAN/postinst" "$deb/DEBIAN/postrm"

# xz, not dpkg-deb's Ubuntu default zstd: older Debian dpkg versions can't read zstd packages.
dpkg-deb -Zxz --build --root-owner-group "$deb" "$out/lunadrome_${version}_amd64.deb"

# ---- .tar.gz ----
tgz="$work/LunaDrome-$version-linux-x64"
mkdir -p "$tgz/app"
cp -r "$publish/." "$tgz/app/"
cp "$here/install.sh" "$here/uninstall.sh" "$here/lunadrome.desktop" "$here/lunadrome.png" "$here/lunadrome-launcher.sh" "$tgz/"
chmod +x "$tgz/install.sh" "$tgz/uninstall.sh" "$tgz/app/LunaDrome"
tar -C "$work" -czf "$out/LunaDrome-$version-linux-x64.tar.gz" "LunaDrome-$version-linux-x64"

ls -lh "$out"
