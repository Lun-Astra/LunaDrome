using System.Reflection;
using System.Runtime.InteropServices;
using Uno.UI.Hosting;

namespace LunaDrome;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.InitializeLogging();

        if (OperatingSystem.IsLinux())
            UseSystemLibVlcOnLinux();

        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            .UseWin32()
            .Build();

        host.Run();
    }

    // Playback on Linux uses the distro's libvlc (Uno's X11 MediaPlayer -> LibVLCSharp). LibVLCSharp
    // P/Invokes a library named plain "libvlc", which .NET resolves to the file "libvlc.so" - but
    // Debian/Ubuntu/Kubuntu's runtime package (libvlc5) only ships "libvlc.so.5"; the unversioned
    // symlink is in libvlc-dev, which normal users don't have. Result: every feature works except
    // playback (reported by Luna's Kubuntu tester). Map the names to the versioned sonames too.
    private static void UseSystemLibVlcOnLinux()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LibVLCSharp.Shared.Core).Assembly, ResolveLibVlc);
        }
        catch (InvalidOperationException)
        {
            // A resolver is already registered for LibVLCSharp - leave it alone; the packages also
            // drop a libvlc.so symlink next to the app as a fallback.
        }
    }

    // Thread-safe "already reported" set (P/Invokes can resolve from several threads).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ReportedLibraries = new();

    private static IntPtr ResolveLibVlc(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string[] candidates = name switch
        {
            "libvlc" => ["libvlc.so", "libvlc.so.5"],
            "libvlccore" => ["libvlccore.so", "libvlccore.so.9"],
            _ => []
        };

        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                // The resolver runs once per P/Invoke entry point, so report each library once.
                if (Environment.GetEnvironmentVariable("LUNADROME_LOG") == "1" && ReportedLibraries.TryAdd(name, 0))
                    Console.Error.WriteLine($"[LunaDrome] loaded {name} as {candidate}");
                return handle;
            }
        }

        if (candidates.Length > 0)
            Console.Error.WriteLine($"[LunaDrome] could not load {name} (tried {string.Join(", ", candidates)}) - install VLC (libvlc5 / vlc-plugin-base)");
        return IntPtr.Zero; // fall back to .NET's default probing
    }
}
