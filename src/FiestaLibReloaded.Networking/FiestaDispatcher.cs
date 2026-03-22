namespace FiestaLibReloaded.Networking;

/// <summary>
/// Base class for packet dispatchers. Register handlers with On&lt;T&gt;(),
/// then call TryDispatch() to route incoming packets.
/// </summary>
public abstract class FiestaDispatcher : IFiestaDispatcher
{
    private readonly Dictionary<ushort, Action<FiestaPacket>> _handlers = new();

    /// <summary>
    /// Register a handler for a specific packet type. The opcode is auto-resolved
    /// from PacketRegistry.
    /// </summary>
    protected void On<T>(Action<T> handler) where T : IFiestaPacketBody, new()
    {
        var opcode = PacketRegistry.GetOpcode<T>();
        _handlers[opcode] = packet => handler(packet.ReadBody<T>());
    }

    /// <summary>
    /// Dispatch an incoming packet to a registered handler.
    /// Returns true if a handler was found and invoked.
    /// </summary>
    public bool TryDispatch(FiestaPacket packet)
    {
        if (_handlers.TryGetValue(packet.Opcode, out var handler))
        {
            handler(packet);
            return true;
        }

        OnUnhandled(packet);
        return false;
    }

    /// <summary>
    /// Called when no handler is registered for a packet's opcode.
    /// Override for logging/debugging.
    /// </summary>
    protected virtual void OnUnhandled(FiestaPacket packet) { }
}
