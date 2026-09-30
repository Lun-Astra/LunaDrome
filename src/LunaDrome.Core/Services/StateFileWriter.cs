using System.Collections.Concurrent;

namespace LunaDrome.Core.Services;

/// <summary>Writes the small state files (queue.json, preferences.json) either right away or on a
/// background thread. The playback timer saves every ~10s, and doing the JSON + disk write on the UI
/// thread made scrolling hitch. Per file, the newest save always wins: a background save that
/// finishes after a newer one (e.g. the synchronous save on exit) is dropped.</summary>
public static class StateFileWriter
{
    private sealed class Slot
    {
        public readonly object Gate = new();
        public long Next;
        public long Written;
    }

    private static readonly ConcurrentDictionary<string, Slot> Slots = new();

    public static void Write(string path, Func<string> content)
    {
        var (slot, seq) = Reserve(path);
        WriteCore(path, slot, seq, content);
    }

    /// <summary><paramref name="content"/> runs on the background thread, so it must only read
    /// data the caller won't change anymore (a snapshot).</summary>
    public static void WriteInBackground(string path, Func<string> content, Action<Exception>? onError = null)
    {
        var (slot, seq) = Reserve(path);
        Task.Run(() =>
        {
            try
            {
                WriteCore(path, slot, seq, content);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        });
    }

    private static (Slot, long) Reserve(string path)
    {
        var slot = Slots.GetOrAdd(path, _ => new Slot());
        return (slot, Interlocked.Increment(ref slot.Next));
    }

    private static void WriteCore(string path, Slot slot, long seq, Func<string> content)
    {
        lock (slot.Gate)
        {
            if (seq < slot.Written)
                return;

            var text = content();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write-then-rename so a crash mid-write can't leave a truncated file behind.
            var temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
            slot.Written = seq;
        }
    }
}
