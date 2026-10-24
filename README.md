# LunaDrome

A self-hosted music player for a personal [Navidrome](https://www.navidrome.org/) server, talking to
it via the Subsonic API. Inspired by Feishin / Finamp / Ensemble, built to grow alongside a
self-hosted music downloader and SendSpin Bluetooth speaker support.

Built with [Uno Platform](https://platform.uno/) (C# / WinUI XAML, Skia renderer), so one codebase
runs on Windows, Linux, Android and iOS (macOS may follow). (The original WPF
Windows-only app was retired in favor of this one; it's still in git history.)

## Features

- Navidrome login with an encrypted remembered password (AES-GCM, local key file)
- Feishin-style Home: hero carousel, genre tiles, Most Played / Recently Played / Recently Added /
  Explore rows with arrow paging, playlists
- Artist and album pages (Feishin layout), clickable album / artist names everywhere
- Albums grid, library-wide track list with sort / search / resizable columns
- Playlists (create, delete, add / remove tracks), favorites, scrobbling
- Queue, synced / plain lyrics, and an audio visualizer (WASAPI process loopback on Windows,
  PulseAudio / PipeWire monitor on Linux) in a full-screen player
- Search, with **"Download via SpotiDrome"**: YouTube Music songs and whole albums go straight
  into the Navidrome library through [SpotiDrome](https://github.com/Lun-Astra/SpotiDrome)
- **Jam** page through [JamiDrome](https://github.com/Lun-Astra/Jamidrome): be the shared jam's
  speaker, share invite links (QR), control playback, Auto DJ and the queue

## Connecting SpotiDrome and JamiDrome

Both servers require an **API key** (Settings in LunaDrome has a field for each, stored
encrypted like the password):

- **SpotiDrome:** in its web UI, Settings → Access → API keys, scope **LunaDrome** (search,
  downloads and their progress only). Address: the web UI, e.g. `https://muziekapp.example.com`.
- **JamiDrome:** on its player page, Host Controls → API keys. Address: the player page, e.g.
  `https://jam.example.com`.

Paste the key under the address and press **Save & test** / **Connect**. A key is shown only once
when it's created; if it's revoked, LunaDrome says so and asks for a new one.

## Structure

```
app/
  LunaDrome.sln
  LunaDrome/                 Uno Platform app (net10.0-desktop)
    App.xaml(.cs)            Styles (dark palette, control templates), window setup
    MainPage.xaml(.cs)       All pages: sidebar, Home, Artists, artist/album pages, Albums,
                             Tracks, playlists, full-screen player, playback bar
    Controls/                Motion (hover/press animations), TrackColumns, CarouselHeader,
                             HomeLayout, VisualizerCanvas, WheelScroll, converters
    Services/                LoopbackCapture (visualizer audio), BlurredArt (header blur/tint)
src/
  LunaDrome.Core/            UI-free library: NavidromeClient, models, settings, credentials,
                             SpectrumAnalyzer
```

## Installing

Grab the latest build from the [Releases](https://github.com/Lun-Astra/LunaDrome/releases) page:

- **Windows:** `LunaDrome-Setup-<version>.exe` - per-user install (no admin), Start menu +
  optional desktop shortcut, uninstall from Apps & features.
- **Debian / Ubuntu:** `sudo apt install ./lunadrome_<version>_amd64.deb` (pulls in VLC libraries).
- **Android:** `LunaDrome-<version>-android.apk` - open it on the phone and allow installing apps from your browser/file manager when asked. Phones get a compact layout (☰ menu); no visualizer on Android yet.
- **iPhone / iPad:** `LunaDrome-<version>-ios.ipa` - unsigned, so install it with [AltStore](https://altstore.io) or [SideStore](https://sidestore.io) and your own (free) Apple ID; they re-sign it every 7 days. Same compact layout as Android, with lock-screen / Control Center controls and background playback; no visualizer and no CarPlay (that needs an Apple entitlement).
- **Other Linux (x64):** extract `LunaDrome-<version>-linux-x64.tar.gz`, run `./install.sh`
  (or `sudo ./install.sh --system`); needs libvlc, and `parec` for the visualizer.

### Connecting to your server

Sign in with your server's **https** address (`https://music.example.com`) whenever the server
has one, not `http://` or a local IP. Over plain http your password and music travel
unencrypted, and some features only work over https - for example the album covers on Discord
(Discord only loads https images, so with an http address it shows the LunaDrome logo instead).

### Navidrome server settings

A few LunaDrome features need a setting on the Navidrome server (in `navidrome.toml`, or the
matching `ND_...` environment variable for Docker; restart Navidrome afterwards):

| Setting | Needed for |
|---|---|
| `EnableSharing = true` (`ND_ENABLESHARING=true`) | **Share song** - without it Navidrome refuses to make share links. |
| `Subsonic.ArtistParticipations = true` (`ND_SUBSONIC_ARTISTPARTICIPATIONS=true`) | Optional: other Subsonic apps then also list the albums an artist only appears on. LunaDrome finds those itself, so it doesn't need this. |

Songs by several artists also need each artist tagged separately (a multi-valued `ARTISTS`
tag); a single "Artist A, Artist B" text becomes one combined artist, because Navidrome doesn't
split on commas. SpotiDrome writes these tags itself, and Settings > Library Maintenance >
Split Combined Artists adds them to songs it downloaded earlier.

**Discord album covers** use Navidrome's public cover links, so they only show when the server
can be reached from the internet over https (and you signed in with that https address, see
above); otherwise Discord shows the LunaDrome logo. Vesktop / Vencord (arRPC) may not show the
covers at all - Discord's own app does.

Releases are built by GitHub Actions (`.github/workflows/release.yml`) when a version tag is
pushed: `git tag v3.4.0 && git push origin v3.4.0`. Installer sources are in `installer/`.

## Running (development)

```
dotnet run --project app/LunaDrome -f net10.0-desktop
```

## Publishing the app you actually use

```
dotnet publish app/LunaDrome/LunaDrome.csproj -f net10.0-desktop -c Release -r win-x64 --self-contained true -o publish/LunaDrome
```

Self-contained (no .NET install needed). It's a folder, not a single-file exe, because the audio
engine (LibVLC) loads its plugins from disk next to the exe. The desktop shortcut points at
`publish/LunaDrome/LunaDrome.exe`. For Linux use `-r linux-x64`.

## Branches

`dev` is where changes land and get tested; `main` is what gets published.
