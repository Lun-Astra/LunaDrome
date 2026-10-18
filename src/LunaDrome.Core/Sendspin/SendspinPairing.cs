using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace LunaDrome.Core.Sendspin;

/// <summary>
/// Pairs LunaDrome with a Sendspin speaker (encrypted revision) so it may play without the
/// speaker allowing unpaired access - the spec's three methods:
/// <list type="bullet">
/// <item>Dynamic pairing code: the speaker shows a fresh 6-digit code (on its display or web
/// page), the user types it into LunaDrome; CPace proves both sides know it, bound to this
/// very handshake, and the speaker hands over a new long-term PSK wrapped under the result.</item>
/// <item>Static pairing code: the same with the speaker's fixed 8-digit code (after a gesture
/// on the speaker, e.g. a button).</item>
/// <item>Pairing PSK: the user pastes the speaker's pairing token (SP:0...), which carries its
/// key and pairing PSK; no code needed.</item>
/// </list>
/// Afterwards the session re-handshakes to the long-term PSK and is activated for playback;
/// the PSK is stored so later connections are paired from the first message.
/// </summary>
public sealed class SendspinPairing
{
    private static readonly byte[] PakeSidLabel = "sendspin-pair-pake-v1"u8.ToArray();
    private static readonly byte[] PskWrapLabel = "sendspin-pair-psk-wrap-v1"u8.ToArray();
    private static readonly byte[] NonceWrapLabel = "sendspin-pair-nonce-wrap-v1"u8.ToArray();
    private static readonly byte[] CommitLabel = "sendspin-pair-commit-v1"u8.ToArray();
    private static readonly byte[] CodeDeriveLabel = "sendspin-pairing-code-derive-v1"u8.ToArray();
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(60);
    private const int RoundLimit = 20;

    private readonly SendspinSpeaker _speaker;
    private readonly SendspinCredentials _credentials;
    private readonly Channel<(string Type, JsonObject Payload)> _messages = Channel.CreateUnbounded<(string, JsonObject)>();

    public SendspinPairing(SendspinSpeaker speaker, SendspinCredentials credentials)
    {
        _speaker = speaker;
        _credentials = credentials;
        speaker.PairingMessage += (type, payload) => _messages.Writer.TryWrite((type, payload));
    }

    /// <summary>Progress for the user ("Waiting for the speaker…", its pair-pending message).</summary>
    public event Action<string>? Status;

    public static bool OffersDynamicCode(SendspinSpeaker s) =>
        s.PairMethods.Contains("dynamic_pairing_code") && s.DynamicCodeFormats.Contains("digits");
    public static bool OffersStaticCode(SendspinSpeaker s) => s.PairMethods.Contains("static_pairing_code");
    public static bool OffersToken(SendspinSpeaker s) => s.PairMethods.Contains("pairing_psk");

