using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace LunaDrome.Core.Sendspin;

/// <summary>The two Noise cipher suites Sendspin defines (servers must support both).</summary>
public enum NoiseSuite { ChaChaPoly, AesGcm }

/// <summary>
/// Noise Protocol Framework, just the part Sendspin uses: pattern KKpsk2 with the server as
/// initiator (both static keys known up front, the PSK mixed in at message 2), Curve25519,
/// SHA-256, and ChaCha20-Poly1305 or AES-GCM. Spec: noiseprotocol.org/noise.html.
/// </summary>
public sealed class NoiseInitiator
{
    private readonly NoiseSuite _suite;
    private readonly byte[] _staticPrivate, _remoteStatic;
    private readonly SymmetricState _ss;
    private byte[]? _ephemeralPrivate;

    /// <param name="psk">32 bytes, mixed in at the end of message 2.</param>
    public NoiseInitiator(NoiseSuite suite, byte[] staticPrivate, byte[] remoteStatic, byte[] prologue, byte[] psk)
    {
        _suite = suite;
        _staticPrivate = staticPrivate;
        _remoteStatic = remoteStatic;
        Psk = psk;
        var name = $"Noise_KKpsk2_25519_{(suite == NoiseSuite.ChaChaPoly ? "ChaChaPoly" : "AESGCM")}_SHA256";
        _ss = new SymmetricState(suite, System.Text.Encoding.ASCII.GetBytes(name));
        _ss.MixHash(prologue);
        // Pre-messages: "-> s" then "<- s".
        _ss.MixHash(X25519Keys.PublicFromPrivate(staticPrivate));
        _ss.MixHash(remoteStatic);
    }

    public byte[] Psk { get; set; }

    /// <summary>Message 1: e, es, ss (+ payload, encrypted without the PSK).</summary>
    public byte[] WriteMessage1(byte[] payload)
    {
        _ephemeralPrivate = X25519Keys.NewPrivate();
        var e = X25519Keys.PublicFromPrivate(_ephemeralPrivate);
        _ss.MixHash(e);
        _ss.MixKey(e);   // psk handshakes also mix every e into the key
        _ss.MixKey(X25519Keys.Agree(_ephemeralPrivate, _remoteStatic));   // es
        _ss.MixKey(X25519Keys.Agree(_staticPrivate, _remoteStatic));      // ss
        return [.. e, .. _ss.EncryptAndHash(payload)];
    }

    /// <summary>Message 2: e, ee, se, psk; returns the decrypted payload.</summary>
    /// <exception cref="CryptographicException">Doesn't authenticate (wrong PSK, tampering).</exception>
    public byte[] ReadMessage2(ReadOnlySpan<byte> message)
    {
        if (_ephemeralPrivate == null) throw new InvalidOperationException("Message 1 first.");
        if (message.Length < 32 + 16) throw new CryptographicException("Noise message 2 is too short.");
        var re = message[..32].ToArray();
        _ss.MixHash(re);
        _ss.MixKey(re);
        _ss.MixKey(X25519Keys.Agree(_ephemeralPrivate, re));   // ee
        _ss.MixKey(X25519Keys.Agree(_staticPrivate, re));      // se (initiator's static, responder's e)
        _ss.MixKeyAndHash(Psk);                                // psk
        var payload = _ss.DecryptAndHash(message[32..].ToArray());
        HandshakeHash = _ss.H.ToArray();
        return payload;
    }

    /// <summary>The handshake hash h (pairing binds to it; a re-handshake uses it as prologue).</summary>
    public byte[] HandshakeHash { get; private set; } = [];

    /// <summary>After message 2: the cipher for what the server sends and what it receives.</summary>
    public (NoiseCipher Send, NoiseCipher Receive) Split()
    {
        var (k1, k2) = _ss.Split();
        return (new NoiseCipher(_suite, k1), new NoiseCipher(_suite, k2));
    }

    /// <summary>A copy of this handshake as it stood right after message 1, so message 2 can
    /// be checked under a second PSK (the Sentinel fallback) - same ephemeral, same h.</summary>
    public static NoiseInitiator ForkAfterMessage1(NoiseSuite suite, byte[] staticPrivate, byte[] remoteStatic,
        byte[] prologue, byte[] psk, byte[] message1Payload, NoiseInitiator original)
    {
        var fork = new NoiseInitiator(suite, staticPrivate, remoteStatic, prologue, psk);
        fork._ephemeralPrivate = original._ephemeralPrivate;
        var e = X25519Keys.PublicFromPrivate(fork._ephemeralPrivate!);
        fork._ss.MixHash(e);
        fork._ss.MixKey(e);
        fork._ss.MixKey(X25519Keys.Agree(fork._ephemeralPrivate!, remoteStatic));
        fork._ss.MixKey(X25519Keys.Agree(staticPrivate, remoteStatic));
        fork._ss.EncryptAndHash(message1Payload);
        return fork;
    }

    private sealed class SymmetricState
    {
        private readonly NoiseSuite _suite;
        private byte[] _ck;
        private byte[]? _k;
        private ulong _n;

