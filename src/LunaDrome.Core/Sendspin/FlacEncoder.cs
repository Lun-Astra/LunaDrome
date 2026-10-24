namespace LunaDrome.Core.Sendspin;

/// <summary>
/// A small FLAC encoder for Sendspin: one FLAC frame per audio chunk, 16-bit stereo (or mono).
/// Per frame it picks the cheapest stereo mode (independent, left/side, right/side, mid/side)
/// and per channel the cheapest of CONSTANT (silence), FIXED predictors of order 0-4 with
/// partitioned Rice coding, or VERBATIM. That's lossless and roughly halves the bitrate of PCM,
/// at a tiny CPU cost; no LPC (the extra few percent aren't worth it for a LAN stream).
/// </summary>
public sealed class FlacEncoder
{
    private const int MaxFixedOrder = 4;
    private const int MaxPartitionOrder = 6;

    private readonly int _sampleRate, _channels, _blockSize;
    private readonly int[][] _signals;            // L, R, mid, side (or just the mono channel)
    private readonly int[] _residual;
    private readonly BitWriter _writer;
    private long _frameNumber;

    public FlacEncoder(int sampleRate, int channels, int blockSize)
    {
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        if (blockSize is < 16 or > 65535) throw new ArgumentOutOfRangeException(nameof(blockSize));
        _sampleRate = sampleRate;
        _channels = channels;
        _blockSize = blockSize;
        _signals = Enumerable.Range(0, channels == 2 ? 4 : 1).Select(_ => new int[blockSize]).ToArray();
        _residual = new int[blockSize];
        // Worst case: verbatim 17-bit side + 16-bit channel + headers.
        _writer = new BitWriter(blockSize * channels * 3 + 64);
    }

    /// <summary>"fLaC" + the STREAMINFO block (marked last): what Sendspin's codec_header carries
    /// and what a decoder needs before the first frame.</summary>
    public byte[] Header()
    {
        var header = new byte[4 + 4 + 34];
        "fLaC"u8.CopyTo(header);
        header[4] = 0x80;                 // last metadata block, type 0 = STREAMINFO
        header[7] = 34;                   // block length
        var info = header.AsSpan(8);
        info[0] = (byte)(_blockSize >> 8); info[1] = (byte)_blockSize;   // min block size
        info[2] = (byte)(_blockSize >> 8); info[3] = (byte)_blockSize;   // max block size
        // min/max frame size (24 bits each) = 0: unknown.
        // sample rate (20) | channels-1 (3) | bits-1 (5) | total samples (36) = 0: unknown
        ulong packed = ((ulong)_sampleRate << 44) | ((ulong)(_channels - 1) << 41) | (15UL << 36);
        for (var i = 0; i < 8; i++) info[10 + i] = (byte)(packed >> (56 - 8 * i));
        // MD5 (16 bytes) = 0: not computed (it's a live stream).
        return header;
    }

