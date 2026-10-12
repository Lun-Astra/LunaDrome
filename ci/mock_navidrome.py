#!/usr/bin/env python3
"""Tiny fake Navidrome (Subsonic API) for the Android UI test: enough for LunaDrome to log in,
show Home and list songs on the Tracks page. Any username/password is accepted."""
import json, struct, zlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

ARTISTS = [("ar1", "Test Artist"), ("ar2", "Other Band")]
ALBUMS = [(f"al{i}", f"Test Album {i}", ARTISTS[i % 2]) for i in range(1, 7)]
SONGS = []
for n in range(1, 61):
    al = ALBUMS[(n - 1) % len(ALBUMS)]
    SONGS.append({"id": f"s{n}", "title": f"Song number {n:02d}", "album": al[1], "albumId": al[0],
                  "artist": al[2][1], "artistId": al[2][0], "track": (n - 1) // len(ALBUMS) + 1,
                  "duration": 180 + n, "genre": "Rock" if n % 2 else "Pop", "year": 2020,
                  "coverArt": al[0], "suffix": "flac", "isDir": False, "type": "music"})

def album_json(al):
    songs = [s for s in SONGS if s["albumId"] == al[0]]
    return {"id": al[0], "name": al[1], "artist": al[2][1], "artistId": al[2][0], "coverArt": al[0],
            "songCount": len(songs), "duration": sum(s["duration"] for s in songs), "year": 2020, "genre": "Rock"}

def png(r, g, b, size=8):
    raw = b"".join(b"\x00" + bytes([r, g, b]) * size for _ in range(size))
    chunk = lambda t, d: struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 2, 0, 0, 0)) \
        + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")

class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        print("[mock]", self.path.split("?")[0], flush=True)

    def send_json(self, body):
        data = json.dumps({"subsonic-response": {"status": "ok", "version": "1.16.1", "type": "navidrome", **body}}).encode()
        self.send_response(200); self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data))); self.end_headers(); self.wfile.write(data)

    def do_GET(self):
        u = urlparse(self.path); q = {k: v[0] for k, v in parse_qs(u.query).items()}
        ep = u.path.rsplit("/", 1)[-1].replace(".view", "")
        if ep == "getCoverArt":
            i = sum(map(ord, q.get("id", "x")))
            data = png(i * 37 % 256, i * 59 % 256, i * 83 % 256)
            self.send_response(200); self.send_header("Content-Type", "image/png")
            self.send_header("Content-Length", str(len(data))); self.end_headers(); self.wfile.write(data); return
        if ep == "stream":
            # 3 minutes of silence (8 kHz mono WAV), so the player really plays.
            frames = 8000 * 180
            data = b"RIFF" + struct.pack("<I", 36 + frames * 2) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, 1, 8000, 16000, 2, 16) \
                + b"data" + struct.pack("<I", frames * 2) + bytes(frames * 2)
            self.send_response(200); self.send_header("Content-Type", "audio/wav")
            self.send_header("Content-Length", str(len(data))); self.end_headers(); self.wfile.write(data); return
        if ep == "ping":
            return self.send_json({})
        if ep == "search3":
            off, cnt = int(q.get("songOffset", 0)), int(q.get("songCount", 20))
            return self.send_json({"searchResult3": {"song": SONGS[off:off + cnt], "album": [], "artist": []}})
        if ep == "getAlbumList2":
            return self.send_json({"albumList2": {"album": [album_json(a) for a in ALBUMS]}})
        if ep == "getAlbum":
            al = next((a for a in ALBUMS if a[0] == q.get("id")), ALBUMS[0])
            return self.send_json({"album": {**album_json(al), "song": [s for s in SONGS if s["albumId"] == al[0]]}})
        if ep == "getArtists":
            return self.send_json({"artists": {"index": [{"name": "T", "artist": [{"id": a, "name": n, "albumCount": 3} for a, n in ARTISTS]}]}})
        if ep == "getArtist":
            ar = next((a for a in ARTISTS if a[0] == q.get("id")), ARTISTS[0])
            return self.send_json({"artist": {"id": ar[0], "name": ar[1], "album": [album_json(a) for a in ALBUMS if a[2] == ar]}})
        if ep == "getGenres":
            return self.send_json({"genres": {"genre": [{"value": "Rock", "songCount": 30, "albumCount": 3}, {"value": "Pop", "songCount": 30, "albumCount": 3}]}})
        if ep == "getPlaylists":
            return self.send_json({"playlists": {"playlist": [{"id": "p1", "name": "Test Playlist", "songCount": 20, "coverArt": "al1"}]}})
        if ep == "getPlaylist":
            return self.send_json({"playlist": {"id": "p1", "name": "Test Playlist", "songCount": 20, "entry": SONGS[:20]}})
        if ep in ("getLyrics", "getLyricsBySongId"):
            return self.send_json({"lyrics": {}, "lyricsList": {"structuredLyrics": []}})
        if ep in ("scrobble", "star", "unstar"):
            return self.send_json({})
        self.send_response(404); self.end_headers()

if __name__ == "__main__":
    print("[mock] fake Navidrome on :4533", flush=True)
    ThreadingHTTPServer(("0.0.0.0", 4533), Handler).serve_forever()
