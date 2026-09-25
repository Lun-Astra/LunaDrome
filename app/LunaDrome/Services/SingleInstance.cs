using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace LunaDrome.Services;

/// <summary>
/// One LunaDrome per user (#14): a second launch doesn't open another window (two players
/// fighting over the queue, media keys and queue.json) - it asks the running one to come to the
/// front and exits. A named mutex decides who's first; a named pipe carries the "show yourself"
/// request (named events don't exist on Linux, pipes and mutexes work on both).
/// Debug builds use their own name so a test build can run next to the installed app.
/// </summary>
internal static class SingleInstance
{
#if DEBUG
    private const string Flavor = "-debug";
#else
    private const string Flavor = "";
#endif

    private static readonly string Name =
        "LunaDrome-" + string.Concat(Environment.UserName.Where(char.IsLetterOrDigit)) + Flavor;

    // Kept referenced for the whole run: the mutex is held until the process exits.
    private static Mutex? s_mutex;

    /// <summary>Raised on a background thread when another launch asks this instance to show itself.</summary>
    public static event Action? ActivationRequested;

    /// <summary>True if this is the only instance (it then starts listening for later launches).</summary>
    public static bool TryClaim()
    {
        try
        {
            s_mutex = new Mutex(initiallyOwned: true, Name, out var createdNew);
            if (!createdNew)
            {
                s_mutex.Dispose();
                s_mutex = null;
                return false;
            }
        }
        catch (Exception ex)
        {
            // Can't tell - better a second window than an app that won't start.
            Console.Error.WriteLine($"[LunaDrome] single-instance check failed: {ex.Message}");
            return true;
        }

        _ = Task.Run(ListenAsync);
        return true;
    }

    private static async Task ListenAsync()
    {
        while (true)
        {
            try
            {
                await using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();
                ActivationRequested?.Invoke();
            }
            catch
            {
                await Task.Delay(1000);
            }
        }
    }

    /// <summary>Asks the running instance to come to the front.</summary>
    public static void ActivateExisting()
    {
        try
        {
            // Windows only lets the foreground process hand focus over; that's this launch.
            if (OperatingSystem.IsWindows())
                AllowSetForegroundWindow(-1 /* ASFW_ANY */);

            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LunaDrome] already running, but couldn't bring it to the front: {ex.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
