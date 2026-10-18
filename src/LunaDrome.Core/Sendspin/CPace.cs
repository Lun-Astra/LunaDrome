using System.Numerics;
using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace LunaDrome.Core.Sendspin;

/// <summary>
/// CPace balanced PAKE, CPACE-X25519-SHA512 (draft-irtf-cfrg-cpace), initiator-responder mode
/// with explicit mutual confirmation - what Sendspin pairing codes run on. A port of the
/// reference implementation aiosendspin uses (the Python "cpace" package).
/// </summary>
public sealed class CPace
{
    private static readonly BigInteger Q = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger A = 486662;
    private static readonly BigInteger Z = 2;
    private static readonly BigInteger Inv2 = BigInteger.ModPow(2, Q - 2, Q);
    private static readonly BigInteger LegendrePower = (Q - 1) / 2;
    private static readonly byte[] Dsi = "CPace255"u8.ToArray();
    private static readonly byte[] DsiIsk = "CPace255_ISK"u8.ToArray();
    private static readonly byte[] MacLabel = "CPaceMac"u8.ToArray();

    private readonly bool _initiator;
    private readonly byte[] _sid, _ad;
    private byte[]? _scalar;
    private byte[] _isk = [], _macKey = [];
    private (byte[] Share, byte[] Ad) _sideA, _sideB;
    private bool _derived;

    private CPace(bool initiator, byte[] sid, byte[] ad, byte[] scalar, byte[] publicShare)
    {
        _initiator = initiator;
        _sid = sid;
        _ad = ad;
        _scalar = scalar;
        PublicShare = publicShare;
    }

    public byte[] PublicShare { get; }

    /// <summary>Starts a run: samples a scalar and computes this side's public share.</summary>
    /// <param name="prs">The password-related string (the pairing code's bytes).</param>
    public static CPace Start(bool initiator, byte[] prs, byte[] sid, byte[] ad, byte[]? ci = null, byte[]? scalar = null)
    {
        scalar ??= RandomNumberGenerator.GetBytes(32);
        var share = ScalarMultVfy(scalar, CalculateGenerator(prs, ci ?? [], sid))
            ?? throw new CryptographicException("CPace generator encodes a low-order point.");
        return new CPace(initiator, sid, ad, scalar, share);
    }

    /// <summary>Takes the peer's share; derives the ISK and the confirmation MAC key.</summary>
    public void Derive(byte[] peerShare, byte[] peerAd)
    {
        var scalar = _scalar ?? throw new InvalidOperationException("Derive may only be called once.");
        _scalar = null;
        if (peerShare.Length != 32) throw new CryptographicException("A CPace share is 32 bytes.");
        var shared = ScalarMultVfy(scalar, peerShare)
            ?? throw new CryptographicException("The peer's CPace share encodes a low-order point.");
        (_sideA, _sideB) = _initiator ? ((PublicShare, _ad), (peerShare, peerAd)) : ((peerShare, peerAd), (PublicShare, _ad));
        byte[] transcript = [.. LvCat(_sideA.Share, _sideA.Ad), .. LvCat(_sideB.Share, _sideB.Ad)];
        _isk = SHA512.HashData([.. LvCat(DsiIsk, _sid, shared), .. transcript]);
        _macKey = SHA512.HashData([.. MacLabel, .. _sid, .. _isk]);
        _derived = true;
    }

    /// <summary>The intermediate session key (feed it through a KDF before use).</summary>
    public byte[] Isk => _derived ? _isk : throw new InvalidOperationException("Derive first.");

    /// <summary>This side's confirmation tag (Ta for the initiator, Tb for the responder).</summary>
    public byte[] Tag() => Mac(own: true);

    /// <summary>Whether the peer's tag proves it knows the password.</summary>
    public bool Verify(byte[] peerTag)
    {
        if (!_derived) throw new InvalidOperationException("Derive first.");
        if (_sideA.Share.AsSpan().SequenceEqual(_sideB.Share) && _sideA.Ad.AsSpan().SequenceEqual(_sideB.Ad))
            return false;   // reflection
        return CryptographicOperations.FixedTimeEquals(peerTag, Mac(own: false));
    }

    private byte[] Mac(bool own)
    {
        if (!_derived) throw new InvalidOperationException("Derive first.");
        var side = own == _initiator ? _sideA : _sideB;
        return HMACSHA512.HashData(_macKey, LvCat(side.Share, side.Ad));
    }

    // ---- the group ----

    internal static byte[] CalculateGenerator(byte[] prs, byte[] ci, byte[] sid)
    {
        var genHash = SHA512.HashData(GeneratorString(prs, ci, sid))[..32];
        return Elligator2(DecodeU(genHash));
    }

    private static byte[] GeneratorString(byte[] prs, byte[] ci, byte[] sid)
    {
        var zpad = Math.Max(0, 128 - 1 - PrependLen(prs).Length - PrependLen(Dsi).Length);
        return LvCat(Dsi, prs, new byte[zpad], ci, sid);
    }

    private static BigInteger DecodeU(byte[] value)
    {
        var u = value.ToArray();
        u[^1] &= 0x7F;   // 255-bit field: ignore the top bit (RFC 7748)
        return new BigInteger(u, isUnsigned: true, isBigEndian: false);
    }

    private static BigInteger Mod(BigInteger x) => ((x % Q) + Q) % Q;

    private static BigInteger Inv0(BigInteger x) => BigInteger.ModPow(Mod(x), Q - 2, Q);

    private static byte[] Elligator2(BigInteger r)
    {
        r = Mod(r);
        var v = Mod(-A * Inv0(Mod(1 + Z * r * r)));
        var eps = BigInteger.ModPow(Mod(v * v * v + A * v * v + v), LegendrePower, Q);
        var x = Mod(eps * v - Mod(1 - eps) * A * Inv2);
        var bytes = x.ToByteArray(isUnsigned: true, isBigEndian: false);
        var output = new byte[32];
        bytes.AsSpan(0, Math.Min(32, bytes.Length)).CopyTo(output);
        return output;
    }

    /// <summary>X25519, or null when the result is the identity (low-order point).</summary>
    private static byte[]? ScalarMultVfy(byte[] scalar, byte[] point)
    {
        var shared = new byte[32];
        return X25519.CalculateAgreement(scalar, 0, point, 0, shared, 0) ? shared : null;
    }

    internal static byte[] PrependLen(byte[] data)
    {
        var output = new List<byte>();
        var length = data.Length;
        while (true)
        {
            output.Add((byte)(length & 0x7F));
            length >>= 7;
            if (length == 0) break;
            output[^1] |= 0x80;
        }
        output.AddRange(data);
        return output.ToArray();
    }

    internal static byte[] LvCat(params byte[][] parts) => parts.SelectMany(PrependLen).ToArray();
}
