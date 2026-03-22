namespace FiestaLibReloaded.Networking;

/// <summary>
/// Stream wrapper with Fiesta's length-prefixed framing.
/// Handles reading/writing packets over a network stream.
/// </summary>
public class FiestaConnection : IDisposable
{
    private readonly Stream _stream;
    private readonly IFiestaStreamCipher _cipher;
    private bool _disposed;

    public FiestaConnection(Stream stream, IFiestaStreamCipher? cipher = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _cipher = cipher ?? NullCipher.Instance;
    }

    /// <summary>
    /// Read a single packet from the stream (blocking).
    /// </summary>
    public FiestaPacket ReadPacket()
    {
        // Read length prefix
        var firstByte = ReadByteOrThrow();
        int frameLen;
        if (firstByte != 0x00)
        {
            frameLen = firstByte;
        }
        else
        {
            var hi = ReadByteOrThrow();
            var lo = ReadByteOrThrow();
            frameLen = (hi << 8) | lo;
        }

        if (frameLen < 2)
            throw new InvalidDataException($"Packet frame too short: {frameLen}");

        // Read opcode + payload
        var frameData = new byte[frameLen];
        ReadExact(frameData, 0, frameLen);
        _cipher.Transform(frameData);

        var dept = frameData[0];
        var cmd = frameData[1];
        var payload = new byte[frameLen - 2];
        if (payload.Length > 0)
            Buffer.BlockCopy(frameData, 2, payload, 0, payload.Length);

        return new FiestaPacket(dept, cmd, payload);
    }

    /// <summary>
    /// Read a single packet from the stream (async).
    /// </summary>
    public async ValueTask<FiestaPacket> ReadPacketAsync(CancellationToken ct = default)
    {
        var firstByte = await ReadByteAsyncOrThrow(ct);
        int frameLen;
        if (firstByte != 0x00)
        {
            frameLen = firstByte;
        }
        else
        {
            var hi = await ReadByteAsyncOrThrow(ct);
            var lo = await ReadByteAsyncOrThrow(ct);
            frameLen = (hi << 8) | lo;
        }

        if (frameLen < 2)
            throw new InvalidDataException($"Packet frame too short: {frameLen}");

        var frameData = new byte[frameLen];
        await ReadExactAsync(frameData, ct);
        _cipher.Transform(frameData);

        var dept = frameData[0];
        var cmd = frameData[1];
        var payload = new byte[frameLen - 2];
        if (payload.Length > 0)
            Buffer.BlockCopy(frameData, 2, payload, 0, payload.Length);

        return new FiestaPacket(dept, cmd, payload);
    }

    /// <summary>
    /// Write a packet to the stream (blocking).
    /// </summary>
    public void WritePacket(FiestaPacket packet)
    {
        var wireBytes = BuildWireBytes(packet);
        _stream.Write(wireBytes, 0, wireBytes.Length);
        _stream.Flush();
    }

    /// <summary>
    /// Write a packet to the stream (async).
    /// </summary>
    public async ValueTask WritePacketAsync(FiestaPacket packet, CancellationToken ct = default)
    {
        var wireBytes = BuildWireBytes(packet);
        await _stream.WriteAsync(wireBytes, ct);
        await _stream.FlushAsync(ct);
    }

    /// <summary>
    /// Convenience: serialize a typed struct body and send it.
    /// </summary>
    public void Send<T>(T body) where T : IFiestaPacketBody
        => WritePacket(FiestaPacket.Create(body));

    /// <summary>
    /// Convenience: serialize a typed struct body and send it (async).
    /// </summary>
    public ValueTask SendAsync<T>(T body, CancellationToken ct = default) where T : IFiestaPacketBody
        => WritePacketAsync(FiestaPacket.Create(body), ct);

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _stream.Dispose();
        }
    }

    private byte[] BuildWireBytes(FiestaPacket packet)
    {
        var opcodeAndPayloadLen = 2 + packet.Payload.Length;

        using var ms = new MemoryStream();

        // Length prefix
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

        // Opcode + payload (cipher applied)
        var frameData = new byte[opcodeAndPayloadLen];
        frameData[0] = packet.Department;
        frameData[1] = packet.Command;
        packet.Payload.Span.CopyTo(frameData.AsSpan(2));
        _cipher.Transform(frameData);

        ms.Write(frameData, 0, frameData.Length);
        return ms.ToArray();
    }

    private byte ReadByteOrThrow()
    {
        var b = _stream.ReadByte();
        if (b < 0) throw new EndOfStreamException("Unexpected end of stream reading packet");
        return (byte)b;
    }

    private async ValueTask<byte> ReadByteAsyncOrThrow(CancellationToken ct)
    {
        var buf = new byte[1];
        var read = await _stream.ReadAsync(buf, ct);
        if (read == 0) throw new EndOfStreamException("Unexpected end of stream reading packet");
        return buf[0];
    }

    private void ReadExact(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            var read = _stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0) throw new EndOfStreamException("Unexpected end of stream reading packet");
            totalRead += read;
        }
    }

    private async ValueTask ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(totalRead), ct);
            if (read == 0) throw new EndOfStreamException("Unexpected end of stream reading packet");
            totalRead += read;
        }
    }
}
