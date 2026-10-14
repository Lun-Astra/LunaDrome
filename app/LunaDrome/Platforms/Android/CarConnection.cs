using Android.Content;
using Android.OS;

namespace LunaDrome.Services;

/// <summary>
/// Whether the phone is connected to Android Auto, the way androidx.car.app's CarConnection
/// does it (without the library): Android Auto publishes the state through a content provider
/// and announces changes with a broadcast. Used to pause when the car is switched off or the
/// phone is unplugged - music shouldn't carry on from the phone's speaker.
/// </summary>
internal static class CarConnection
{
    private const string ProviderUri = "content://androidx.car.app.connection";
    private const string StateColumn = "CarConnectionState";
    private const string UpdatedAction = "androidx.car.app.connection.action.CAR_CONNECTION_UPDATED";

    private static bool s_started, s_connected;
    private static Action? s_disconnected;

    /// <summary>Starts watching (once per process); <paramref name="disconnected"/> runs on the
    /// main thread when an Android Auto connection ends.</summary>
    public static void Watch(Action disconnected)
    {
        s_disconnected = disconnected;
        if (s_started)
            return;
        s_started = true;
        var context = Android.App.Application.Context;
        var filter = new IntentFilter(UpdatedAction);
        // Sent by the Android Auto app, so the receiver has to accept other apps' broadcasts.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            context.RegisterReceiver(new Receiver(), filter, ReceiverFlags.Exported);
        else
            context.RegisterReceiver(new Receiver(), filter);
        s_connected = Query();
        AndroidLog.Write($"Android Auto: {(s_connected ? "connected" : "not connected")}");
    }

    private static bool Query()
    {
        try
        {
            using var cursor = Android.App.Application.Context.ContentResolver?.Query(
                Android.Net.Uri.Parse(ProviderUri), new[] { StateColumn }, null, null, null);
            if (cursor == null || !cursor.MoveToNext())
                return false;
            return cursor.GetInt(cursor.GetColumnIndex(StateColumn)) != 0;   // 1 = car OS, 2 = projection
        }
        catch
        {
            return false;   // no Android Auto installed
        }
    }

    private sealed class Receiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            var connected = Query();
            if (connected == s_connected)
                return;
            s_connected = connected;
            AndroidLog.Write($"Android Auto: {(connected ? "connected" : "disconnected")}");
            if (!connected)
                s_disconnected?.Invoke();
        }
    }
}
