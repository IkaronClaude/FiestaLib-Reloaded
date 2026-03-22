namespace FiestaLibReloaded.Networking;

/// <summary>
/// Implemented by all generated protocol structs and shared types.
/// Provides binary serialization via BinaryReader/BinaryWriter.
/// </summary>
public interface IFiestaPacketBody
{
    void Read(BinaryReader reader);
    void Write(BinaryWriter writer);
}

public static class FiestaPacketBodyExtensions
{
    public static void Read(this IFiestaPacketBody body, Stream stream)
        => body.Read(new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true));

    public static void Write(this IFiestaPacketBody body, Stream stream)
        => body.Write(new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true));

    public static byte[] ToBytes(this IFiestaPacketBody body)
    {
        using var ms = new MemoryStream();
        body.Write(ms);
        return ms.ToArray();
    }

    public static T FromBytes<T>(byte[] data) where T : IFiestaPacketBody, new()
    {
        var body = new T();
        using var ms = new MemoryStream(data);
        body.Read(ms);
        return body;
    }
}