        public SymmetricState(NoiseSuite suite, byte[] protocolName)
        {
            _suite = suite;
            H = protocolName.Length <= 32 ? [.. protocolName, .. new byte[32 - protocolName.Length]] : SHA256.HashData(protocolName);
            _ck = H.ToArray();
        }

        public byte[] H { get; private set; }

        public void MixHash(ReadOnlySpan<byte> data) => H = SHA256.HashData([.. H, .. data]);

        public void MixKey(byte[] ikm)
        {
            var (ck, k, _) = Hkdf(_ck, ikm, 2);
            _ck = ck;
            _k = k;
            _n = 0;
        }

        public void MixKeyAndHash(byte[] ikm)
        {
            var (ck, tempH, k) = Hkdf(_ck, ikm, 3);
            _ck = ck;
            MixHash(tempH);
            _k = k!;
            _n = 0;
        }

        public byte[] EncryptAndHash(byte[] plaintext)
        {
            var ciphertext = _k == null ? plaintext : NoiseCipher.Seal(_suite, _k, _n++, H, plaintext);
            MixHash(ciphertext);
            return ciphertext;
        }

        public byte[] DecryptAndHash(byte[] ciphertext)
        {
            var plaintext = _k == null ? ciphertext : NoiseCipher.Open(_suite, _k, _n++, H, ciphertext);
            MixHash(ciphertext);
            return plaintext;
        }

        public (byte[], byte[]) Split()
        {
            var (k1, k2, _) = Hkdf(_ck, [], 2);
            return (k1, k2);
        }

        private static (byte[], byte[], byte[]?) Hkdf(byte[] chainingKey, byte[] ikm, int outputs)
        {
            var temp = HMACSHA256.HashData(chainingKey, ikm);
            var o1 = HMACSHA256.HashData(temp, new byte[] { 0x01 });
            var o2 = HMACSHA256.HashData(temp, (byte[])[.. o1, 0x02]);
            var o3 = outputs == 3 ? HMACSHA256.HashData(temp, (byte[])[.. o2, 0x03]) : null;
            return (o1, o2, o3);
        }
    }
}

/// <summary>A Noise transport CipherState: one direction, a key and a counting nonce.</summary>
public sealed class NoiseCipher(NoiseSuite suite, byte[] key)
{
    private ulong _n;

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext) => Seal(suite, key, _n++, [], plaintext.ToArray());

    /// <exception cref="CryptographicException">Fails authentication.</exception>
    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext) => Open(suite, key, _n++, [], ciphertext.ToArray());

    internal static byte[] Seal(NoiseSuite suite, byte[] key, ulong n, byte[] ad, byte[] plaintext)
    {
        var aead = Aead(suite, true, key, n, ad);
        var output = new byte[aead.GetOutputSize(plaintext.Length)];
        var len = aead.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        aead.DoFinal(output, len);
        return output;
    }

    internal static byte[] Open(NoiseSuite suite, byte[] key, ulong n, byte[] ad, byte[] ciphertext)
    {
        try
        {
            var aead = Aead(suite, false, key, n, ad);
            var output = new byte[aead.GetOutputSize(ciphertext.Length)];
            var len = aead.ProcessBytes(ciphertext, 0, ciphertext.Length, output, 0);
            len += aead.DoFinal(output, len);
            return output.AsSpan(0, len).ToArray();
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException ex)
        {
            throw new CryptographicException("Noise message failed authentication.", ex);
        }
    }

    private static IAeadCipher Aead(NoiseSuite suite, bool encrypt, byte[] key, ulong n, byte[] ad)
    {
        // 96-bit nonce: 32 zero bits, then the counter (little-endian for ChaChaPoly, big for AES-GCM).
        var nonce = new byte[12];
        if (suite == NoiseSuite.ChaChaPoly)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), n);
        else
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), n);
        IAeadCipher cipher = suite == NoiseSuite.ChaChaPoly ? new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305() : new GcmBlockCipher(new AesEngine());
        cipher.Init(encrypt, new AeadParameters(new KeyParameter(key), 128, nonce, ad));
        return cipher;
    }
}

/// <summary>Curve25519 keys as raw 32-byte arrays.</summary>
public static class X25519Keys
{
    // X25519 clamps the scalar itself, so any 32 random bytes are a private key.
    public static byte[] NewPrivate() => RandomNumberGenerator.GetBytes(32);

    public static byte[] PublicFromPrivate(byte[] privateKey)
    {
        var pub = new byte[32];
        X25519.ScalarMultBase(privateKey, 0, pub, 0);
        return pub;
    }

    public static byte[] Agree(byte[] privateKey, byte[] publicKey)
    {
        var shared = new byte[32];
        if (!X25519.CalculateAgreement(privateKey, 0, publicKey, 0, shared, 0))
            throw new CryptographicException("X25519 agreement gave the all-zero value (bad public key).");
        return shared;
    }

    public static string ToId(byte[] publicKey) => Base64Url.Encode(publicKey);
}

/// <summary>base64url without padding, as Sendspin uses for ids, PSKs and handshake bytes.</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
