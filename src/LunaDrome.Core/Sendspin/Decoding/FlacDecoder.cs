namespace LunaDrome.Core.Sendspin.Decoding;

/// <summary>
/// A streaming FLAC decoder (all of the format real files use: CONSTANT / VERBATIM / FIXED / LPC
/// subframes, wasted bits, Rice and Rice2 residuals with escapes, independent / left-side /
/// right-side / mid-side stereo, 4-32 bit samples, any block size). Feed it bytes as they
/// arrive; it hands back whole frames. Used to stream songs from Navidrome to Sendspin speakers
/// on phones, whose platform player gives no access to its decoded audio.
/// </summary>
public sealed class FlacDecoder : IAudioDecoder
{
    private readonly List<byte> _pending = new();
    private bool _headerDone;
    private int[][] _channels = [];
    private int[] _residualScratch = [];

    /// <summary>All metadata blocks read (cover art can follow STREAMINFO); frames come next.</summary>
    public bool HeaderDone => _headerDone;
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int BitsPerSample { get; private set; }
    /// <summary>Total samples per channel from STREAMINFO (0 = unknown).</summary>
    public long TotalSamples { get; private set; }
    public int MaxBlockSize { get; private set; }
    /// <summary>Sample number (per channel) of the first sample of the last decoded frame.</summary>
    public long LastFrameSample { get; private set; } = -1;

    /// <summary>True when the stream starts with "fLaC".</summary>
    public static bool Sniff(ReadOnlySpan<byte> start) => start.Length >= 4 && start[..4].SequenceEqual("fLaC"u8);

    /// <summary>Feeds bytes; returns decoded frames as interleaved samples scaled to 16 bits.</summary>
    public IEnumerable<short[]> Feed(ReadOnlySpan<byte> data) => FeedFrames(data).Select(f => f.Samples);