    /// <summary>Encodes exactly one block of interleaved 16-bit samples into one FLAC frame.</summary>
    public byte[] EncodeFrame(ReadOnlySpan<short> interleaved)
    {
        if (interleaved.Length != _blockSize * _channels)
            throw new ArgumentException($"Expected {_blockSize * _channels} samples, got {interleaved.Length}.");

        int assignment;   // FLAC channel assignment code
        int first, second, firstBits, secondBits;
        if (_channels == 1)
        {
            for (var i = 0; i < _blockSize; i++) _signals[0][i] = interleaved[i];
            assignment = 0;
            first = 0; second = -1; firstBits = 16; secondBits = 0;
        }
        else
        {
            int[] left = _signals[0], right = _signals[1], mid = _signals[2], side = _signals[3];
            for (var i = 0; i < _blockSize; i++)
            {
                int l = interleaved[2 * i], r = interleaved[2 * i + 1];
                left[i] = l; right[i] = r;
                mid[i] = (l + r) >> 1;      // FLAC's mid: floor((L+R)/2), the lost bit is in side
                side[i] = l - r;
            }
            long costL = SubframeCost(left, 16), costR = SubframeCost(right, 16);
            long costM = SubframeCost(mid, 16), costS = SubframeCost(side, 17);
            // 0b0001 independent, 0b1000 left/side, 0b1001 side/right, 0b1010 mid/side
            var options = new (long Cost, int Code, int A, int B, int ABits, int BBits)[]
            {
                (costL + costR, 0b0001, 0, 1, 16, 16),
                (costL + costS, 0b1000, 0, 3, 16, 17),
                (costS + costR, 0b1001, 3, 1, 17, 16),
                (costM + costS, 0b1010, 2, 3, 16, 17),
            };
            var best = options.MinBy(o => o.Cost);
            assignment = best.Code;
            first = best.A; second = best.B; firstBits = best.ABits; secondBits = best.BBits;
        }

        var w = _writer;
        w.Reset();
        WriteFrameHeader(w, assignment);
        WriteSubframe(w, _signals[first], firstBits);
        if (second >= 0) WriteSubframe(w, _signals[second], secondBits);
        w.AlignToByte();
        var crc16 = Crc16(w.Buffer.AsSpan(0, w.ByteCount));
        w.Write((uint)crc16, 16);
        _frameNumber++;
        return w.Buffer.AsSpan(0, w.ByteCount).ToArray();
    }

    // ---- frame header ----

    private void WriteFrameHeader(BitWriter w, int assignment)
    {
        var start = w.ByteCount;
        w.Write(0b11111111111110, 14);     // sync
        w.Write(0, 1);                      // reserved
        w.Write(0, 1);                      // fixed block size: the header carries the frame number
        w.Write(0b0111, 4);                 // block size: 16-bit (size-1) at the end of the header
        w.Write(SampleRateCode(out var extraRate), 4);
        w.Write((uint)assignment, 4);
        w.Write(0b100, 3);                  // 16 bits per sample
        w.Write(0, 1);                      // reserved
        WriteUtf8Number(w, _frameNumber);
        w.Write((uint)(_blockSize - 1), 16);
        if (extraRate.Bits > 0) w.Write(extraRate.Value, extraRate.Bits);
        w.Write(Crc8(w.Buffer.AsSpan(start, w.ByteCount - start)), 8);
    }

    private uint SampleRateCode(out (uint Value, int Bits) extra)
    {
        extra = (0, 0);
        switch (_sampleRate)
        {
            case 88200: return 0b0001;
            case 176400: return 0b0010;
            case 192000: return 0b0011;
            case 8000: return 0b0100;
            case 16000: return 0b0101;
            case 22050: return 0b0110;
            case 24000: return 0b0111;
            case 32000: return 0b1000;
            case 44100: return 0b1001;
            case 48000: return 0b1010;
            case 96000: return 0b1011;
        }
        if (_sampleRate % 1000 == 0 && _sampleRate / 1000 <= 255) { extra = ((uint)(_sampleRate / 1000), 8); return 0b1100; }
        if (_sampleRate <= 65535) { extra = ((uint)_sampleRate, 16); return 0b1101; }
        extra = ((uint)(_sampleRate / 10), 16);
        return 0b1110;
    }

    /// <summary>FLAC's "UTF-8" coding of the frame number (up to 36 bits).</summary>
    private static void WriteUtf8Number(BitWriter w, long value)
    {
        var v = (ulong)value & 0xF_FFFF_FFFF;
        if (v < 0x80) { w.Write((uint)v, 8); return; }
        var bytes = v < 0x800 ? 2 : v < 0x10000 ? 3 : v < 0x200000 ? 4 : v < 0x4000000 ? 5 : v < 0x80000000 ? 6 : 7;
        var lead = bytes == 7 ? 0xFEu : (0xFF00u >> bytes) & 0xFF;
        var firstBits = bytes == 7 ? 0 : 7 - bytes;
        w.Write(lead | (uint)(v >> (6 * (bytes - 1))) & ((1u << firstBits) - 1), 8);
        for (var i = bytes - 2; i >= 0; i--)
            w.Write(0x80u | (uint)((v >> (6 * i)) & 0x3F), 8);
    }

