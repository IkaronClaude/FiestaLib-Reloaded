using System.Text;

namespace FiestaLibReloaded.Shn;

/// <summary>The data type of a SHN column, mapped from the file's raw on-disk type code.</summary>
public enum ShnColumnType { Byte, SByte, Int16, UInt16, Int32, UInt32, UInt64, Float, String }

/// <summary>One column of a SHN table: its <see cref="Name"/>, mapped <see cref="Type"/>,
/// byte <see cref="Length"/>, and the raw on-disk <see cref="TypeCode"/>.</summary>
public sealed record ShnColumn(string Name, ShnColumnType Type, int Length, uint TypeCode);

/// <summary>
/// A decoded Shine data table (<c>.shn</c>) — the client-side game-data format. Reads
/// anything a real client reads (ItemInfo, ActiveSkill, ClassName, MapInfo, …). BYO:
/// point it at a client <c>ressystem</c> file the operator supplies at runtime; the repo
/// ships no game data. (Server-only tables — <c>NPC.txt</c>, <c>*Server.shn</c> — are a
/// different, gated source and are deliberately NOT what this is for.)
///
/// <para>File layout: a 32-byte crypt header, then a <c>u32</c> total file length, then
/// the body XOR'd with the Fiesta data cipher (symmetric, rolling key derived from
/// position — the same routine the Zone uses as <c>CDataReader::Encription</c>, and that
/// the [1801] anti-cheat checksum is built on). The decrypted body is
/// <c>[header u32][recordCount u32][defaultRecordLength u32][columnCount u32]</c>, then
/// the column definitions (48-byte name + <c>u32</c> type + <c>i32</c> length), then the
/// rows (each prefixed with a <c>u16</c> row length). Strings are EUC-KR (code page 949).</para>
///
/// <para>Read-only; a dependency-free port of the proven ik-fiesta-collab SHN reader.</para>
/// </summary>
public sealed class ShnTable
{
    /// <summary>The table name (the file name without extension, e.g. "ActiveSkill").</summary>
    public string Name { get; }

    /// <summary>The columns, in on-disk order.</summary>
    public IReadOnlyList<ShnColumn> Columns { get; }

    /// <summary>The rows: each a column-name → value map. Value runtime types follow the
    /// column type (<c>byte</c>/<c>sbyte</c>/<c>short</c>/<c>ushort</c>/<c>int</c>/
    /// <c>uint</c>/<c>ulong</c>/<c>float</c>/<c>string</c>).</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; }

    private ShnTable(string name, IReadOnlyList<ShnColumn> columns,
                     IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        Name = name;
        Columns = columns;
        Rows = rows;
    }

    private static readonly Encoding EucKr;

