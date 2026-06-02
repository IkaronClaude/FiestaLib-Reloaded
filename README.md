# FiestaLib-Reloaded

A modern **.NET library for the Fiesta Online network protocol** — wire framing,
opcodes, typed packet bodies, dispatch, and the cipher abstraction. It's the
protocol layer underneath [ik-fiesta-proxy](https://github.com/IkaronClaude/ik-fiesta-proxy),
and it's standalone enough to build any other Fiesta tooling (sniffers, bots,
emulators, test clients) on top of.

Open source, cross-platform (Linux / Windows / macOS, anywhere .NET runs).
**No copyrighted game content is included** — the cipher *table* in particular
is bring-your-own; this library ships only the cipher *interface*.

## Lineage

Descends from the **OPTool-Reloaded** family. A practical consequence worth
knowing: server-to-server traffic uses the same length-prefixed framing as
client traffic but is **unencrypted** — no XOR, no `SEED_ACK` handshake — which
is why s2s tooling never needs a cipher table.

## What's in the box

Two projects (see `FiestaLibReloaded.slnx`):

### `FiestaLibReloaded.Networking`

The protocol core.

- **Framing** — `FiestaPacket` is the wire unit: a length prefix, a little-endian
  `ushort` opcode (6-bit department + 10-bit command), and the payload. The
  length prefix is **1 byte for 1–254**, or **`0x00` followed by a 2-byte
  little-endian length** for larger frames. (`0x00` is reserved as the extension
  marker so it can never appear as an inline length — getting this LE order right
  is what unblocked extended-length s2s frames.)
- **Typed bodies** — every protocol struct implements `IFiestaPacketBody`
  (`Read`/`Write` over `BinaryReader`/`BinaryWriter`). Build a packet from a
  struct with `FiestaPacket.Create<T>(body)`, read one back with
  `packet.ReadBody<T>()`.
- **Opcodes** — `[FiestaOpcode(department, command)]` tags each struct;
  `PacketRegistry` maps types ⇄ opcodes. Per-department opcode enums live under
  `Enums/`, the struct definitions under `Structs/`.
- **Dispatch** — subclass `FiestaDispatcher`, register handlers with `On<T>(...)`,
  route incoming packets with `TryDispatch(packet)`. Unhandled opcodes go to an
  overridable `OnUnhandled`.
- **Cipher** — `IFiestaStreamCipher` is a single symmetric `Transform(Span<byte>)`
  (XOR is its own inverse, so one method covers encrypt and decrypt).
  `NullCipher` is the no-op used for unencrypted s2s. The concrete XOR
  implementation is fed a bring-your-own table by the consumer (e.g. fiesta-proxy
  via `XOR_TABLE_HEX` / `XOR_TABLE_PATH`); **no table is shipped here**.

### `FiestaLibReloaded.Config`

Parsing for Fiesta's `ServerInfo.txt` family — `ServerInfoParser`,
`ServerInfoEntry`, `FiestaServerType` — so tools can read the server topology
the game itself uses.

## Protocol reference

`docs/extracted/` holds machine-extracted opcode/struct/enum dumps per server
(`Login`, `WorldManager`, `Zone`, `Account`, `AccountLog`) plus a merged view —
handy when adding or verifying a packet definition.

## Build

```bash
dotnet build FiestaLibReloaded.slnx
```

Targets a current .NET SDK; reference `FiestaLibReloaded.Networking` from your
own project to use it.

## Status

`Tracker.md` tracks in-progress work (e.g. read-length-mismatch detection to
catch struct readers that drift from the actual payload).