    /// <summary>Like <see cref="Feed"/>, with each frame's first sample number (per channel).</summary>
    public List<DecodedFrame> FeedFrames(ReadOnlySpan<byte> data)
    {
        _pending.AddRange(data.ToArray());
        var frames = new List<DecodedFrame>();
        while (true)
        {
            var buffer = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending);
            if (!_headerDone)
            {
                var used = ParseHeader(buffer);
                if (used == 0) break;
                _pending.RemoveRange(0, used);
                continue;
            }
            var (consumed, frame) = TryDecodeFrame(buffer);
            if (consumed == 0) break;
            _pending.RemoveRange(0, consumed);
            if (frame != null) frames.Add(new DecodedFrame(LastFrameSample, frame));
        }
        return frames;
    }

    /// <summary>After a seek into the middle of the file (a Range request): the next bytes are
    /// frame data, not the header; the decoder resyncs on the next valid frame.</summary>
    public void RestartMidStream()
    {
        _pending.Clear();
        if (!_headerDone) throw new InvalidOperationException("Read the header first.");
    }

    /// <summary>Byte offset where audio frames start (known once the header is parsed).</summary>
    public long AudioOffset { get; private set; }

    // ---- metadata ----

    private int _headerPos;   // bytes of header consumed so far (for AudioOffset)

    private int ParseHeader(Span<byte> b)
    {
        // "fLaC" then metadata blocks; STREAMINFO is the first.
        var pos = 0;
        if (_headerPos == 0)
        {
            if (b.Length < 4) return 0;
            if (!Sniff(b)) throw new InvalidDataException("Not a FLAC stream.");
            pos = 4;
        }
        while (true)
        {
            if (b.Length < pos + 4) { Commit(pos); return pos; }
            var last = (b[pos] & 0x80) != 0;
            var type = b[pos] & 0x7F;
            var length = (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3];
            if (b.Length < pos + 4 + length) { Commit(pos); return pos; }
            if (type == 0)
            {
                var info = b.Slice(pos + 4, length);
                MaxBlockSize = (info[2] << 8) | info[3];
                SampleRate = (info[10] << 12) | (info[11] << 4) | (info[12] >> 4);
                Channels = ((info[12] >> 1) & 0x07) + 1;
                BitsPerSample = (((info[12] & 1) << 4) | (info[13] >> 4)) + 1;
                TotalSamples = ((long)(info[13] & 0x0F) << 32) | ((long)info[14] << 24) | ((long)info[15] << 16) | ((long)info[16] << 8) | info[17];
            }
            pos += 4 + length;
            if (last)
            {
                _headerDone = true;
                Commit(pos);
                AudioOffset = _headerPos;
                if (SampleRate == 0 || Channels == 0) throw new InvalidDataException("FLAC stream without STREAMINFO.");
                return pos;
            }
        }

        void Commit(int consumed) => _headerPos += consumed;
    }

    // ---- frames ----

    /// <summary>Returns (bytes consumed, frame or null). 0 consumed = need more data.</summary>
    private (int, short[]?) TryDecodeFrame(Span<byte> b)
    {
        // Find a sync code; skip junk (after a seek we land mid-frame).
        var start = 0;
        while (true)
        {
            while (start + 1 < b.Length && !(b[start] == 0xFF && (b[start + 1] & 0xFE) == 0xF8)) start++;
            if (start + 16 > b.Length) return (start > 0 ? start : 0, null);
            var result = DecodeAt(b[start..], out var length);
            if (result == Result.NeedMore) return (start, null);
            if (result == Result.Ok) return (start + length, _lastFrame);
            start++;   // false sync: keep searching
        }
    }

    private enum Result { Ok, NeedMore, Invalid }
    private short[]? _lastFrame;

    private Result DecodeAt(Span<byte> b, out int length)
    {
        length = 0;
        try
        {
            var r = new BitReader(b);
            r.Read(15); // sync + reserved
            var variable = r.Read(1) == 1;
            var bsCode = (int)r.Read(4);
            var srCode = (int)r.Read(4);
            var chCode = (int)r.Read(4);
            var bpsCode = (int)r.Read(3);
            if (r.Read(1) != 0 || bsCode == 0 || srCode == 15 || chCode > 10 || bpsCode == 3) return Result.Invalid;
            var number = ReadUtf8(ref r);
            if (number < 0) return Result.Invalid;
            int blockSize = bsCode switch
            {
                1 => 192,
                >= 2 and <= 5 => 576 << (bsCode - 2),
                6 => (int)r.Read(8) + 1,
                7 => (int)r.Read(16) + 1,
                _ => 256 << (bsCode - 8),
            };
            if (srCode == 12) r.Read(8); else if (srCode is 13 or 14) r.Read(16);
            var headerBytes = r.BytePosition;
            var crc8 = (int)r.Read(8);
            if (Crc8(b[..headerBytes]) != crc8) return Result.Invalid;
            var bps = bpsCode switch { 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, 7 => 32, _ => BitsPerSample };
            var channels = chCode <= 7 ? chCode + 1 : 2;
            if (channels != Channels) return Result.Invalid;

            if (_channels.Length != channels || _channels[0].Length < blockSize)
                _channels = Enumerable.Range(0, channels).Select(_ => new int[Math.Max(blockSize, MaxBlockSize)]).ToArray();
            for (var ch = 0; ch < channels; ch++)
            {
                var extra = (chCode == 8 && ch == 1) || (chCode == 9 && ch == 0) || (chCode == 10 && ch == 1) ? 1 : 0;
                DecodeSubframe(ref r, _channels[ch], blockSize, bps + extra);
            }
            r.AlignToByte();
            var bodyEnd = r.BytePosition;
            var crc16 = (int)r.Read(16);
            if (Crc16(b[..bodyEnd]) != crc16) return Result.Invalid;
            length = r.BytePosition;

            // Stereo decorrelation.
            int[] c0 = _channels[0], c1 = channels > 1 ? _channels[1] : c0;
            switch (chCode)
            {
                case 8: for (var i = 0; i < blockSize; i++) c1[i] = c0[i] - c1[i]; break;            // left/side
                case 9: for (var i = 0; i < blockSize; i++) c0[i] += c1[i]; break;                   // side/right
                case 10:
                    for (var i = 0; i < blockSize; i++)
                    {
                        long mid = ((long)c0[i] << 1) | (c1[i] & 1);
                        long side = c1[i];
                        c0[i] = (int)((mid + side) >> 1);
                        c1[i] = (int)((mid - side) >> 1);
                    }
                    break;
            }
            LastFrameSample = variable ? number : number * (MaxBlockSize > 0 ? MaxBlockSize : blockSize);
            _lastFrame = To16Bit(blockSize, channels, bps);
            return Result.Ok;
        }
        catch (EndOfStreamException)
        {
            return Result.NeedMore;
        }
        catch (InvalidDataException)
        {
            return Result.Invalid;
        }
    }

    private short[] To16Bit(int blockSize, int channels, int bps)
    {
        var output = new short[blockSize * channels];
        var shift = bps - 16;
        for (var ch = 0; ch < channels; ch++)
        {
            var src = _channels[ch];
            for (var i = 0; i < blockSize; i++)
            {
                var v = shift >= 0 ? src[i] >> shift : src[i] << -shift;
                output[i * channels + ch] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            }
        }
        return output;
    }

    private static long ReadUtf8(ref BitReader r)
    {
        var first = (int)r.Read(8);
        if ((first & 0x80) == 0) return first;
        var count = 0;
        while (count < 7 && (first & (0x80 >> count)) != 0) count++;
        if (count is 1 or > 7) return -1;
        long value = count == 7 ? 0 : first & ((1 << (7 - count)) - 1);
        for (var i = 1; i < count; i++)
        {
            var next = (int)r.Read(8);
            if ((next & 0xC0) != 0x80) return -1;
            value = (value << 6) | (long)(next & 0x3F);
        }
        return value;
    }

    private void DecodeSubframe(ref BitReader r, int[] output, int blockSize, int bps)
    {
        if (r.Read(1) != 0) throw new InvalidDataException("Subframe padding bit set.");
        var type = (int)r.Read(6);
        var wasted = 0;
        if (r.Read(1) == 1)
        {
            wasted = 1;
            while (r.Read(1) == 0) wasted++;
            bps -= wasted;
        }
        if (type == 0)
        {
            var v = r.ReadSigned(bps);
            Array.Fill(output, v, 0, blockSize);
        }
        else if (type == 1)
        {
            for (var i = 0; i < blockSize; i++) output[i] = r.ReadSigned(bps);
        }
        else if (type is >= 8 and <= 12)
        {
            var order = type - 8;
            for (var i = 0; i < order; i++) output[i] = r.ReadSigned(bps);
            DecodeResidual(ref r, output, blockSize, order);
            for (var i = order; i < blockSize; i++)
            {
                output[i] += order switch
                {
                    0 => 0,
                    1 => output[i - 1],
                    2 => 2 * output[i - 1] - output[i - 2],
                    3 => 3 * output[i - 1] - 3 * output[i - 2] + output[i - 3],
                    _ => 4 * output[i - 1] - 6 * output[i - 2] + 4 * output[i - 3] - output[i - 4],
                };
            }
        }
        else if (type >= 32)
        {
            var order = type - 31;
            for (var i = 0; i < order; i++) output[i] = r.ReadSigned(bps);
            var precision = (int)r.Read(4) + 1;
            if (precision == 16) throw new InvalidDataException("Invalid LPC precision.");
            var shift = r.ReadSigned(5);
            var coefficients = new int[order];
            for (var i = 0; i < order; i++) coefficients[i] = r.ReadSigned(precision);
            DecodeResidual(ref r, output, blockSize, order);
            for (var i = order; i < blockSize; i++)
            {
                long sum = 0;
                for (var j = 0; j < order; j++) sum += (long)coefficients[j] * output[i - j - 1];
                output[i] += (int)(shift >= 0 ? sum >> shift : sum << -shift);
            }
        }
        else
        {
            throw new InvalidDataException($"Reserved subframe type {type}.");
        }
        if (wasted > 0)
            for (var i = 0; i < blockSize; i++) output[i] <<= wasted;
    }

    private static void DecodeResidual(ref BitReader r, int[] output, int blockSize, int order)
    {
        var method = (int)r.Read(2);
        if (method > 1) throw new InvalidDataException("Reserved residual coding method.");
        var paramBits = method == 0 ? 4 : 5;
        var escape = method == 0 ? 15 : 31;
        var partitionOrder = (int)r.Read(4);
        var partitions = 1 << partitionOrder;
        var partSize = blockSize >> partitionOrder;
        if (partSize < order || (blockSize & (partitions - 1)) != 0) throw new InvalidDataException("Bad partition order.");
        var i = order;
        for (var p = 0; p < partitions; p++)
        {
            var count = p == 0 ? partSize - order : partSize;
            var k = (int)r.Read(paramBits);
            if (k == escape)
            {
                var bits = (int)r.Read(5);
                for (var n = 0; n < count; n++) output[i++] = bits == 0 ? 0 : r.ReadSigned(bits);
            }
            else
            {
                for (var n = 0; n < count; n++)
                {
                    var q = r.ReadUnary();
                    var u = (q << k) | (k > 0 ? r.Read(k) : 0);
                    output[i++] = (int)(u >> 1) ^ -(int)(u & 1);
                }
            }
        }
    }

    private static int Crc8(ReadOnlySpan<byte> data)
    {
        var crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
        }
        return crc;
    }

    private static int Crc16(ReadOnlySpan<byte> data)
    {
        var crc = 0;
        foreach (var b in data)
        {
            crc ^= b << 8;
            for (var i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
        }
        return crc;
    }

    /// <summary>MSB-first bit reader over a span; throws EndOfStreamException past the end.</summary>
    private ref struct BitReader(Span<byte> data)
    {
        private readonly Span<byte> _data = data;
        private int _pos;      // byte
        private int _bit;      // bits used in _data[_pos]

        public readonly int BytePosition => _pos + (_bit > 0 ? 1 : 0);

        public uint Read(int bits)
        {
            uint value = 0;
            while (bits > 0)
            {
                if (_pos >= _data.Length) throw new EndOfStreamException();
                var available = 8 - _bit;
                var take = Math.Min(available, bits);
                var chunk = (uint)(_data[_pos] >> (available - take)) & ((1u << take) - 1);
                value = (value << take) | chunk;
                bits -= take;
                _bit += take;
                if (_bit == 8) { _bit = 0; _pos++; }
            }
            return value;
        }

        public int ReadSigned(int bits)
        {
            if (bits == 0) return 0;
            var v = Read(bits);
            return bits == 32 ? (int)v : (int)(v << (32 - bits)) >> (32 - bits);
        }

        public uint ReadUnary()
        {
            uint count = 0;
            while (true)
            {
                if (_pos >= _data.Length) throw new EndOfStreamException();
                // Fast path: skip whole zero bytes.
                if (_bit == 0 && _data[_pos] == 0) { count += 8; _pos++; continue; }
                if (Read(1) == 1) return count;
                count++;
                if (count > 1 << 20) throw new InvalidDataException("Runaway unary code.");
            }
        }

        public void AlignToByte()
        {
            if (_bit > 0) { _bit = 0; _pos++; }
        }
    }
}

/// <summary>One decoded frame: its first sample's number in the stream and its samples.</summary>
public readonly record struct DecodedFrame(long Sample, short[] Samples);

/// <summary>A streaming audio decoder: bytes in, interleaved 16-bit frames out.</summary>
public interface IAudioDecoder
{
    int SampleRate { get; }
    int Channels { get; }
    IEnumerable<short[]> Feed(ReadOnlySpan<byte> data);
}