    static ShnTable()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        EucKr = Encoding.GetEncoding(949);
    }

    /// <summary>Load and decode a SHN file from disk.</summary>
    public static ShnTable Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Decode a SHN table from a stream, naming it <paramref name="name"/>.</summary>
    public static ShnTable Read(Stream stream, string name)
    {
        using var reader = new BinaryReader(stream, EucKr, leaveOpen: true);
        reader.ReadBytes(32);                       // crypt header (not needed to read)
        int dataLength = reader.ReadInt32() - 36;   // body length after the 36-byte preamble
        if (dataLength < 0) throw new InvalidDataException($"SHN '{name}': invalid data length");
        var body = reader.ReadBytes(dataLength);
        Decrypt(body);                              // symmetric data cipher, in place

        using var ms = new MemoryStream(body);
        using var r = new BinaryReader(ms, EucKr, leaveOpen: true);
        r.ReadUInt32();                             // header
        uint recordCount = r.ReadUInt32();
        uint defaultRecordLength = r.ReadUInt32();
        uint columnCount = r.ReadUInt32();

        var columns = ReadColumns(r, columnCount);
        ValidateRecordLength(columns, defaultRecordLength, name);
        var rows = ReadRows(r, columns, recordCount);
        return new ShnTable(name, columns, rows);
    }

    /// <summary>First row whose <paramref name="column"/> equals <paramref name="value"/>
    /// (numeric compare, type-agnostic), or null. Handy for keyed lookups like a skill or
    /// item id.</summary>
    public IReadOnlyDictionary<string, object?>? FindByLong(string column, long value)
    {
        foreach (var row in Rows)
            if (row.TryGetValue(column, out var v) && v is not null && TryToLong(v, out var l) && l == value)
                return row;
        return null;
    }

    /// <summary>Coerce a SHN cell to <see cref="long"/> (any integral column type), or
    /// false if it isn't an integer-typed value.</summary>
    public static bool TryToLong(object? value, out long result)
    {
        switch (value)
        {
            case byte b: result = b; return true;
            case sbyte sb: result = sb; return true;
            case short s: result = s; return true;
            case ushort us: result = us; return true;
            case int i: result = i; return true;
            case uint ui: result = ui; return true;
            case long l: result = l; return true;
            case ulong ul: result = (long)ul; return true;
            default: result = 0; return false;
        }
    }

    // ── decode internals (mirrors the on-disk format / the collab reader) ──────────

    private static void Decrypt(byte[] b)
    {
        // Symmetric Fiesta data cipher: backwards walk, rolling key seeded by length,
        // mixing position*11, (i&0xF)+0x55, the prior key, and 0xAA. Same algorithm as
        // the bot's Zone Encryption.Apply and collab's ShnCrypto — applying it twice is
        // the identity, so this one pass decrypts a freshly-read encrypted body.
        int n = b.Length;
        byte key = (byte)n;
        for (int i = n - 1; i >= 0; i--)
        {
            b[i] ^= key;
            byte a = (byte)((i & 0x0F) + 0x55);
            a ^= (byte)(i * 11);
            a ^= key;
            a ^= 0xAA;
            key = a;
        }
    }

    private static List<ShnColumn> ReadColumns(BinaryReader reader, uint count)
    {
        var columns = new List<ShnColumn>((int)count);
        int unkCount = 0;
        for (int i = 0; i < count; i++)
        {
            string name = ReadPaddedString(reader, 48);
            uint typeCode = reader.ReadUInt32();
            int length = reader.ReadInt32();
            string columnName = string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2
                ? $"Undefined{unkCount++}"
                : name;
            columns.Add(new ShnColumn(columnName, MapType(typeCode), length, typeCode));
        }
        return columns;
    }

    private static void ValidateRecordLength(List<ShnColumn> columns, uint defaultRecordLength, string name)
    {
        uint computed = 2; // u16 row-length prefix
        foreach (var c in columns) computed += (uint)c.Length;
        if (computed != defaultRecordLength)
            throw new InvalidDataException(
                $"SHN '{name}': computed record length {computed} != declared {defaultRecordLength}");
    }

    private static List<IReadOnlyDictionary<string, object?>> ReadRows(
        BinaryReader reader, List<ShnColumn> columns, uint recordCount)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>((int)recordCount);
        for (uint i = 0; i < recordCount; i++)
        {
            reader.ReadUInt16(); // row length prefix (fixed cols are self-describing)
            var row = new Dictionary<string, object?>(columns.Count);
            foreach (var col in columns)
            {
                object value = col.TypeCode switch
                {
                    1 or 12 or 16 => reader.ReadByte(),
                    2 => reader.ReadUInt16(),
                    3 or 11 or 18 or 27 => reader.ReadUInt32(),
                    5 => reader.ReadSingle(),
                    9 or 10 or 24 => ReadPaddedString(reader, col.Length),
                    13 or 21 => reader.ReadInt16(),
                    20 => reader.ReadSByte(),
                    22 => reader.ReadInt32(),
                    26 => ReadNullTerminatedString(reader),
                    29 => reader.ReadUInt64(),
                    _ => throw new InvalidDataException($"Unknown SHN column type {col.TypeCode}")
                };
                row[col.Name] = value;
            }
            rows.Add(row);
        }
        return rows;
    }

    private static ShnColumnType MapType(uint typeCode) => typeCode switch
    {
        1 or 12 or 16 => ShnColumnType.Byte,
        2 => ShnColumnType.UInt16,
        3 or 11 or 18 or 27 => ShnColumnType.UInt32,
        5 => ShnColumnType.Float,
        9 or 10 or 24 or 26 => ShnColumnType.String,
        13 or 21 => ShnColumnType.Int16,
        20 => ShnColumnType.SByte,
        22 => ShnColumnType.Int32,
        29 => ShnColumnType.UInt64,
        _ => throw new InvalidDataException($"Unknown SHN type code {typeCode}")
    };

    private static string ReadPaddedString(BinaryReader reader, int length)
    {
        var buffer = reader.ReadBytes(length);
        int end = 0;
        while (end < length && buffer[end] != 0x00) end++;
        return end > 0 ? EucKr.GetString(buffer, 0, end) : string.Empty;
    }

    private static string ReadNullTerminatedString(BinaryReader reader)
    {
        long start = reader.BaseStream.Position;
        while (reader.ReadByte() != 0x00) { }
        int len = (int)(reader.BaseStream.Position - start - 1);
        if (len <= 0) return string.Empty;
        reader.BaseStream.Position = start;
        var result = EucKr.GetString(reader.ReadBytes(len));
        reader.ReadByte(); // consume the terminator
        return result;
    }
}
