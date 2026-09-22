# LunaDrome

A self-hosted music player for Windows, talking to a personal [Navidrome](https://www.navidrome.org/)
server via the Subsonic API. Inspired by clients like Finamp / Ensemble / Feishin, but built to grow
alongside a self-hosted music downloader and SendSpin Bluetooth speaker support.

## Stack

- .NET 6, WPF (Windows desktop)
- Subsonic API (token auth) for talking to Navidrome
- DPAPI (`System.Security.Cryptography.ProtectedData`) for encrypted local password storage

## Structure

```
src/Musicplayer.App/
  Models/            Artist, Album, Song
  Services/
    NavidromeClient.cs   Subsonic API client (auth, browse, stream/cover art URLs, all-albums/all-songs)
    SettingsService.cs   Persists server URL, username, and an encrypted (DPAPI) password
    SubsonicDto.cs        Raw JSON wire-format types
    DarkTitleBar.cs       Forces the native Windows title bar into dark mode
  Views/
    LoginWindow.xaml(.cs) Navidrome login screen (auto-signs in if a password was remembered)
  MainWindow.xaml(.cs)    Sidebar nav (Home / Artists / Albums / Songs) + queue + playback bar
```

## Running

```
dotnet run --project src/Musicplayer.App
```

## Building a standalone .exe

```
dotnet publish src/Musicplayer.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Produces a single `LunaDrome.exe` (no .NET runtime install required) in
`src/Musicplayer.App/bin/Release/net6.0-windows/win-x64/publish/`.

On first launch you get a login screen: enter your Navidrome server URL, username, and password.
If "Remember me" is checked, the password is encrypted (Windows DPAPI, tied to your user account)
and next launch signs you in automatically.

Once signed in:
- **Home** — recently added albums
- **Artists** — browse artist → album → song
- **Albums** — every album in your library
- **Songs** — every song in your library
- Right-click any song for Play now / Play next / Add to queue
- Hover an album cover for a quick play button
- Queue panel on the right (toggle with the ☰ button in the playback bar)

## Current scope (v0)

- [x] Navidrome login (Subsonic token auth against `/rest/ping.view`), with encrypted remembered password
- [x] Home / Artists / Albums / Songs navigation
- [x] Album art
- [x] Queue: per-song add/play-next/play-now, whole-album queueing, collapsible panel
- [x] Playback: play/pause/stop/next/prev, seek bar, volume
- [ ] Search
- [ ] Playlists
- [ ] SendSpin Bluetooth speaker output
- [ ] Self-hosted music downloader integration

### Known risk to verify

`GetAllSongsAsync` (the "Songs" page) queries Subsonic's `search3.view` with an **empty query
string**, assuming Navidrome treats that as "match everything". This hasn't been confirmed against
a real server yet — if the Songs page loads empty or errors, this needs to fall back to iterating
albums and pulling their tracks instead.

See `PROJECT_MEMORY.md` for decisions and conversation history.
