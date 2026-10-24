using System.Net;
using System.Net.Http.Headers;

namespace LunaDrome.Core.Sendspin.Decoding;

/// <summary>
/// Plays one song into a <see cref="SendspinSource"/> by decoding it itself - for phones, whose
/// platform player won't hand over its audio. Reads the same address the player opened (a
/// Navidrome stream URL or a prefetched file): FLAC is decoded here and seeks with an HTTP
/// Range request to the exact sample; a raw MP3 is decoded with NLayer (seeking = decoding up to
/// the position); anything else is fetched transcoded to MP3 by Navidrome from the position on.
/// Output is resampled to 48 kHz stereo; the source's backpressure paces it to real time.
/// </summary>
public sealed class SendspinTrackPlayer : IAsyncDisposable
{
    private readonly SendspinSource _source;
    private readonly HttpClient _http;
    private readonly ManualResetEventSlim _running = new(true);
    private CancellationTokenSource? _cts;
    private Task? _task;
    private long _framesOut;   // 48 kHz frames written for the current song

    public SendspinTrackPlayer(SendspinSource source, HttpClient http)
    {
        _source = source;
        _http = http;
    }

    public Action<string>? Log { get; set; }
    public Uri? Uri { get; private set; }
    public TimeSpan StartPosition { get; private set; }
    public bool Paused => !_running.IsSet;
    /// <summary>Where decoding has got to (ahead of what's heard by the buffers' worth).</summary>
    public TimeSpan DecodedPosition => StartPosition + TimeSpan.FromSeconds(Interlocked.Read(ref _framesOut) / (double)SendspinOutput.SampleRate);
    /// <summary>The song played to its end.</summary>
    public bool Ended { get; private set; }

