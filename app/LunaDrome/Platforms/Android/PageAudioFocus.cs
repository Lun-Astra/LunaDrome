using Android.Content;
using Android.Media;
using Android.OS;

namespace LunaDrome.Services;

/// <summary>
/// Audio focus for the app's own player (Uno's MediaPlayer never asks for it). Without focus
/// Android Auto doesn't switch the car to LunaDrome, so the phone played to no one; it's also
/// what makes a call or another music app pause us. Android ducks for navigation prompts by
/// itself. (AutoPlayer, used while the page isn't logged in, has its own.)
/// </summary>
internal sealed class PageAudioFocus : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
{
    private static PageAudioFocus? s_instance;

    private readonly AudioManager? _audio =
        (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService);
    private readonly Action<MediaCommand> _command;
    private AudioFocusRequestClass? _request;
    private bool _held, _resumeOnGain;

    private PageAudioFocus(Action<MediaCommand> command) => _command = command;

    /// <summary>Called with the page's playing state (true / false = paused / null = stopped).</summary>
    public static void Update(bool? playing, Action<MediaCommand> command)
    {
        s_instance ??= new PageAudioFocus(command);
        if (playing == true)
            s_instance.Request();
        else if (playing == null)
            s_instance.Abandon();
        // Paused: keep the focus, so a call ending can resume us.
    }

    private void Request()
    {
        _resumeOnGain = false;
        if (_held || _audio == null)
            return;
        AudioFocusRequest result;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            _request ??= new AudioFocusRequestClass.Builder(AudioFocus.Gain)
                .SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!)!
                .SetOnAudioFocusChangeListener(this)!
                .Build();
            result = _audio.RequestAudioFocus(_request!);
        }
        else
        {
#pragma warning disable CA1422, CS0618 // the pre-Android 8 API
            result = _audio.RequestAudioFocus(this, Android.Media.Stream.Music, AudioFocus.Gain);
#pragma warning restore CA1422, CS0618
        }
        _held = result == AudioFocusRequest.Granted;
        AndroidLog.Write($"audio focus: {result}");
    }

    private void Abandon()
    {
        _resumeOnGain = false;
        if (!_held || _audio == null)
            return;
        _held = false;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            if (_request != null)
                _audio.AbandonAudioFocusRequest(_request);
        }
        else
        {
#pragma warning disable CA1422, CS0618
            _audio.AbandonAudioFocus(this);
#pragma warning restore CA1422, CS0618
        }
    }

    // Arrives on the main thread, like the session callbacks.
    public void OnAudioFocusChange(AudioFocus focusChange)
    {
        AndroidLog.Write($"app player: audio focus {focusChange}");
        switch (focusChange)
        {
            case AudioFocus.Gain:
                _held = true;
                if (_resumeOnGain)
                {
                    _resumeOnGain = false;
                    _command(MediaCommand.Play);
                }
                break;
            case AudioFocus.LossTransient:   // a call, a voice assistant
                _resumeOnGain = AndroidMediaControls.Current?.IsPlaying == true;
                _command(MediaCommand.Pause);
                break;
            case AudioFocus.Loss:            // another music app took over
                _held = false;
                _resumeOnGain = false;
                _command(MediaCommand.Pause);
                break;
        }
    }
}
