#!/bin/sh
# LunaDrome launcher (installed as `lunadrome`).
# Uno's Linux media player checks for a library named exactly "libvlc.so" before enabling
# playback, but distros' runtime VLC packages (e.g. Kubuntu's libvlc5) only ship libvlc.so.5 -
# the plain name comes with libvlc-dev. Without it the log says "libvlc.so was not found" and
# nothing plays. The installer links libvlc.so -> the system's libvlc.so.5 inside the app
# folder; putting that folder on the library path makes both Uno's check and LibVLC's own
# loading find it, without needing libvlc-dev.
APPDIR="@APPDIR@"
LIBDIR="$APPDIR"

# If the link isn't there (e.g. VLC was installed after LunaDrome), make it in the user's cache.
if [ ! -e "$APPDIR/libvlc.so" ]; then
  LIBDIR="${XDG_CACHE_HOME:-$HOME/.cache}/lunadrome/lib"
  mkdir -p "$LIBDIR"
  ldconfig_bin=$(command -v ldconfig || echo /sbin/ldconfig)
  for pair in libvlc.so.5:libvlc.so libvlccore.so.9:libvlccore.so; do
    soname=${pair%%:*}; link=${pair##*:}
    target=$("$ldconfig_bin" -p 2>/dev/null | awk -v n="$soname" '$1 == n { print $NF; exit }')
    [ -n "$target" ] && ln -sf "$target" "$LIBDIR/$link"
  done
  [ -e "$LIBDIR/libvlc.so" ] || echo "LunaDrome: VLC not found - install it for playback (Debian/Ubuntu: sudo apt install libvlc5 vlc-plugin-base)" >&2
fi

export LD_LIBRARY_PATH="$LIBDIR${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$APPDIR/LunaDrome" "$@"
