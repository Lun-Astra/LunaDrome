using System.ComponentModel;
using LunaDrome.Core.Services;

namespace LunaDrome.Controls;

/// <summary>
/// One "Download via SpotiDrome" request on the Downloads page: the SpotiDrome job it started
/// and its latest state (polled from GET /jobs/&lt;id&gt;).
/// </summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    public required string JobId { get; init; }
    public required string Label { get; init; }
    public required bool IsAlbum { get; init; }

    // What was sent, so a failed/interrupted download can be sent again (Retry).
    public required string Url { get; init; }
    public string? AlbumName { get; init; }

    // What to look for in the library once it's done (Play / Open).
    public required string Title { get; init; }
    public required string Artist { get; init; }

    public SpotiDromeJob? Job { get; private set; }
    public string? Error { get; private set; }

    public bool IsFinished => Job?.IsDone == true || Error != null;
    public bool Succeeded => Job?.IsDone == true && Job.Downloaded > 0;

    public string StatusText
    {
        get
        {
            if (Error != null)
                return Error;
            if (Unreachable)
                return "SpotiDrome isn't responding - still trying…";
            if (Job == null)
                return "Sent to SpotiDrome…";
            return Job.Status switch
            {
                "pending" => "Waiting for SpotiDrome…",
                "running" when Job.Total > 1 => $"Downloading {Job.Current} of {Job.Total}: {Job.CurrentTrack}",
                "running" => $"Downloading: {Job.CurrentTrack ?? Label}",
                "uploading" => "Copying to your Navidrome library…",
                "scanning" => "Waiting for Navidrome to pick it up…",
                "done" when Job.Downloaded == 0 && WasInterrupted => "Interrupted by a SpotiDrome restart.",
                "done" when Job.Downloaded == 0 && Job.Failed == 0 => "Already in your library.",
                "done" when Job.Downloaded == 0 => $"Failed - {LastLogLine()}",
                "done" when Job.Failed > 0 => $"Done: {Job.Downloaded} downloaded, {Job.Failed} failed.",
                "done" => IsAlbum ? $"In your library ({Job.Downloaded} songs)." : "In your library.",
                _ => Job.Status,
            };
        }
    }

    private string LastLogLine() => Job?.Log.LastOrDefault(l => l.StartsWith('✗')) ?? Job?.Log.LastOrDefault() ?? "see SpotiDrome";

    public double Progress => Job is { Total: > 0 } j ? Math.Clamp((double)j.Current / j.Total, 0, 1) : 0;
    public bool IsIndeterminate => !IsFinished && (Job == null || Job.Total == 0 || Job.Status is "uploading" or "scanning" or "pending");
    public Visibility ProgressVisibility => IsFinished ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The last status check failed (SpotiDrome down/overloaded); the download itself may be fine.</summary>
    public bool Unreachable { get; private set; }

    // Play (song) / Open (album) once it's in the library; Retry when nothing came of it
    // (failed, or cut off by a SpotiDrome restart - its log then says "Interrupted").
    public bool CanRetry => IsFinished && !Succeeded && !(Job is { IsDone: true, Downloaded: 0, Failed: 0 } && !WasInterrupted);
    private bool WasInterrupted => Job?.Log.Any(l => l.Contains("Interrupted", StringComparison.OrdinalIgnoreCase)) == true;
    public string ActionText => CanRetry ? "Retry" : IsAlbum ? "Open" : "Play";
    public Visibility ActionVisibility => IsFinished && (CanRetry || Job?.IsDone == true) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CancelVisibility => IsFinished ? Visibility.Collapsed : Visibility.Visible;

    public void SetUnreachable(bool unreachable)
    {
        if (Unreachable == unreachable)
            return;
        Unreachable = unreachable;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
    }

    public void Update(SpotiDromeJob? job, string? error = null)
    {
        Job = job ?? Job;
        Error = error;
        Unreachable = false;
        foreach (var name in new[] { nameof(StatusText), nameof(Progress), nameof(IsIndeterminate), nameof(ProgressVisibility),
                                     nameof(ActionText), nameof(ActionVisibility), nameof(CancelVisibility) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
