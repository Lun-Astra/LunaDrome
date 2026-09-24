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
ln -s /opt/lunadrome/LunaDrome "$deb/usr/bin/lunadrome"
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
# xz, not dpkg-deb's Ubuntu default zstd: older Debian dpkg versions can't read zstd packages.
dpkg-deb -Zxz --build --root-owner-group "$deb" "$out/lunadrome_${version}_amd64.deb"

# ---- .tar.gz ----
tgz="$work/LunaDrome-$version-linux-x64"
mkdir -p "$tgz/app"
cp -r "$publish/." "$tgz/app/"
cp "$here/install.sh" "$here/uninstall.sh" "$here/lunadrome.desktop" "$here/lunadrome.png" "$tgz/"
chmod +x "$tgz/install.sh" "$tgz/uninstall.sh" "$tgz/app/LunaDrome"
tar -C "$work" -czf "$out/LunaDrome-$version-linux-x64.tar.gz" "LunaDrome-$version-linux-x64"

ls -lh "$out"