    // ---- subframes ----

    /// <summary>Bits the cheapest subframe for this signal would take.</summary>
    private long SubframeCost(int[] signal, int bits)
    {
        if (IsConstant(signal)) return 8 + bits;
        var (cost, _, _) = BestFixed(signal, bits);
        return Math.Min(cost, 8 + (long)bits * _blockSize);
    }

    private void WriteSubframe(BitWriter w, int[] signal, int bits)
    {
        if (IsConstant(signal))
        {
            w.Write(0, 1);
            w.Write(0b000000, 6);           // CONSTANT
            w.Write(0, 1);                   // no wasted bits
            w.WriteSigned(signal[0], bits);
            return;
        }
        var (cost, order, partitionOrder) = BestFixed(signal, bits);
        if (cost >= 8 + (long)bits * _blockSize)
        {
            w.Write(0, 1);
            w.Write(0b000001, 6);           // VERBATIM
            w.Write(0, 1);
            for (var i = 0; i < _blockSize; i++) w.WriteSigned(signal[i], bits);
            return;
        }
        w.Write(0, 1);
        w.Write((uint)(0b001000 | order), 6);   // FIXED, predictor order
        w.Write(0, 1);
        for (var i = 0; i < order; i++) w.WriteSigned(signal[i], bits);   // warm-up samples
        ComputeResidual(signal, order, _residual);
        WriteResidual(w, _residual, order, partitionOrder);
    }

    private bool IsConstant(int[] signal)
    {
        var v = signal[0];
        for (var i = 1; i < _blockSize; i++) if (signal[i] != v) return false;
        return true;
    }

    /// <summary>The FIXED order and partition order with the fewest bits, and that count.</summary>
    private (long Cost, int Order, int PartitionOrder) BestFixed(int[] signal, int bits)
    {
        var best = (Cost: long.MaxValue, Order: 0, PartitionOrder: 0);
        for (var order = 0; order <= MaxFixedOrder && order < _blockSize; order++)
        {
            ComputeResidual(signal, order, _residual);
            var (riceBits, partitionOrder) = BestPartitioning(_residual, order);
            var cost = 8 + (long)order * bits + riceBits;
            if (cost < best.Cost) best = (cost, order, partitionOrder);
        }
        return best;
    }

    private void ComputeResidual(int[] s, int order, int[] r)
    {
        // The FIXED predictors are successive differences.
        switch (order)
        {
            case 0: for (var i = 0; i < _blockSize; i++) r[i] = s[i]; break;
            case 1: for (var i = 1; i < _blockSize; i++) r[i] = s[i] - s[i - 1]; break;
            case 2: for (var i = 2; i < _blockSize; i++) r[i] = s[i] - 2 * s[i - 1] + s[i - 2]; break;
            case 3: for (var i = 3; i < _blockSize; i++) r[i] = s[i] - 3 * s[i - 1] + 3 * s[i - 2] - s[i - 3]; break;
            default: for (var i = 4; i < _blockSize; i++) r[i] = s[i] - 4 * s[i - 1] + 6 * s[i - 2] - 4 * s[i - 3] + s[i - 4]; break;
        }
    }

    private int MaxPartitionOrderFor(int order)
    {
        var p = 0;
        while (p < MaxPartitionOrder && _blockSize % (1 << (p + 1)) == 0 && (_blockSize >> (p + 1)) > order) p++;
        return p;
    }

    /// <summary>Residual bits (incl. the coding header) for the best partition order.</summary>
    private (long Bits, int PartitionOrder) BestPartitioning(int[] residual, int order)
    {
        var best = (Bits: long.MaxValue, PartitionOrder: 0);
        for (var p = 0; p <= MaxPartitionOrderFor(order); p++)
        {
            var bits = 2L + 4;   // coding method + partition order
            var parameterBits = 4;
            long total = 0;
            var useWide = false;
            var partitions = 1 << p;
            var partSize = _blockSize >> p;
            for (var part = 0; part < partitions; part++)
            {
                var start = part == 0 ? order : part * partSize;
                var end = (part + 1) * partSize;
                var (k, cost) = BestRiceParameter(residual, start, end);
                if (k > 14) useWide = true;
                total += cost;
            }
            if (useWide) parameterBits = 5;
            bits += total + (long)partitions * parameterBits;
            if (bits < best.Bits) best = (bits, p);
        }
        return best;
    }

