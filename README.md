# LunaDrome

A self-hosted music player for a personal [Navidrome](https://www.navidrome.org/) server, talking to
it via the Subsonic API. Inspired by Feishin / Finamp / Ensemble, built to grow alongside a
self-hosted music downloader and SendSpin Bluetooth speaker support.

Built with [Uno Platform](https://platform.uno/) (C# / WinUI XAML, Skia renderer), so one codebase
runs on Windows and Linux today, with macOS / iOS / Android as later targets. (The original WPF
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