    /// <summary>Starts (or restarts) playback of <paramref name="uri"/> at <paramref name="start"/>;
    /// whatever was buffered is dropped.</summary>
    public void Play(Uri uri, TimeSpan start, bool paused = false)
    {
        StopDecoding();
        _source.Flush();
        Uri = uri;
        StartPosition = start < TimeSpan.Zero ? TimeSpan.Zero : start;
        Interlocked.Exchange(ref _framesOut, 0);
        Ended = false;
        if (paused) _running.Reset(); else _running.Set();
        var cts = _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(uri, StartPosition, cts.Token));
    }

    public void Pause()
    {
        _running.Reset();
        _source.Pause();
    }

    public void Resume()
    {
        _source.Resume();
        _running.Set();
    }

    public void Stop()
    {
        StopDecoding();
        _source.Stop();
        Uri = null;
    }

    private void StopDecoding()
    {
        _cts?.Cancel();
        _running.Set();   // wake a paused loop so it can see the cancel
        _cts = null;
    }

    private async Task RunAsync(Uri uri, TimeSpan start, CancellationToken token)
    {
        try
        {
            if (uri.IsFile)
                await PlayFileAsync(uri.LocalPath, start, token).ConfigureAwait(false);
            else
                await PlayHttpAsync(uri, start, token).ConfigureAwait(false);
            if (!token.IsCancellationRequested)
            {
                Ended = true;
                _source.Drain();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log?.Invoke($"sendspin: decoding {uri.AbsolutePath} failed: {ex.Message}");
            if (!token.IsCancellationRequested) _source.Drain();
        }
    }

    // ---- sources ----

    private async Task PlayFileAsync(string path, TimeSpan start, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        var head = new byte[4];
        var read = await file.ReadAtLeastAsync(head, 4, throwOnEndOfStream: false, token).ConfigureAwait(false);
        file.Position = 0;
        if (FlacDecoder.Sniff(head.AsSpan(0, read)))
        {
            await DecodeFlacAsync(file, start, token, seek: async offset =>
            {
                file.Position = offset;
                return file;
            }).ConfigureAwait(false);
        }
        else
        {
            DecodeMp3(file, start, token);
        }
    }

    private async Task PlayHttpAsync(Uri uri, TimeSpan start, CancellationToken token)
    {
        // Formats this can't decode (opus, aac, ...) are asked for as MP3 instead.
        var playUri = uri;
        if (NeedsTranscode(uri)) playUri = TranscodedUri(uri, TimeSpan.Zero);
        var response = await GetAsync(playUri, null, token).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var head = new byte[10];
        var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
        var prefixed = new PrefixedStream(head.AsMemory(0, read), stream);
        if (FlacDecoder.Sniff(head.AsSpan(0, read)))
        {
            var length = response.Content.Headers.ContentLength;
            await DecodeFlacAsync(prefixed, start, token, seek: async offset =>
            {
                response.Dispose();
                response = await GetAsync(playUri, offset, token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.PartialContent) return null;   // no Range support
                return await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            }, length).ConfigureAwait(false);
        }
        else if (LooksLikeMp3(head.AsSpan(0, read)))
        {
            DecodeMp3(prefixed, start, token);
        }
        else
        {
            // Something else (opus, m4a, ...): let Navidrome transcode, from the position on.
            response.Dispose();
            Log?.Invoke("sendspin: not FLAC or MP3 - asking Navidrome for MP3");
            using var transcoded = await GetAsync(TranscodedUri(uri, start), null, token).ConfigureAwait(false);
            DecodeMp3(await transcoded.Content.ReadAsStreamAsync(token).ConfigureAwait(false), TimeSpan.Zero, token);
        }
        response.Dispose();
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, long? from, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (from is { } offset) request.Headers.Range = new RangeHeaderValue(offset, null);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static bool NeedsTranscode(Uri uri)
    {
        var format = QueryValue(uri, "format");
        return format is not (null or "" or "raw" or "flac" or "mp3");
    }

    /// <summary>The same Subsonic stream URL asking for 320k MP3 from <paramref name="offset"/>.</summary>
    private static Uri TranscodedUri(Uri uri, TimeSpan offset)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        query["format"] = "mp3";
        query["maxBitRate"] = "320";
        if (offset > TimeSpan.Zero) query["timeOffset"] = ((int)offset.TotalSeconds).ToString();
        return new UriBuilder(uri) { Query = query.ToString() }.Uri;
    }

    private static string? QueryValue(Uri uri, string name) => System.Web.HttpUtility.ParseQueryString(uri.Query)[name];

    private static bool LooksLikeMp3(ReadOnlySpan<byte> head) =>
        (head.Length >= 3 && head[..3].SequenceEqual("ID3"u8)) || (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0);

    // ---- decoders ----

    private async Task DecodeFlacAsync(Stream stream, TimeSpan start, CancellationToken token,
        Func<long, Task<Stream?>> seek, long? length = null)
    {
        var decoder = new FlacDecoder();
        var buffer = new byte[32 * 1024];
        Resampler? resampler = null;

        // Read the header (cover art can follow STREAMINFO); the first frames may come along.
        var pending = new List<DecodedFrame>();
        while (!decoder.HeaderDone)
        {
            var n = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (n == 0) return;
            pending.AddRange(decoder.FeedFrames(buffer.AsSpan(0, n)));
        }
        var target = (long)(start.TotalSeconds * decoder.SampleRate);
        if (target > 0)
        {
            var total = length ?? (stream.CanSeek ? stream.Length : 0);
            var seconds = decoder.TotalSamples / (double)decoder.SampleRate;
            var bytesPerSecond = total > decoder.AudioOffset && seconds > 0 ? (total - decoder.AudioOffset) / seconds : 0;
            // Jump close to the target (a little early), check where it landed, back off if late.
            var margin = 2.0;
            for (var attempt = 0; attempt < 5 && bytesPerSecond > 0; attempt++)
            {
                var guess = decoder.AudioOffset + (long)((start.TotalSeconds - margin) * bytesPerSecond);
                if (guess <= decoder.AudioOffset) break;          // near the start: just decode from there
                var seeked = await seek(guess).ConfigureAwait(false);
                if (seeked == null) break;                         // no Range support: decode forward
                stream = seeked;
                decoder.RestartMidStream();
                pending.Clear();
                while (pending.Count == 0)
                {
                    var n = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (n == 0) break;
                    pending.AddRange(decoder.FeedFrames(buffer.AsSpan(0, n)));
                }
                if (pending.Count == 0 || pending[0].Sample <= target) break;
                margin += (pending[0].Sample - target) / (double)decoder.SampleRate + 2;   // landed late
            }
        }

        void Emit(DecodedFrame frame)
        {
            var channels = decoder.Channels;
            var span = frame.Samples.AsSpan();
            var count = span.Length / channels;
            if (frame.Sample + count <= target) return;            // entirely before the target
            if (frame.Sample < target) span = span[(int)((target - frame.Sample) * channels)..];
            Write(span, channels, decoder.SampleRate, ref resampler, token);
        }

        foreach (var frame in pending) Emit(frame);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            _running.Wait(token);
            var n = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (n == 0) break;
            foreach (var frame in decoder.FeedFrames(buffer.AsSpan(0, n))) Emit(frame);
        }
    }

    private void DecodeMp3(Stream stream, TimeSpan start, CancellationToken token)
    {
        var decoder = new Mp3Decoder();
        Resampler? resampler = null;
        long skip = -1;   // samples (all channels) still to drop; -1 = not known until the rate is
        var buffer = new byte[16 * 1024];
        void Emit(short[] frame)
        {
            if (skip < 0) skip = (long)(start.TotalSeconds * decoder.SampleRate) * decoder.Channels;
            var span = frame.AsSpan();
            if (skip > 0)
            {
                var drop = (int)Math.Min(skip, span.Length);
                skip -= drop;
                span = span[drop..];
                if (span.IsEmpty) return;
            }
            Write(span, decoder.Channels, decoder.SampleRate, ref resampler, token);
        }
        while (true)
        {
            token.ThrowIfCancellationRequested();
            _running.Wait(token);
            var n = stream.Read(buffer, 0, buffer.Length);
            if (n == 0) break;
            foreach (var frame in decoder.Feed(buffer.AsSpan(0, n))) Emit(frame);
        }
        foreach (var frame in decoder.Flush()) Emit(frame);
    }

    /// <summary>To 48 kHz stereo, then into the source (which blocks while it holds enough).</summary>
    private void Write(ReadOnlySpan<short> samples, int channels, int rate, ref Resampler? resampler, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        short[] stereo;
        if (channels == 2) stereo = samples.ToArray();
        else
        {
            var frames = samples.Length / channels;
            stereo = new short[frames * 2];
            for (var i = 0; i < frames; i++)
            {
                stereo[2 * i] = samples[i * channels];
                stereo[2 * i + 1] = samples[i * channels + (channels > 1 ? 1 : 0)];
            }
        }
        resampler ??= new Resampler(rate, SendspinOutput.SampleRate, 2);
        var output = resampler.Process(stereo);
        if (output.Length == 0) return;
        _source.Write(output);
        Interlocked.Add(ref _framesOut, output.Length / 2);
    }

    public async ValueTask DisposeAsync()
    {
        var task = _task;
        StopDecoding();
        if (task != null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        _running.Dispose();
    }

    /// <summary>Puts already-read bytes back in front of a stream (after sniffing the format).</summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_prefix.Length > 0)
            {
                var n = Math.Min(buffer.Length, _prefix.Length);
                _prefix.Span[..n].CopyTo(buffer);
                _prefix = _prefix[n..];
                return n;
            }
            return inner.Read(buffer);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefix.Length > 0) return Read(buffer.Span);
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
