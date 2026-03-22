using FiestaLibReloaded.Networking.Enums;

namespace FiestaLibReloaded.Networking;

/// <summary>
/// Marks a protocol struct with its department and command opcode.
/// Applied by the code generator to structs with known opcodes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class FiestaOpcodeAttribute : Attribute
{
    public ProtocolCommand Department { get; }
    public ushort Command { get; }
    public ushort Opcode => (ushort)(((byte)Department << 8) | Command);

    public FiestaOpcodeAttribute(ProtocolCommand department, ushort command)
    {
        Department = department;
        Command = command;
    }
}
