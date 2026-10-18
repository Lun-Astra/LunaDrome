namespace LunaDrome.Core.Sendspin.Decoding;

/// <summary>
/// Streaming MP3 decoder: finds MPEG audio frames in bytes as they arrive (skipping an ID3v2
/// tag and junk) and decodes each with NLayer's frame decoder, which keeps the Layer III bit
/// reservoir across frames. NLayer's own MpegFile needs a seekable stream; a network stream
/// isn't one.
/// </summary>
public sealed class Mp3Decoder : IAudioDecoder
{
    private static readonly int[,] BitRates =
    {
        // MPEG1 L1, L2, L3, then MPEG2/2.5 L1, L2&L3 (kbit/s, index 1..14)
        { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 },
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },
        { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
    };

    private readonly NLayer.MpegFrameDecoder _decoder = new();
    private readonly List<byte> _pending = new();
    private readonly float[] _floats = new float[1152 * 2];
    private bool _id3Checked;

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    public IEnumerable<short[]> Feed(ReadOnlySpan<byte> data)
    {
        _pending.AddRange(data.ToArray());
        var output = new List<short[]>();
        while (true)
        {
            var b = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending);
            if (!_id3Checked)
            {
                if (b.Length < 10) break;
                if (b[..3].SequenceEqual("ID3"u8))
                {
                    var size = (b[6] << 21) | (b[7] << 14) | (b[8] << 7) | b[9];
                    var total = 10 + size + ((b[5] & 0x10) != 0 ? 10 : 0);
                    if (b.Length < total) break;
                    _pending.RemoveRange(0, total);
                }
                _id3Checked = true;
                continue;
            }
            // Next sync word with a plausible header.
            var start = 0;
            Header header = default;
            while (start + 4 <= b.Length && !(b[start] == 0xFF && (b[start + 1] & 0xE0) == 0xE0 && TryParse(b[start..], out header)))
                start++;
            if (start + 4 > b.Length)
            {
                if (start > 0) _pending.RemoveRange(0, Math.Max(0, start - 3));
                break;
            }
            if (b.Length < start + header.Length + 4) { if (start > 0) _pending.RemoveRange(0, start); break; }
            // Guard against a false sync: the next frame must start right after this one.
            var next = b[(start + header.Length)..];
            if (!(next[0] == 0xFF && (next[1] & 0xE0) == 0xE0 && TryParse(next, out _)))
            {
                _pending.RemoveRange(0, start + 1);
                continue;
            }
            var frame = new Frame(b.Slice(start, header.Length).ToArray(), header);
            _pending.RemoveRange(0, start + header.Length);
            int count;
            try { count = _decoder.DecodeFrame(frame, _floats, 0); }
            catch (Exception) { continue; }   // a broken frame: skip it
            if (count <= 0) continue;
            SampleRate = header.SampleRate;
            Channels = header.Channels;
            var pcm = new short[count];
            for (var i = 0; i < count; i++)
                pcm[i] = (short)Math.Clamp(Math.Round(_floats[i] * 32767f), short.MinValue, short.MaxValue);
            output.Add(pcm);
        }
        return output;
    }

    /// <summary>Remaining frames once the stream has ended (the last frame has no successor).</summary>
    public IEnumerable<short[]> Flush()
    {
        _pending.AddRange(new byte[] { 0xFF, 0xFB, 0x90, 0x00 });   // a fake next header lets the last frame through
        var frames = Feed(ReadOnlySpan<byte>.Empty).ToList();
        _pending.Clear();
        return frames;
    }

    private readonly record struct Header(int Version, int Layer, int SampleRate, int Channels, int Length, int Samples, bool Crc,
        int ChannelMode, int ModeExtension, int BitRateIndex, int SampleRateIndex, bool Copyright, uint Raw);

    private static bool TryParse(ReadOnlySpan<byte> b, out Header header)
    {
        header = default;
        if (b.Length < 4) return false;
        var raw = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        var versionBits = (int)(raw >> 19) & 3;     // 0 = 2.5, 2 = 2, 3 = 1
        var layerBits = (int)(raw >> 17) & 3;       // 1 = III, 2 = II, 3 = I
        var bitrateIndex = (int)(raw >> 12) & 0xF;
        var rateIndex = (int)(raw >> 10) & 3;
        if (versionBits == 1 || layerBits == 0 || bitrateIndex is 0 or 15 || rateIndex == 3) return false;
        var layer = 4 - layerBits;
        var mpeg1 = versionBits == 3;
        int rate = rateIndex switch { 0 => 44100, 1 => 48000, _ => 32000 };
        if (versionBits == 2) rate /= 2; else if (versionBits == 0) rate /= 4;
        var table = mpeg1 ? layer - 1 : (layer == 1 ? 3 : 4);
        var bitrate = BitRates[table, bitrateIndex] * 1000;
        var padding = (int)(raw >> 9) & 1;
        var samples = layer == 1 ? 384 : layer == 3 && !mpeg1 ? 576 : 1152;
        var length = layer == 1 ? (12 * bitrate / rate + padding) * 4 : samples / 8 * bitrate / rate + padding;
        if (length < 21) return false;
        var mode = (int)(raw >> 6) & 3;
        header = new Header(versionBits, layer, rate, mode == 3 ? 1 : 2, length, samples, ((raw >> 16) & 1) == 0,
            mode, (int)(raw >> 4) & 3, bitrateIndex, rateIndex, ((raw >> 3) & 1) == 1, raw);
        return true;
    }

    /// <summary>One frame for NLayer: the header fields and a bit reader over the frame's bytes.</summary>
    private sealed class Frame(byte[] data, Header header) : NLayer.IMpegFrame
    {
        private int _readOffset = 4 + (header.Crc ? 2 : 0);
        private ulong _bucket;
        private int _bits;

        public int SampleRate => header.SampleRate;
        public int SampleRateIndex => header.SampleRateIndex;
        public int FrameLength => data.Length;
        public int BitRate => BitRates[header.Version == 3 ? header.Layer - 1 : (header.Layer == 1 ? 3 : 4), header.BitRateIndex] * 1000;
        public NLayer.MpegVersion Version => header.Version switch
        {
            3 => NLayer.MpegVersion.Version1,
            2 => NLayer.MpegVersion.Version2,
            _ => NLayer.MpegVersion.Version25,
        };
        public NLayer.MpegLayer Layer => (NLayer.MpegLayer)header.Layer;
        public NLayer.MpegChannelMode ChannelMode => (NLayer.MpegChannelMode)header.ChannelMode;
        public int ChannelModeExtension => header.ModeExtension;
        public int SampleCount => header.Samples;
        public int BitRateIndex => header.BitRateIndex;
        public bool IsCopyrighted => header.Copyright;
        public bool HasCrc => header.Crc;
        public bool IsCorrupted => false;

        public void Reset()
        {
            _readOffset = 4 + (header.Crc ? 2 : 0);
            _bucket = 0;
            _bits = 0;
        }

        public int ReadBits(int bitCount)
        {
            while (_bits < bitCount)
            {
                if (_readOffset >= data.Length) throw new EndOfStreamException();
                _bucket = (_bucket << 8) | data[_readOffset++];
                _bits += 8;
            }
            var value = (int)((_bucket >> (_bits - bitCount)) & ((1UL << bitCount) - 1));
            _bits -= bitCount;
            return value;
        }
    }
}
