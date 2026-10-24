namespace LunaDrome.Core.Sendspin;

/// <summary>
/// Who LunaDrome is to Sendspin speakers. The encrypted protocol identifies a server by its
/// long-lived Curve25519 public key (<see cref="ServerId"/>); speakers remember the server
/// they last played from by it, and pairing binds a long-term PSK to it. The older protocol
/// used a free-form id (<see cref="LegacyServerId"/>).
/// </summary>
public sealed class SendspinCredentials(string legacyServerId, byte[] staticPrivateKey,
    Func<string, byte[]?> loadPairing, Action<string, byte[]?> savePairing)
{
    public string LegacyServerId { get; } = legacyServerId;
    public byte[] StaticPrivateKey { get; } = staticPrivateKey;
    public byte[] StaticPublicKey { get; } = X25519Keys.PublicFromPrivate(staticPrivateKey);
    public string ServerId => X25519Keys.ToId(StaticPublicKey);

    /// <summary>The long-term PSK from pairing with this client, if any.</summary>
    public byte[]? LongTermPsk(string clientId) => loadPairing(clientId);

    /// <summary>Stores (or with null removes) the pairing record for a client.</summary>
    public void SavePairing(string clientId, byte[]? psk) => savePairing(clientId, psk);

    /// <summary>The published Sentinel PSK, used when no other PSK applies (no authentication).</summary>
    public static readonly byte[] SentinelPsk =
        System.Security.Cryptography.SHA256.HashData("sendspin-sentinel-psk-v1"u8);

    /// <summary>psk_id = base64url(SHA-256("sendspin-psk-id-v1" || PSK)).</summary>
    public static string PskId(byte[] psk) =>
        Base64Url.Encode(System.Security.Cryptography.SHA256.HashData([.. "sendspin-psk-id-v1"u8, .. psk]));
}
