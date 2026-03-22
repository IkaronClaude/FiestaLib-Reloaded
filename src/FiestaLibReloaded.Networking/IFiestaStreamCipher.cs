namespace FiestaLibReloaded.Networking;

/// <summary>
/// Abstraction for XOR-based stream encryption used in client-server traffic.
/// XOR is symmetric so a single Transform method handles both encrypt and decrypt.
/// </summary>
public interface IFiestaStreamCipher
{
    void Transform(Span<byte> data);
}

/// <summary>
/// No-op cipher for server-to-server traffic (unencrypted).
/// </summary>
public sealed class NullCipher : IFiestaStreamCipher
{
    public static readonly NullCipher Instance = new();
    public void Transform(Span<byte> data) { }
}
