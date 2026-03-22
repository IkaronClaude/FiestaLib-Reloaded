namespace FiestaLibReloaded.Networking;

/// <summary>
/// Raw packet representation: 6-bit department + 10-bit command opcode + payload bytes.
/// Wire format: [length prefix] [opcode LE ushort] [payload].
/// </summary>
public class FiestaPacket
{
    public ushort Opcode { get; }
    public byte Department => (byte)(Opcode >> 10);
    public ushort Command => (ushort)(Opcode & 0x3FF);
    public ReadOnlyMemory<byte> Payload { get; }

    public FiestaPacket(ushort opcode, ReadOnlyMemory<byte> payload)
    {
        Opcode = opcode;
        Payload = payload;
    }

    /// <summary>
    /// Build a packet from a typed struct body. Looks up the opcode in PacketRegistry.
    /// </summary>
    public static FiestaPacket Create<T>(T body) where T : IFiestaPacketBody
    {
        var opcode = PacketRegistry.GetOpcode<T>();
        var payload = body.ToBytes();
        return new FiestaPacket(opcode, payload);
    }

    /// <summary>
    /// Deserialize the payload into a typed struct body.
    /// </summary>
    public T ReadBody<T>() where T : IFiestaPacketBody, new()
    {
        var body = new T();
        using var ms = new MemoryStream(Payload.ToArray());
        body.Read(new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true));
        return body;
    }

    /// <summary>
    /// Serialize to wire format: [length prefix] [opcode LE ushort] [payload].
    /// </summary>
    public byte[] ToBytes()
    {
        var opcodeAndPayloadLen = 2 + Payload.Length;

        using var ms = new MemoryStream();
        if (opcodeAndPayloadLen < 255)
        {
            ms.WriteByte((byte)opcodeAndPayloadLen);
        }
        else
        {
            ms.WriteByte(0x00);
            ms.WriteByte((byte)(opcodeAndPayloadLen >> 8));
            ms.WriteByte((byte)(opcodeAndPayloadLen & 0xFF));
        }

        // Opcode as little-endian ushort
        ms.WriteByte((byte)(Opcode & 0xFF));
        ms.WriteByte((byte)(Opcode >> 8));
        ms.Write(Payload.Span);
        return ms.ToArray();
    }
}