    private static (int K, long Cost) BestRiceParameter(int[] residual, int start, int end)
    {
        var n = end - start;
        if (n <= 0) return (0, 0);
        long sum = 0;
        for (var i = start; i < end; i++) sum += Fold(residual[i]);
        // Start from the estimate log2(mean) and check its neighbours exactly.
        var mean = sum / n;
        var guess = mean > 0 ? 63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)mean) : 0;
        var best = (K: 0, Cost: long.MaxValue);
        for (var k = Math.Max(0, guess - 1); k <= Math.Min(30, guess + 1); k++)
        {
            long cost = (long)n * (k + 1);
            for (var i = start; i < end; i++) cost += Fold(residual[i]) >> k;
            if (cost < best.Cost) best = (k, cost);
        }
        return best;
    }

    private static uint Fold(int v) => (uint)((v << 1) ^ (v >> 31));

    private void WriteResidual(BitWriter w, int[] residual, int order, int partitionOrder)
    {
        var partitions = 1 << partitionOrder;
        var partSize = _blockSize >> partitionOrder;
        var ks = new int[partitions];
        var wide = false;
        for (var part = 0; part < partitions; part++)
        {
            var start = part == 0 ? order : part * partSize;
            ks[part] = BestRiceParameter(residual, start, (part + 1) * partSize).K;
            if (ks[part] > 14) wide = true;
        }
        w.Write(wide ? 1u : 0u, 2);         // 00 = 4-bit Rice parameters, 01 = 5-bit
        w.Write((uint)partitionOrder, 4);
        for (var part = 0; part < partitions; part++)
        {
            var k = ks[part];
            w.Write((uint)k, wide ? 5 : 4);
            var start = part == 0 ? order : part * partSize;
            var end = (part + 1) * partSize;
            for (var i = start; i < end; i++)
            {
                var u = Fold(residual[i]);
                w.WriteUnary(u >> k);
                if (k > 0) w.Write(u & ((1u << k) - 1), k);
            }
        }
    }

    // ---- CRCs ----

    private static uint Crc8(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
        }
        return crc;
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (var b in data)
        {
            crc ^= (uint)b << 8;
            for (var i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
        }
        return (ushort)crc;
    }

    /// <summary>MSB-first bit writer into a reusable buffer.</summary>
    private sealed class BitWriter(int capacity)
    {
        private ulong _acc;
        private int _accBits;
        public byte[] Buffer { get; private set; } = new byte[capacity];
        public int ByteCount { get; private set; }

        public void Reset() { _acc = 0; _accBits = 0; ByteCount = 0; }

        public void Write(uint value, int bits)
        {
            if (bits == 0) return;
            _acc = (_acc << bits) | (value & (bits == 32 ? 0xFFFFFFFFu : (1u << bits) - 1));
            _accBits += bits;
            while (_accBits >= 8)
            {
                _accBits -= 8;
                Put((byte)(_acc >> _accBits));
            }
        }

        public void WriteSigned(int value, int bits) => Write((uint)value, bits);

        public void WriteUnary(uint zeros)
        {
            while (zeros >= 24) { Write(0, 24); zeros -= 24; }
            Write(1, (int)zeros + 1);   // zeros, then the terminating 1
        }

        public void AlignToByte()
        {
            if (_accBits > 0) Write(0, 8 - _accBits);
        }

        private void Put(byte b)
        {
            if (ByteCount == Buffer.Length)
            {
                var bigger = new byte[Buffer.Length * 2];
                Array.Copy(Buffer, bigger, ByteCount);
                Buffer = bigger;
            }
            Buffer[ByteCount++] = b;
        }
    }
}
