namespace FiestaLibReloaded.Networking;

/// <summary>
/// Raw packet representation: department + command opcode + payload bytes.
/// </summary>
public class FiestaPacket
{
    public byte Department { get; }
    public byte Command { get; }
    public ushort Opcode => (ushort)((Department << 8) | Command);
    public ReadOnlyMemory<byte> Payload { get; }

    public FiestaPacket(byte department, byte command, ReadOnlyMemory<byte> payload)
    {
        Department = department;
        Command = command;
        Payload = payload;
    }

    /// <summary>
    /// Build a packet from a typed struct body. Looks up the opcode in PacketRegistry.
    /// </summary>
    public static FiestaPacket Create<T>(T body) where T : IFiestaPacketBody
    {
        var opcode = PacketRegistry.GetOpcode<T>();
        var dept = (byte)(opcode >> 8);
        var cmd = (byte)(opcode & 0xFF);
        var payload = body.ToBytes();
        return new FiestaPacket(dept, cmd, payload);
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
    /// Serialize to wire format: [length prefix] [department] [command] [payload].
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

        ms.WriteByte(Department);
        ms.WriteByte(Command);
        ms.Write(Payload.Span);
        return ms.ToArray();
    }
}
