namespace FiestaLibReloaded.Networking;

/// <summary>
/// Dispatches incoming packets to registered handlers by opcode.
/// </summary>
public interface IFiestaDispatcher
{
    bool TryDispatch(FiestaPacket packet);
}