    /// <summary>Pairs with a code the user reads off the speaker. <paramref name="askCode"/> is
    /// called once per round (true = the previous code was wrong); null cancels.</summary>
    /// <exception cref="SendspinPairingException">Wrong code, cancelled, or the speaker aborted.</exception>
    public async Task PairWithCodeAsync(bool dynamic, Func<bool, Task<string?>> askCode, CancellationToken token)
    {
        // Code pairing runs on a Sentinel-keyed session.
        if (_speaker.PskCategory != "sn")
        {
            await _speaker.RehandshakeAsync(SendspinCredentials.SentinelPsk, "sn", token).ConfigureAwait(false);
        }
        var pairing = new JsonObject { ["method"] = dynamic ? "dynamic_pairing_code" : "static_pairing_code" };
        if (dynamic) pairing["format"] = "digits";
        var index = _speaker.Activate(playback: false, pairing);
        Status?.Invoke("Waiting for the speaker…");

        var init = await ReceivePairInitAsync(index, token).ConfigureAwait(false);
        byte[]? commitB = null;
        if (dynamic)
            commitB = DecodeField(init, "commit_B", 32);
        else if (init["commit_B"] != null)
            throw Protocol("client/pair-init carries commit_B for a static code.");

        var nonceA = dynamic ? RandomNumberGenerator.GetBytes(32) : [];
        var round = 1;
        CPace cpace;
        byte[] sid, prs;
        JsonObject confirm;
        while (true)
        {
            if (dynamic)
            {
                var initPayload = new JsonObject();
                if (round == 1) initPayload["nonce_A"] = Base64Url.Encode(nonceA);
                _speaker.SendPairingJson("server/pair-init", initPayload);
            }
            var entered = await askCode(round > 1).ConfigureAwait(false);
            if (entered == null)
            {
                Abort("user_cancelled");
                throw new SendspinPairingException("Pairing cancelled.", cancelled: true);
            }
            var digits = new string(entered.Where(c => c is not (' ' or '-')).ToArray());
            var length = dynamic ? 6 : 8;
            if (digits.Length != length || !digits.All(char.IsAsciiDigit))
            {
                Abort("user_cancelled");
                throw new SendspinPairingException($"The code is {length} digits.");
            }
            prs = Encoding.ASCII.GetBytes(digits);
            sid = [.. PakeSidLabel, .. _speaker.HandshakeHash, .. BigEndian(index), .. BigEndian(dynamic ? round : 1)];
            cpace = CPace.Start(initiator: true, prs, sid, "server"u8.ToArray());
            _speaker.SendPairingJson("server/pair-auth", new JsonObject { ["pake_msg_1"] = Base64Url.Encode(cpace.PublicShare) });
            var auth = await ReceiveAsync(token, "client/pair-auth").ConfigureAwait(false);
            try { cpace.Derive(DecodeField(auth.Payload, "pake_msg_2", 32), "client"u8.ToArray()); }
            catch (CryptographicException) { throw Protocol("The speaker's CPace share is invalid."); }
            _speaker.SendPairingJson("server/pair-confirm", new JsonObject { ["server_kc"] = Base64Url.Encode(cpace.Tag()) });

            var reply = await ReceiveAsync(token, "client/pair-confirm", "client/pair-retry").ConfigureAwait(false);
            if (reply.Type == "client/pair-confirm")
            {
                confirm = reply.Payload;
                break;
            }
            if (!dynamic) throw Protocol("client/pair-retry isn't part of static-code pairing.");
            if (round >= RoundLimit) throw new SendspinPairingException("Too many wrong codes; the speaker stopped accepting them.");
            round++;   // the speaker couldn't verify: the code was wrong, try again
        }

        if (!cpace.Verify(DecodeField(confirm, "client_kc", 64)))
        {
            Abort("pairing_code_mismatch");
            throw new SendspinPairingException("That code doesn't match.");
        }
        if (dynamic)
        {
            // The revealed nonce_B must open the earlier commitment, and the code the user typed
            // must be the one derived from this handshake: that's what defeats a relay.
            byte[] nonceB;
            try { nonceB = Unwrap(NonceWrapLabel, sid, cpace, DecodeField(confirm, "wrapped_nonce_B", 48)); }
            catch (CryptographicException) { throw Protocol("wrapped_nonce_B doesn't decrypt."); }
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData([.. CommitLabel, .. nonceB]), commitB!))
                throw Protocol("The revealed nonce doesn't match the commitment.");
            if (DeriveDigits(_speaker.HandshakeHash, nonceA, nonceB) != Encoding.ASCII.GetString(prs))
                throw Protocol("The code isn't bound to this connection.");
        }
        else if (confirm["wrapped_nonce_B"] != null)
        {
            throw Protocol("wrapped_nonce_B in static-code pairing.");
        }

        var finalize = await ReceiveAsync(token, "client/pair-finalize").ConfigureAwait(false);
        byte[] psk;
        try { psk = Unwrap(PskWrapLabel, sid, cpace, DecodeField(finalize.Payload, "wrapped_psk", 48)); }
        catch (CryptographicException) { throw Protocol("wrapped_psk doesn't decrypt."); }
        await FinishAsync(psk, token).ConfigureAwait(false);
    }

    /// <summary>Pairs with the speaker's pairing token (SP:0..., from its label, leaflet or web page).</summary>
    /// <exception cref="SendspinPairingException">Malformed token, wrong speaker, or the speaker aborted.</exception>
    public async Task PairWithTokenAsync(string token, CancellationToken cancellationToken)
    {
        var payload = DecodeToken(token, '0') ?? throw new SendspinPairingException("That isn't a pairing token (SP:0…).");
        if (payload.Length < 64) throw new SendspinPairingException("That pairing token is too short.");
        if (!payload.AsSpan(0, 32).SequenceEqual(_speaker.ClientKey))
            throw new SendspinPairingException("That token belongs to a different speaker.");
        var pairingPsk = payload[32..64];

        // Switch to the pairing PSK (authenticates both sides), then let it hand over a long-term one.
        await _speaker.RehandshakeAsync(pairingPsk, "pr", cancellationToken).ConfigureAwait(false);
        var index = _speaker.Activate(playback: false, new JsonObject { ["method"] = "pairing_psk" });
        var init = await ReceivePairInitAsync(index, cancellationToken).ConfigureAwait(false);
        if (init["commit_B"] != null) throw Protocol("client/pair-init carries commit_B for a pairing PSK.");
        var finalize = await ReceiveAsync(cancellationToken, "client/pair-finalize").ConfigureAwait(false);
        var psk = DecodeField(finalize.Payload, "long_term_psk", 32);
        if (psk.AsSpan().SequenceEqual(pairingPsk)) throw Protocol("The speaker reused its pairing PSK.");
        await FinishAsync(psk, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores the new long-term PSK, acknowledges it, re-handshakes to it and activates playback.</summary>
    private async Task FinishAsync(byte[] psk, CancellationToken token)
    {
        if (psk.Length != 32 || psk.AsSpan().SequenceEqual(SendspinCredentials.SentinelPsk))
            throw Protocol("The speaker offered an unusable PSK.");
        _credentials.SavePairing(_speaker.ClientId, psk);   // persist before acknowledging
        _speaker.SendPairingJson("server/pair-finalize", new JsonObject());
        await _speaker.RehandshakeAsync(psk, "lt", token).ConfigureAwait(false);
        _speaker.Activate(playback: true);
    }

    private async Task<JsonObject> ReceivePairInitAsync(int index, CancellationToken token)
    {
        while (true)
        {
            var message = await ReceiveAsync(token, "client/pair-init", "client/pair-pending").ConfigureAwait(false);
            var theirs = (int?)message.Payload["pairing_index"] ?? -1;
            if (theirs < index) continue;   // left over from a superseded attempt
            if (theirs > index) throw Protocol("pairing_index ahead of LunaDrome's count.");
            if (message.Type == "client/pair-pending")
            {
                // Held back (no pairing window open, cooldown): show what it asks for.
                var text = (string?)message.Payload["message"];
                Status?.Invoke(string.IsNullOrWhiteSpace(text)
                    ? "The speaker is waiting - it may need a button press or a moment."
                    : "The speaker says: " + (text.Length > 200 ? text[..200] : text));
                continue;
            }
            return message.Payload;
        }
    }

    private async Task<(string Type, JsonObject Payload)> ReceiveAsync(CancellationToken token, params string[] expected)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(StepTimeout);
        while (true)
        {
            (string Type, JsonObject Payload) message;
            try { message = await _messages.Reader.ReadAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new SendspinPairingException("The speaker stopped answering.");
            }
            if (message.Type == "pair/abort")
            {
                var reason = (string?)message.Payload["reason"];
                throw new SendspinPairingException(reason switch
                {
                    "pairing_code_mismatch" => "That code doesn't match.",
                    "attempt_timeout" => "The speaker gave up waiting.",
                    "user_cancelled" => "Pairing was cancelled on the speaker.",
                    "concurrent_attempt" => "The speaker is already pairing with something else.",
                    "method_not_supported" => "The speaker doesn't support that way of pairing.",
                    _ => $"The speaker stopped the pairing ({reason}).",
                }, cancelled: reason == "user_cancelled");
            }
            if (expected.Contains(message.Type)) return message;
            // Anything else from a previous attempt is a leftover: ignore it.
        }
    }

    private void Abort(string reason)
    {
        _speaker.SendPairingJson("pair/abort", new JsonObject { ["reason"] = reason });
        _speaker.Activate(playback: false);   // leave the pairing activity
    }

    private byte[] Unwrap(byte[] label, byte[] sid, CPace cpace, byte[] wrapped)
    {
        var key = SHA256.HashData([.. label, .. sid, .. cpace.Isk]);
        return NoiseCipher.Open(_speaker.Suite, key, 0, [], wrapped);   // zero nonce, no AD
    }

    internal static string DeriveDigits(byte[] h, byte[] nonceA, byte[] nonceB)
    {
        var digest = SHA256.HashData([.. CodeDeriveLabel, .. h, .. nonceA, .. nonceB]);
        var value = new System.Numerics.BigInteger(digest, isUnsigned: true, isBigEndian: true) % 1_000_000;
        return ((int)value).ToString("D6");
    }

    private static byte[] DecodeField(JsonObject payload, string name, int length)
    {
        try
        {
            var bytes = Base64Url.Decode((string?)payload[name] ?? throw Protocol($"{name} is missing."));
            return bytes.Length == length ? bytes : throw Protocol($"{name} has the wrong length.");
        }
        catch (FormatException) { throw Protocol($"{name} isn't base64url."); }
    }

    private static byte[] BigEndian(int value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static SendspinPairingException Protocol(string message) =>
        new($"Pairing failed: {message}", protocolError: true);

    /// <summary>Decodes a pairing token "SP:" + version + base32 body (2s written as 9s),
    /// leniently: case, surrounding spaces and the prefix don't matter.</summary>
    public static byte[]? DecodeToken(string text, char version)
    {
        var s = text.Trim().ToUpperInvariant();
        if (s.StartsWith("SP:", StringComparison.Ordinal)) s = s[3..];
        if (s.Length < 2 || s[0] != version) return null;
        var body = s[1..].Replace('9', '2');
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in body)
        {
            var v = alphabet.IndexOf(c);
            if (v < 0) return null;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        return output.ToArray();
    }
}

public sealed class SendspinPairingException(string message, bool cancelled = false, bool protocolError = false) : Exception(message)
{
    public bool Cancelled { get; } = cancelled;
    /// <summary>A condition no conformant speaker produces: the connection should be dropped.</summary>
    public bool ProtocolError { get; } = protocolError;
}
