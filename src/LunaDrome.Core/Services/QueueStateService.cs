using System.Text.Json;
using LunaDrome.Core.Models;

namespace LunaDrome.Core.Services;

/// <summary>The play queue as it was when the app closed, so the next launch can pick up there.</summary>
public class QueueState
{
    // Only restored for the same server + user it was saved under.
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";

    public List<Song> Songs { get; set; } = new();
    public int CurrentIndex { get; set; } = -1;
    public double PositionSeconds { get; set; }
    public bool Shuffled { get; set; }
    public int RepeatMode { get; set; }

    // While shuffled: the pre-shuffle order, as indices into Songs (restored on un-shuffle).
    public List<int>? UnshuffledOrder { get; set; }
}

/// <summary>Reads/writes queue.json next to settings.json (%AppData% / ~/.config LunaDrome-Uno).</summary>
public static class QueueStateService
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LunaDrome-Uno", "queue.json");

    public static QueueState? Load()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<QueueState>(File.ReadAllText(StatePath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(QueueState state)
    {
        // Cover art URLs carry the Subsonic auth token + salt (a replayable credential) - never
        // write them to disk; they're rebuilt from AlbumId after loading.
        var songs = state.Songs.Select(s =>
        {
            var copy = s.Clone();
            copy.CoverArtUrl = null;
            return copy;
        }).ToList();

        var json = JsonSerializer.Serialize(new QueueState
        {
            ServerUrl = state.ServerUrl,
            Username = state.Username,
            Songs = songs,
            CurrentIndex = state.CurrentIndex,
            PositionSeconds = state.PositionSeconds,
            Shuffled = state.Shuffled,
            RepeatMode = state.RepeatMode,
            UnshuffledOrder = state.UnshuffledOrder,
        });

        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        // Write-then-rename so a crash mid-write can't leave a truncated queue.json behind.
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, StatePath, overwrite: true);
    }
}
