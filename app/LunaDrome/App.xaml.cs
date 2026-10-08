using System;
using Microsoft.Extensions.Logging;
using Uno.Resizetizer;

namespace LunaDrome;

public partial class App : Application
{
    /// <summary>
    /// Initializes the singleton application object. This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        this.InitializeComponent();
    }

    protected Window? MainWindow { get; private set; }

    // Debug (the test build run from bin\Debug) looks different from the installed app, so it's
    // obvious in the taskbar whether the test build is the one that's open: orange moon icon
    // and "(test)" in the title.
#if DEBUG
    private const string WindowTitle = "LunaDrome (test)";
    private const string IconFile = "lunadrome-test.ico";
#else
    private const string WindowTitle = "LunaDrome";
    private const string IconFile = "lunadrome.ico";
#endif

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // No UseStudio() here: in Debug it overlays Uno's Hot Design toolbar on the top-left of the
        // window, right over the sidebar logo, which made debug builds look unlike the live app.
        MainWindow = new Window { Title = WindowTitle };

        // Same default size as the WPF app's MainWindow (1200x720).
        try { MainWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 1200, Height = 720 }); }
        catch { /* not supported on every Uno host */ }


        // Do not repeat app initialization when the Window already has content,
        // just ensure that the window is active
        if (MainWindow.Content is not Frame rootFrame)
        {
            // Create a Frame to act as the navigation context and navigate to the first page
            rootFrame = new Frame();

            // Place the frame in the current Window
            MainWindow.Content = rootFrame;

            rootFrame.NavigationFailed += OnNavigationFailed;

            // Remember the queue + position (#12) and preferences for next launch.
            MainWindow.Closed += (_, _) =>
            {
                var page = rootFrame.Content as MainPage;
                page?.SaveQueueState();
                // Playing the jam? Hand it back so it pauses now, not after the server's timeout.
                page?.ReleaseJamOnExit();
                // Delete the prebuffered tracks.
                page?.ClearCacheOnExit();
            };
        }

        if (rootFrame.Content == null)
        {
            // When the navigation stack isn't restored navigate to the first page,
            // configuring the new page by passing required information as a navigation
            // parameter
            rootFrame.Navigate(typeof(MainPage), args.Arguments);
        }

        // LunaDrome's moon icon for the window/taskbar (instead of the template's Uno logo).
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", IconFile);
        try
        {
            if (System.IO.File.Exists(icon))
                MainWindow.AppWindow.SetIcon(icon);
            else
                MainWindow.SetWindowIcon();
        }
        catch
        {
            MainWindow.SetWindowIcon();
        }

        // Ensure the current window is active
        MainWindow.Activate();

        // Launching LunaDrome again while it runs brings this window up instead (#14).
        var window = MainWindow;
        LunaDrome.Services.SingleInstance.ActivationRequested += () => window.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
                    { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
                    presenter.Restore();
            }
            catch { /* not supported on every Uno host */ }
            window.Activate();
            if (OperatingSystem.IsWindows())
                LunaDrome.Services.NativeWindow.BringToFront();
        });
    }

    /// <summary>
    /// Invoked when Navigation to a certain page fails
    /// </summary>
    /// <param name="sender">The Frame which failed navigation</param>
    /// <param name="e">Details about the navigation failure</param>
    void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        throw new InvalidOperationException($"Failed to load {e.SourcePageType.FullName}: {e.Exception}");
    }

    /// <summary>
    /// Configures global Uno Platform logging
    /// </summary>
    public static void InitializeLogging()
    {
#if !DEBUG
        // Release builds log only on request: LUNADROME_LOG=1 lunadrome (troubleshooting, e.g.
        // Linux playback). Media player / native library messages then go to the terminal.
        if (Environment.GetEnvironmentVariable("LUNADROME_LOG") != "1")
            return;
#endif
        // Logging is disabled by default for release builds, as it incurs a significant
        // initialization cost from Microsoft.Extensions.Logging setup. If startup performance
        // is a concern for your application, keep this disabled. If you're running on the web or
        // desktop targets, you can use URL or command line parameters to enable it.
        //
        // For more performance documentation: https://platform.uno/docs/articles/Uno-UI-Performance.html

        var factory = LoggerFactory.Create(builder =>
        {
#if __WASM__
            builder.AddProvider(new global::Uno.Extensions.Logging.WebAssembly.WebAssemblyConsoleLoggerProvider());
#elif __IOS__
            builder.AddProvider(new global::Uno.Extensions.Logging.OSLogLoggerProvider());

            // Log to the Visual Studio Debug console
            builder.AddConsole();
#else
            builder.AddConsole();
#endif

            // Exclude logs below this level
            builder.SetMinimumLevel(LogLevel.Information);

            // Playback diagnostics (Linux libvlc issues): media player internals in detail.
            builder.AddFilter("Uno.UI.MediaPlayer", LogLevel.Debug);
            builder.AddFilter("Windows.Media", LogLevel.Debug);
            builder.AddFilter("LibVLCSharp", LogLevel.Debug);
            builder.AddFilter("LunaDrome", LogLevel.Debug);

            // Default filters for Uno Platform namespaces
            builder.AddFilter("Uno", LogLevel.Warning);
            builder.AddFilter("Windows", LogLevel.Warning);
            builder.AddFilter("Microsoft", LogLevel.Warning);

            // Harmless on every machine where Windows' "Make text bigger" was never changed: the
            // TextScaleFactor registry value doesn't exist, Uno warns and falls back to 1.0.
            builder.AddFilter("Uno.UI.Runtime.Skia.Win32.Win32TextScaleFactorExtension", LogLevel.Error);

            // Generic Xaml events
            // builder.AddFilter("Microsoft.UI.Xaml", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.VisualStateGroup", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.StateTriggerBase", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.UIElement", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.FrameworkElement", LogLevel.Trace );

            // Layouter specific messages
            // builder.AddFilter("Microsoft.UI.Xaml.Controls", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Controls.Layouter", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Controls.Panel", LogLevel.Debug );

            // builder.AddFilter("Windows.Storage", LogLevel.Debug );

            // Binding related messages
            // builder.AddFilter("Microsoft.UI.Xaml.Data", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Data", LogLevel.Debug );

            // Binder memory references tracking
            // builder.AddFilter("Uno.UI.DataBinding.BinderReferenceHolder", LogLevel.Debug );

            // DevServer and HotReload related
            // builder.AddFilter("Uno.UI.RemoteControl", LogLevel.Information);

            // Debug JS interop
            // builder.AddFilter("Uno.Foundation.WebAssemblyRuntime", LogLevel.Debug );
        });

        global::Uno.Extensions.LogExtensionPoint.AmbientLoggerFactory = factory;

#if HAS_UNO
        global::Uno.UI.Adapter.Microsoft.Extensions.Logging.LoggingAdapter.Initialize();
#endif
    }
}
