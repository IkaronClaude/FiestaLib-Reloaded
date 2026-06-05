// Hand-written — NOT part of the generated PDB extraction (kept in its own file
// so the code generator never clobbers it).
//
// NC_ACT_NOTICE_REQ is the "GM Say" / server announcement. OpTool (or a client /
// zone) sends it to WorldManager, which broadcasts NC_ACT_NOTICE_CMD to every
// connected client. WM handler: CParserOPTool::fc_NC_ACT_NOTICE_REQ. The
// broadcast is world-wide — there is no per-zone target field.
//
// The extraction only contains PROTO_NC_ACT_NOTICE_CMD_SEND, which embeds a
// PROTO_NC_ACT_CHAT_REQ as its `cmd` field, so the REQ body shares the chat
// layout: { itemLinkDataCount, len, content[len] }. For a plain notice
// itemLinkDataCount is 0 and content is the message text (EUC-KR / cp949).
//
// Department ACT (0x08), command 16 -> opcode (0x08 << 10) | 16 = 0x2010. It is
// not in the generated PacketRegistry, so callers build the packet with the
// Opcode constant below instead of FiestaPacket.Create<T>().

using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Enums;

namespace FiestaLibReloaded.Networking.Structs;

[FiestaOpcode(ProtocolCommand.Act, 16)]
public class PROTO_NC_ACT_NOTICE_REQ : IFiestaPacketBody
{
    /// <summary>Wire opcode: (ACT 0x08 &lt;&lt; 10) | 16.</summary>
    public const ushort Opcode = 0x2010;

    public byte itemLinkDataCount;
    public byte len;
    public byte[] content = [];

    public void Read(BinaryReader reader)
    {
        itemLinkDataCount = reader.ReadByte();
        len = reader.ReadByte();
        content = reader.ReadBytes(len);
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(itemLinkDataCount);
        writer.Write(len);
        writer.Write(content);
    }
}
