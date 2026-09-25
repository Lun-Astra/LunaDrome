using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LunaDrome.Core.Models;

namespace LunaDrome.Services;

/// <summary>
/// Windows' SystemMediaTransportControls - the media flyout (volume/lock screen overlay with
/// title, art and play/pause/skip), which also delivers the keyboard's media keys (#3, #13).
///
/// The app is a plain net10.0 Uno/Skia desktop build without the Windows SDK projection, so this
/// talks to the WinRT COM interfaces directly. Vtable slots and IIDs were checked against
/// System32\WinMetadata\Windows.Media.winmd; every interface here derives from IInspectable
/// (slots 0-5), so its own methods start at slot 6.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class SmtcMediaControls : IMediaControls
{
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid IID_IAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    private static readonly Guid IID_ISystemMediaTransportControlsInterop = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");
    private static readonly Guid IID_ISystemMediaTransportControls = new("99fa3ff4-1742-42a6-902e-087d41f965ec");
    private static readonly Guid IID_IRandomAccessStreamReferenceStatics = new("857309dc-3fbf-4e7d-986f-ef3b1a07a964");
    private static readonly Guid IID_IUriRuntimeClassFactory = new("44a9796f-723e-4fdf-a218-033e75b0c084");
    // TypedEventHandler<SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs>:
    // the WinRT parameterized-interface IID (SHA-1 of its type signature), computed offline.
    private static readonly Guid IID_ButtonPressedHandler = new("0557e996-7b23-5bae-aa81-ea0d671143a4");

    // ISystemMediaTransportControls
    private const int Smtc_put_PlaybackStatus = 7, Smtc_get_DisplayUpdater = 8, Smtc_put_IsEnabled = 11,
        Smtc_put_IsPlayEnabled = 13, Smtc_put_IsStopEnabled = 15, Smtc_put_IsPauseEnabled = 17,
        Smtc_put_IsPreviousEnabled = 25, Smtc_put_IsNextEnabled = 27, Smtc_add_ButtonPressed = 32, Smtc_remove_ButtonPressed = 33;
    // ISystemMediaTransportControlsDisplayUpdater
    private const int Du_put_Type = 7, Du_put_Thumbnail = 11, Du_get_MusicProperties = 12, Du_ClearAll = 16, Du_Update = 17;
    // IMusicDisplayProperties
    private const int Music_put_Title = 7, Music_put_AlbumArtist = 9, Music_put_Artist = 11;

    // Windows.Media enums
    private const int PlaybackStatusClosed = 0, PlaybackStatusStopped = 2, PlaybackStatusPlaying = 3, PlaybackStatusPaused = 4;
    private const int MediaPlaybackTypeMusic = 1;

    private static SmtcMediaControls? s_instance;
    private static IntPtr s_handler;

    private IntPtr _smtc;
    private IntPtr _updater;
    private long _buttonToken;

    public event Action<MediaCommand>? CommandReceived;

    public static SmtcMediaControls? TryCreate()
    {
        var hwnd = NativeWindow.FindAppWindow();
        if (hwnd == IntPtr.Zero)
            return null;

        var interop = GetActivationFactory("Windows.Media.SystemMediaTransportControls", IID_ISystemMediaTransportControlsInterop);
        try
        {
            IntPtr smtc;
            var iid = IID_ISystemMediaTransportControls;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Vtbl(interop)[6])(interop, hwnd, &iid, &smtc), "GetForWindow");
            var controls = new SmtcMediaControls { _smtc = smtc };
            controls.Initialize();
            return controls;
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    private void Initialize()
    {
        PutBool(_smtc, Smtc_put_IsEnabled, true);
        PutBool(_smtc, Smtc_put_IsPlayEnabled, true);
        PutBool(_smtc, Smtc_put_IsPauseEnabled, true);
        PutBool(_smtc, Smtc_put_IsStopEnabled, true);
        PutBool(_smtc, Smtc_put_IsPreviousEnabled, true);
        PutBool(_smtc, Smtc_put_IsNextEnabled, true);
        PutInt(_smtc, Smtc_put_PlaybackStatus, PlaybackStatusClosed);

        IntPtr updater;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(_smtc)[Smtc_get_DisplayUpdater])(_smtc, &updater), "get_DisplayUpdater");
        _updater = updater;

        s_instance = this;
        if (s_handler == IntPtr.Zero)
            s_handler = CreateButtonPressedHandler();
        long token;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Vtbl(_smtc)[Smtc_add_ButtonPressed])(_smtc, s_handler, &token), "add_ButtonPressed");
        _buttonToken = token;
    }

    public void SetTrack(Song? song, string? artUrl = null)
    {
        if (_updater == IntPtr.Zero)
            return;

        try
        {
            if (song == null)
            {
                Call(_updater, Du_ClearAll);
                Call(_updater, Du_Update);
                PutInt(_smtc, Smtc_put_PlaybackStatus, PlaybackStatusClosed);
                return;
            }

            PutInt(_updater, Du_put_Type, MediaPlaybackTypeMusic);

            IntPtr music;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(_updater)[Du_get_MusicProperties])(_updater, &music), "get_MusicProperties");
            try
            {
                PutString(music, Music_put_Title, song.Title);
                PutString(music, Music_put_Artist, song.ArtistName);
                PutString(music, Music_put_AlbumArtist, string.IsNullOrEmpty(song.AlbumArtistName) ? song.ArtistName : song.AlbumArtistName);
            }
            finally
            {
                Marshal.Release(music);
            }

            SetThumbnail(artUrl);
            Call(_updater, Du_Update);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LunaDrome] media flyout update failed: {ex.Message}");
        }
    }

    // RandomAccessStreamReference.CreateFromUri(new Uri(artUrl)): Windows fetches the art itself.
    private void SetThumbnail(string? artUrl)
    {
        if (string.IsNullOrEmpty(artUrl))
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vtbl(_updater)[Du_put_Thumbnail])(_updater, IntPtr.Zero);
            return;
        }

        var uriFactory = GetActivationFactory("Windows.Foundation.Uri", IID_IUriRuntimeClassFactory);
        var statics = GetActivationFactory("Windows.Storage.Streams.RandomAccessStreamReference", IID_IRandomAccessStreamReferenceStatics);
        IntPtr uri = IntPtr.Zero, reference = IntPtr.Zero;
        var hstring = CreateHString(artUrl);
        try
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Vtbl(uriFactory)[6])(uriFactory, hstring, &uri), "CreateUri");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Vtbl(statics)[7])(statics, uri, &reference), "CreateFromUri");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vtbl(_updater)[Du_put_Thumbnail])(_updater, reference), "put_Thumbnail");
        }
        finally
        {
            WindowsDeleteString(hstring);
            if (reference != IntPtr.Zero) Marshal.Release(reference);
            if (uri != IntPtr.Zero) Marshal.Release(uri);
            Marshal.Release(statics);
            Marshal.Release(uriFactory);
        }
    }

    public void SetPlaybackState(bool? playing)
    {
        if (_smtc == IntPtr.Zero)
            return;
        try
        {
            PutInt(_smtc, Smtc_put_PlaybackStatus, playing switch
            {
                true => PlaybackStatusPlaying,
                false => PlaybackStatusPaused,
                null => PlaybackStatusStopped,
            });
        }
        catch { /* best-effort */ }
    }

    public void SetCanGoPreviousNext(bool canGoPrevious, bool canGoNext)
    {
        if (_smtc == IntPtr.Zero)
            return;
        try
        {
            PutBool(_smtc, Smtc_put_IsPreviousEnabled, canGoPrevious);
            PutBool(_smtc, Smtc_put_IsNextEnabled, canGoNext);
        }
        catch { /* best-effort */ }
    }

    public void SetPosition(TimeSpan position, TimeSpan duration)
    {
        // The flyout works fine without a timeline; not worth ISystemMediaTransportControls2.
    }

    public void Dispose()
    {
        if (_smtc == IntPtr.Zero)
            return;
        try
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Vtbl(_smtc)[Smtc_remove_ButtonPressed])(_smtc, _buttonToken);
            PutInt(_smtc, Smtc_put_PlaybackStatus, PlaybackStatusClosed);
        }
        catch { /* shutting down */ }
        if (_updater != IntPtr.Zero) Marshal.Release(_updater);
        Marshal.Release(_smtc);
        _updater = _smtc = IntPtr.Zero;
        if (s_instance == this)
            s_instance = null;
    }

    // ----- ButtonPressed handler: a minimal hand-built COM object (IUnknown + Invoke) -----
    // It's created once and never freed, so AddRef/Release don't need to count. Declaring
    // IAgileObject lets Windows call it straight from its own thread instead of marshaling.

    private static IntPtr CreateButtonPressedHandler()
    {
        var vtbl = (void**)NativeMemory.Alloc((nuint)(4 * sizeof(void*)));
        vtbl[0] = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&HandlerQueryInterface;
        vtbl[1] = (delegate* unmanaged[Stdcall]<IntPtr, uint>)&HandlerAddRefRelease;
        vtbl[2] = (delegate* unmanaged[Stdcall]<IntPtr, uint>)&HandlerAddRefRelease;
        vtbl[3] = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&HandlerInvoke;
        var obj = (void**)NativeMemory.Alloc((nuint)sizeof(void*));
        obj[0] = vtbl;
        return (IntPtr)obj;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int HandlerQueryInterface(IntPtr self, Guid* iid, IntPtr* result)
    {
        if (*iid == IID_IUnknown || *iid == IID_ButtonPressedHandler || *iid == IID_IAgileObject)
        {
            *result = self;
            return 0;
        }
        *result = IntPtr.Zero;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint HandlerAddRefRelease(IntPtr self) => 1;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int HandlerInvoke(IntPtr self, IntPtr sender, IntPtr args)
    {
        try
        {
            // ISystemMediaTransportControlsButtonPressedEventArgs.get_Button (slot 6)
            int button;
            if (((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(args)[6])(args, &button) < 0)
                return 0;

            MediaCommand? command = button switch
            {
                0 => MediaCommand.Play,
                1 => MediaCommand.Pause,
                2 => MediaCommand.Stop,
                6 => MediaCommand.Next,
                7 => MediaCommand.Previous,
                _ => null,
            };
            if (command is { } c && s_instance is { } instance)
                instance.CommandReceived?.Invoke(c);
        }
        catch { /* never let an exception cross into Windows */ }
        return 0;
    }

    // ----- WinRT plumbing -----

    private static void** Vtbl(IntPtr obj) => *(void***)obj;

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new COMException($"SMTC {what} failed", hr);
    }

    private static void Call(IntPtr obj, int slot) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(obj)[slot])(obj), $"slot {slot}");

    private static void PutBool(IntPtr obj, int slot, bool value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Vtbl(obj)[slot])(obj, value ? (byte)1 : (byte)0), $"slot {slot}");

    private static void PutInt(IntPtr obj, int slot, int value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Vtbl(obj)[slot])(obj, value), $"slot {slot}");

    private static void PutString(IntPtr obj, int slot, string value)
    {
        var hstring = CreateHString(value);
        try
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vtbl(obj)[slot])(obj, hstring), $"slot {slot}");
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static IntPtr GetActivationFactory(string className, Guid iid)
    {
        var hstring = CreateHString(className);
        try
        {
            Check(RoGetActivationFactory(hstring, ref iid, out var factory), $"activation of {className}");
            return factory;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static IntPtr CreateHString(string value)
    {
        Check(WindowsCreateString(value, (uint)value.Length, out var hstring), "WindowsCreateString");
        return hstring;
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}
