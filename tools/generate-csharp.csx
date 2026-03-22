// generate-csharp.csx - Generate C# classes from merged PDB extraction JSON.
//
// Usage:
//   dotnet script tools/generate-csharp.csx -- <merged-dir> <output-project-dir>
//
// Generates:
//   - Enum files: ProtocolCommand + per-department opcode enums
//   - Struct files: classes with Read(BinaryReader)/Write(BinaryWriter) methods
//     - Bitfield structs get a backing field + properties
//     - Fixed arrays as T[]
//     - Variable-length trailing arrays as T[] (sized by preceding count field)

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (Args.Count < 2)
{
    Console.WriteLine("Usage: dotnet script tools/generate-csharp.csx -- <merged-dir> <output-project-dir>");
    return;
}

var mergedDir = Args[0];
var outputDir = Args[1];

var enumsFile = Path.Combine(mergedDir, "all-enums.json");
var structsFile = Path.Combine(mergedDir, "all-structs.json");

if (!File.Exists(enumsFile) || !File.Exists(structsFile))
{
    Console.Error.WriteLine("Error: all-enums.json and/or all-structs.json not found.");
    return;
}

var enumsDir = Path.Combine(outputDir, "Enums");
var structsDir = Path.Combine(outputDir, "Structs");
Directory.CreateDirectory(enumsDir);
Directory.CreateDirectory(structsDir);

Console.WriteLine("=== C# Code Generator ===");

// ============================================================
// Helpers
// ============================================================

var CsKeywords = new HashSet<string> {
    "abstract","as","base","bool","break","byte","case","catch","char","checked",
    "class","const","continue","decimal","default","delegate","do","double",
    "else","enum","event","explicit","extern","false","finally","fixed","float",
    "for","foreach","goto","if","implicit","in","int","interface","internal","is",
    "lock","long","namespace","new","null","object","operator","out","override",
    "params","private","protected","public","readonly","ref","return","sbyte",
    "sealed","short","sizeof","stackalloc","static","string","struct","switch",
    "this","throw","true","try","typeof","uint","ulong","unchecked","unsafe",
    "ushort","using","virtual","void","volatile","while"
};

string SafeId(string name)
{
    if (string.IsNullOrEmpty(name)) return "_unnamed";
    name = name.Replace("::", "_").Replace("<", "_").Replace(">", "_")
               .Replace(",", "_").Replace(" ", "_").Replace("-", "_");
    name = name.Trim('_');
    if (string.IsNullOrEmpty(name)) return "_unnamed";
    if (char.IsDigit(name[0])) name = "_" + name;
    if (CsKeywords.Contains(name)) return "@" + name;
    return name;
}

string MapType(string cType)
{
    // Strip bitfield suffix
    var bfm = Regex.Match(cType, @"^(.+):(\d+)$");
    if (bfm.Success) cType = bfm.Groups[1].Value;

    return cType switch
    {
        "char" => "sbyte",
        "unsigned char" or "bool" => "byte",
        "short" => "short",
        "unsigned short" => "ushort",
        "int" => "int",
        "unsigned int" or "unsigned long" => "uint",
        "long" => "int",
        "long long" => "long",
        "unsigned __int64" => "ulong",
        "float" => "float",
        "double" => "double",
        "wchar_t" => "ushort", // UTF-16 code unit; arrays treated as strings
        _ => cType // struct/enum name or unsupported
    };
}

bool IsPointer(string cType) => cType.EndsWith("*");
bool IsVoid(string cType) => cType == "void";

bool IsPrimitive(string csType) => csType is "byte" or "sbyte" or "short" or "ushort"
    or "int" or "uint" or "long" or "ulong" or "float" or "double";

var ReadMethod = new Dictionary<string, string>
{
    ["byte"] = "ReadByte()",
    ["sbyte"] = "ReadSByte()",
    ["short"] = "ReadInt16()",
    ["ushort"] = "ReadUInt16()",
    ["int"] = "ReadInt32()",
    ["uint"] = "ReadUInt32()",
    ["long"] = "ReadInt64()",
    ["ulong"] = "ReadUInt64()",
    ["float"] = "ReadSingle()",
    ["double"] = "ReadDouble()",
};

var TypeSize = new Dictionary<string, int>
{
    ["byte"] = 1, ["sbyte"] = 1,
    ["short"] = 2, ["ushort"] = 2,
    ["int"] = 4, ["uint"] = 4, ["float"] = 4,
    ["long"] = 8, ["ulong"] = 8, ["double"] = 8,
};

(string baseType, int count)? ParseArray(string cType)
{
    var m = Regex.Match(cType, @"^(.+)\[(\d+)\]$");
    if (!m.Success) return null;
    var inner = m.Groups[1].Value;
    var count = int.Parse(m.Groups[2].Value);
    var nested = ParseArray(inner);
    if (nested != null) return (nested.Value.baseType, nested.Value.count * count);
    return (inner, count);
}

bool IsBitfield(string cType) => Regex.IsMatch(cType, @":\d+$");

bool IsSkippable(string cType) =>
    cType.Contains("::") || cType.Contains("<") || cType.Contains("?") ||
    cType.Contains("unnamed") || cType.StartsWith("[");

string ToPascalCase(string name)
{
    if (!name.Contains('_') && !name.All(c => char.IsUpper(c) || char.IsDigit(c)))
        return name;
    var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
    var sb = new StringBuilder();
    foreach (var part in parts)
    {
        sb.Append(char.ToUpper(part[0]));
        if (part.Length > 1) sb.Append(part[1..].ToLower());
    }
    return sb.ToString();
}

// ============================================================
// Generate Enums
// ============================================================
Console.Write("Generating enums... ");

var enumsJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(enumsFile));

// ProtocolCommand enum
{
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated />");
    sb.AppendLine("namespace FiestaLibReloaded.Networking.Enums;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>Protocol department IDs. Full opcode = (DepartmentId &lt;&lt; 8) | OpcodeValue.</summary>");
    sb.AppendLine("public enum ProtocolCommand : byte");
    sb.AppendLine("{");
    foreach (var dept in enumsJson.EnumerateObject())
    {
        var hex = dept.Value.GetProperty("hex").GetString();
        sb.AppendLine($"    {ToPascalCase(dept.Name)} = {hex},");
    }
    sb.AppendLine("}");
    File.WriteAllText(Path.Combine(enumsDir, "ProtocolCommand.cs"), sb.ToString());
}

int enumFileCount = 0;
foreach (var dept in enumsJson.EnumerateObject())
{
    var deptName = dept.Name;
    var deptPascal = ToPascalCase(deptName);
    var opcodes = dept.Value.GetProperty("opcodes");

    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated />");
    sb.AppendLine("namespace FiestaLibReloaded.Networking.Enums;");
    sb.AppendLine();
    sb.AppendLine($"/// <summary>Opcodes for the {deptName} department (0x{dept.Value.GetProperty("id").GetInt32():X2}).</summary>");
    sb.AppendLine($"public enum {deptPascal}Opcode : ushort");
    sb.AppendLine("{");

    foreach (var op in opcodes.EnumerateObject())
    {
        var prefix = $"NC_{deptName}_";
        var member = op.Name.StartsWith(prefix) ? op.Name[prefix.Length..] : op.Name;
        member = ToPascalCase(member);
        member = SafeId(member);
        sb.AppendLine($"    {member} = {op.Value.GetInt32()},");
    }

    sb.AppendLine("}");
    File.WriteAllText(Path.Combine(enumsDir, $"{deptPascal}Opcode.cs"), sb.ToString());
    enumFileCount++;
}

Console.WriteLine($"{enumFileCount + 1} files");

// ============================================================
// Build opcode lookup: struct name -> (deptPascal, deptHex, opcodeValue)
// ============================================================
var opcodeMap = new Dictionary<string, (string DeptPascal, string DeptHex, int OpcodeValue)>();
foreach (var dept in enumsJson.EnumerateObject())
{
    var deptPascal = ToPascalCase(dept.Name);
    var deptHex = dept.Value.GetProperty("hex").GetString();
    var opcodes = dept.Value.GetProperty("opcodes");
    foreach (var op in opcodes.EnumerateObject())
    {
        var structName = "PROTO_" + op.Name;
        opcodeMap[structName] = (deptPascal, deptHex, op.Value.GetInt32());
    }
}
Console.WriteLine($"Opcode map: {opcodeMap.Count} entries");

// ============================================================
// Generate Structs
// ============================================================
Console.Write("Generating structs... ");

var structsJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(structsFile));
var protoStructs = structsJson.GetProperty("protocol_structs");
var refTypes = structsJson.GetProperty("referenced_types");

var knownTypes = new HashSet<string>();
foreach (var s in protoStructs.EnumerateObject()) knownTypes.Add(s.Name);
foreach (var s in refTypes.EnumerateObject()) knownTypes.Add(s.Name);

// --- Field analysis types ---
record ParsedField(
    string Name, string OrigType, string CsType, int Offset, int Size,
    bool IsBase, bool IsBitfield, int BitOffset, int BitCount,
    bool IsArray, string ArrayElemCsType, int ArrayCount, bool IsVarArray,
    bool IsKnownStruct, bool IsSkipped);

List<ParsedField> AnalyzeFields(JsonElement fields)
{
    var result = new List<ParsedField>();

    foreach (var f in fields.EnumerateArray())
    {
        var name = f.GetProperty("name").GetString();
        var type = f.GetProperty("type").GetString();
        var offset = f.GetProperty("offset").GetInt32();
        var size = f.GetProperty("size").GetInt32();
        var isBase = f.TryGetProperty("is_base", out var ib) && ib.GetBoolean();
        var hasBitOff = f.TryGetProperty("bit_offset", out var bo);
        var hasBitCnt = f.TryGetProperty("bit_count", out var bc);

        if (IsSkippable(type) || IsVoid(type) || IsPointer(type))
        {
            result.Add(new ParsedField(name, type, "", offset, size, isBase, false, 0, 0,
                false, "", 0, false, false, true));
            continue;
        }

        // Bitfield
        if (IsBitfield(type))
        {
            var bfm = Regex.Match(type, @"^(.+):(\d+)$");
            var baseType = MapType(bfm.Groups[1].Value);
            var bits = int.Parse(bfm.Groups[2].Value);
            var bitOff = hasBitOff ? bo.GetInt32() : 0;
            result.Add(new ParsedField(name, type, baseType, offset, size, isBase, true,
                bitOff, bits, false, "", 0, false, false, false));
            continue;
        }

        // Array
        var arr = ParseArray(type);
        if (arr != null)
        {
            var (elemType, count) = arr.Value;
            var csElem = MapType(elemType);
            var isKnownElem = knownTypes.Contains(elemType);
            if (isKnownElem) csElem = SafeId(elemType);
            var isVarArray = count == 0;
            var isPrimElem = IsPrimitive(csElem);

            // For non-primitive, non-known arrays: skip
            if (!isPrimElem && !isKnownElem)
            {
                result.Add(new ParsedField(name, type, "", offset, size, isBase, false, 0, 0,
                    true, csElem, count, isVarArray, false, true));
                continue;
            }

            result.Add(new ParsedField(name, type, csElem, offset, size, isBase, false, 0, 0,
                true, csElem, count, isVarArray, isKnownElem, false));
            continue;
        }

        // Known struct
        if (knownTypes.Contains(type))
        {
            result.Add(new ParsedField(name, type, SafeId(type), offset, size, isBase, false, 0, 0,
                false, "", 0, false, true, false));
            continue;
        }

        // Primitive
        var csType = MapType(type);
        if (IsPrimitive(csType))
        {
            result.Add(new ParsedField(name, type, csType, offset, size, isBase, false, 0, 0,
                false, "", 0, false, false, false));
            continue;
        }

        // Enum types or other unmapped: infer from size
        if (size > 0)
        {
            var inferredType = size switch
            {
                1 => "byte",
                2 => "ushort",
                4 => "uint",
                8 => "ulong",
                _ => ""
            };
            if (inferredType != "")
            {
                result.Add(new ParsedField(name, type, inferredType, offset, size, isBase, false, 0, 0,
                    false, "", 0, false, false, false));
                continue;
            }
        }

        // Unknown
        result.Add(new ParsedField(name, type, "", offset, size, isBase, false, 0, 0,
            false, "", 0, false, false, true));
    }

    return result.OrderBy(f => f.Offset).ThenBy(f => f.BitOffset).ToList();
}

// Find count field for a variable-length array
string FindCountField(List<ParsedField> fields, int varArrayIndex)
{
    // Search backwards for a numeric field
    for (int i = varArrayIndex - 1; i >= 0; i--)
    {
        var f = fields[i];
        if (f.IsSkipped || f.IsBitfield || f.IsArray) continue;
        if (!IsPrimitive(f.CsType)) continue;

        var nameLC = f.Name.ToLowerInvariant();
        if (nameLC.Contains("num") || nameLC.Contains("count") || nameLC.Contains("cnt") ||
            nameLC.Contains("size") || nameLC.Contains("length"))
            return SafeId(f.Name);
    }
    // Fallback: use the field immediately before (must be scalar)
    for (int i = varArrayIndex - 1; i >= 0; i--)
    {
        var f = fields[i];
        if (f.IsSkipped || f.IsBitfield || f.IsArray) continue;
        if (IsPrimitive(f.CsType))
            return SafeId(f.Name);
    }
    return null;
}

void WriteClassFile(string filePath, string ns, IEnumerable<(string name, int sizeOf, JsonElement fields)> defs,
    Dictionary<string, (string DeptPascal, string DeptHex, int OpcodeValue)> opcodes)
{
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated />");
    sb.AppendLine("using FiestaLibReloaded.Networking;");
    sb.AppendLine("using FiestaLibReloaded.Networking.Enums;");
    sb.AppendLine();
    sb.AppendLine("namespace " + ns + ";");
    sb.AppendLine();

    foreach (var (structName, sizeOf, rawFields) in defs)
    {
        if (structName.Contains("::") || structName.Contains("<"))
        {
            sb.AppendLine($"// Skipped: {structName} (scoped/templated type)");
            sb.AppendLine();
            continue;
        }

        var csName = SafeId(structName);
        var fields = AnalyzeFields(rawFields);

        // Identify bitfield groups: offset -> list of bitfield fields
        var bitfieldGroups = new Dictionary<int, List<ParsedField>>();
        foreach (var f in fields.Where(f => f.IsBitfield))
        {
            if (!bitfieldGroups.ContainsKey(f.Offset))
                bitfieldGroups[f.Offset] = new List<ParsedField>();
            bitfieldGroups[f.Offset].Add(f);
        }

        // Determine backing type for each bitfield group
        var bitfieldBackingType = new Dictionary<int, string>();
        foreach (var (off, group) in bitfieldGroups)
        {
            // Use the first field's type as backing type
            bitfieldBackingType[off] = group[0].CsType;
        }

        // Find count fields for var-length arrays
        var varArrayCountFields = new Dictionary<int, string>(); // field index -> count field name
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i].IsVarArray && !fields[i].IsSkipped)
            {
                var countField = FindCountField(fields, i);
                if (countField != null)
                    varArrayCountFields[i] = countField;
            }
        }

        // --- Emit class ---
        var hasOpcode = opcodes.TryGetValue(structName, out var opcodeInfo);
        if (hasOpcode)
        {
            var fullOpcode = (int.Parse(opcodeInfo.DeptHex.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber) << 8) | opcodeInfo.OpcodeValue;
            sb.AppendLine($"/// <summary>");
            sb.AppendLine($"/// Department: {opcodeInfo.DeptPascal} ({opcodeInfo.DeptHex}) | Opcode: {opcodeInfo.OpcodeValue} | Full: 0x{fullOpcode:X4}");
            sb.AppendLine($"/// sizeof = {sizeOf}");
            sb.AppendLine($"/// </summary>");
            sb.AppendLine($"[FiestaOpcode(ProtocolCommand.{opcodeInfo.DeptPascal}, {opcodeInfo.OpcodeValue})]");
        }
        else
        {
            sb.AppendLine($"/// <summary>sizeof = {sizeOf}</summary>");
        }
        sb.AppendLine($"public class {csName} : IFiestaPacketBody");
        sb.AppendLine("{");

        // Emit fields
        var emittedBitfieldOffsets = new HashSet<int>();
        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            if (f.IsSkipped)
            {
                sb.AppendLine($"    // {f.OrigType} {f.Name} at offset {f.Offset} (unsupported type)");
                continue;
            }

            if (f.IsBitfield)
            {
                if (emittedBitfieldOffsets.Add(f.Offset))
                {
                    var bt = bitfieldBackingType[f.Offset];
                    // Use uint for all bit manipulation (byte/ushort promote to int in C#)
                    var propType = bt is "ulong" or "long" ? "ulong" : "uint";
                    var backingField = $"_bits_{f.Offset}";
                    sb.AppendLine($"    private {bt} _bits_{f.Offset};");
                    foreach (var bf in bitfieldGroups[f.Offset])
                    {
                        var mask = (1UL << bf.BitCount) - 1;
                        var maskLit = propType == "ulong" ? $"0x{mask:X}ul" : $"0x{mask:X}u";
                        var shift = bf.BitOffset;
                        var propName = SafeId(bf.Name);
                        var cast = bt == propType ? "" : $"({bt})";
                        sb.AppendLine($"    /// <summary>bits [{shift}..{shift + bf.BitCount})</summary>");
                        sb.AppendLine($"    public {propType} {propName}");
                        sb.AppendLine("    {");
                        sb.AppendLine($"        get => (({propType}){backingField} >> {shift}) & {maskLit};");
                        sb.AppendLine($"        set => {backingField} = {cast}(({propType}){backingField} & ~({maskLit} << {shift}) | ((value & {maskLit}) << {shift}));");
                        sb.AppendLine("    }");
                    }
                }
                continue;
            }

            var safeName = SafeId(f.Name);

            if (f.IsBase)
            {
                if (f.IsKnownStruct)
                {
                    sb.AppendLine($"    public {f.CsType} Base = new();");
                }
                else
                {
                    sb.AppendLine($"    // base: {f.OrigType} at offset {f.Offset}");
                }
                continue;
            }

            if (f.IsArray)
            {
                if (f.IsVarArray)
                {
                    if (IsPrimitive(f.ArrayElemCsType))
                        sb.AppendLine($"    public {f.ArrayElemCsType}[] {safeName} = [];");
                    else
                        sb.AppendLine($"    public {f.ArrayElemCsType}[] {safeName} = [];");
                }
                else
                {
                    sb.AppendLine($"    public {f.ArrayElemCsType}[] {safeName} = new {f.ArrayElemCsType}[{f.ArrayCount}];");
                }
                continue;
            }

            if (f.IsKnownStruct)
            {
                sb.AppendLine($"    public {f.CsType} {safeName} = new();");
                continue;
            }

            // Primitive or mapped enum
            sb.AppendLine($"    public {f.CsType} {safeName};");
        }

        sb.AppendLine();

        // --- Emit Read(BinaryReader) ---
        sb.AppendLine("    public void Read(BinaryReader reader)");
        sb.AppendLine("    {");

        var emittedBfRead = new HashSet<int>();
        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            if (f.IsSkipped)
            {
                if (f.Size > 0)
                    sb.AppendLine($"        reader.ReadBytes({f.Size}); // skip {f.OrigType} {f.Name}");
                continue;
            }

            var safeName = SafeId(f.Name);

            if (f.IsBitfield)
            {
                if (emittedBfRead.Add(f.Offset))
                {
                    var bt = bitfieldBackingType[f.Offset];
                    if (ReadMethod.TryGetValue(bt, out var rm))
                        sb.AppendLine($"        _bits_{f.Offset} = reader.{rm};");
                }
                continue;
            }

            if (f.IsBase)
            {
                if (f.IsKnownStruct)
                    sb.AppendLine($"        Base.Read(reader);");
                else if (f.Size > 0)
                    sb.AppendLine($"        reader.ReadBytes({f.Size}); // skip base {f.OrigType}");
                continue;
            }

            if (f.IsArray)
            {
                if (f.IsVarArray)
                {
                    var countField = varArrayCountFields.GetValueOrDefault(i);
                    if (countField != null)
                    {
                        if (IsPrimitive(f.ArrayElemCsType))
                        {
                            if (f.ArrayElemCsType == "byte")
                            {
                                sb.AppendLine($"        {safeName} = reader.ReadBytes({countField});");
                            }
                            else
                            {
                                sb.AppendLine($"        {safeName} = new {f.ArrayElemCsType}[{countField}];");
                                sb.AppendLine($"        for (int i = 0; i < {countField}; i++)");
                                sb.AppendLine($"            {safeName}[i] = reader.{ReadMethod[f.ArrayElemCsType]};");
                            }
                        }
                        else
                        {
                            sb.AppendLine($"        {safeName} = new {f.ArrayElemCsType}[{countField}];");
                            sb.AppendLine($"        for (int i = 0; i < {countField}; i++)");
                            sb.AppendLine("        {");
                            sb.AppendLine($"            {safeName}[i] = new {f.ArrayElemCsType}();");
                            sb.AppendLine($"            {safeName}[i].Read(reader);");
                            sb.AppendLine("        }");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"        // TODO: {safeName} is variable-length but count field not detected");
                    }
                }
                else
                {
                    // Fixed-size array
                    if (IsPrimitive(f.ArrayElemCsType))
                    {
                        if (f.ArrayElemCsType == "byte")
                        {
                            sb.AppendLine($"        {safeName} = reader.ReadBytes({f.ArrayCount});");
                        }
                        else
                        {
                            sb.AppendLine($"        for (int i = 0; i < {f.ArrayCount}; i++)");
                            sb.AppendLine($"            {safeName}[i] = reader.{ReadMethod[f.ArrayElemCsType]};");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"        for (int i = 0; i < {f.ArrayCount}; i++)");
                        sb.AppendLine("        {");
                        sb.AppendLine($"            {safeName}[i] = new {f.ArrayElemCsType}();");
                        sb.AppendLine($"            {safeName}[i].Read(reader);");
                        sb.AppendLine("        }");
                    }
                }
                continue;
            }

            if (f.IsKnownStruct)
            {
                sb.AppendLine($"        {safeName}.Read(reader);");
                continue;
            }

            // Primitive
            if (ReadMethod.TryGetValue(f.CsType, out var readExpr))
                sb.AppendLine($"        {safeName} = reader.{readExpr};");
            else
                sb.AppendLine($"        // Cannot read {f.CsType} {safeName}");
        }

        sb.AppendLine("    }");
        sb.AppendLine();

        // --- Emit Write(BinaryWriter) ---
        sb.AppendLine("    public void Write(BinaryWriter writer)");
        sb.AppendLine("    {");

        var emittedBfWrite = new HashSet<int>();
        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            if (f.IsSkipped)
            {
                if (f.Size > 0)
                    sb.AppendLine($"        writer.Write(new byte[{f.Size}]); // skip {f.OrigType} {f.Name}");
                continue;
            }

            var safeName = SafeId(f.Name);

            if (f.IsBitfield)
            {
                if (emittedBfWrite.Add(f.Offset))
                {
                    sb.AppendLine($"        writer.Write(_bits_{f.Offset});");
                }
                continue;
            }

            if (f.IsBase)
            {
                if (f.IsKnownStruct)
                    sb.AppendLine($"        Base.Write(writer);");
                else if (f.Size > 0)
                    sb.AppendLine($"        writer.Write(new byte[{f.Size}]); // skip base {f.OrigType}");
                continue;
            }

            if (f.IsArray)
            {
                if (f.IsVarArray)
                {
                    if (IsPrimitive(f.ArrayElemCsType))
                    {
                        if (f.ArrayElemCsType == "byte")
                        {
                            sb.AppendLine($"        writer.Write({safeName});");
                        }
                        else
                        {
                            sb.AppendLine($"        for (int i = 0; i < {safeName}.Length; i++)");
                            sb.AppendLine($"            writer.Write({safeName}[i]);");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"        for (int i = 0; i < {safeName}.Length; i++)");
                        sb.AppendLine($"            {safeName}[i].Write(writer);");
                    }
                }
                else
                {
                    if (IsPrimitive(f.ArrayElemCsType))
                    {
                        if (f.ArrayElemCsType == "byte")
                        {
                            sb.AppendLine($"        writer.Write({safeName}, 0, {f.ArrayCount});");
                        }
                        else
                        {
                            sb.AppendLine($"        for (int i = 0; i < {f.ArrayCount}; i++)");
                            sb.AppendLine($"            writer.Write({safeName}[i]);");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"        for (int i = 0; i < {f.ArrayCount}; i++)");
                        sb.AppendLine($"            {safeName}[i].Write(writer);");
                    }
                }
                continue;
            }

            if (f.IsKnownStruct)
            {
                sb.AppendLine($"        {safeName}.Write(writer);");
                continue;
            }

            // Primitive
            sb.AppendLine($"        writer.Write({safeName});");
        }

        sb.AppendLine("    }");

        // Special handling for C struct tm: add DateTime convenience property
        if (structName == "tm")
        {
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Convenience property that converts to/from the raw tm fields.</summary>");
            sb.AppendLine("    public DateTime DateTime");
            sb.AppendLine("    {");
            sb.AppendLine("        get => new(tm_year + 1900, tm_mon + 1, tm_mday, tm_hour, tm_min, tm_sec);");
            sb.AppendLine("        set");
            sb.AppendLine("        {");
            sb.AppendLine("            tm_sec = value.Second;");
            sb.AppendLine("            tm_min = value.Minute;");
            sb.AppendLine("            tm_hour = value.Hour;");
            sb.AppendLine("            tm_mday = value.Day;");
            sb.AppendLine("            tm_mon = value.Month - 1;");
            sb.AppendLine("            tm_year = value.Year - 1900;");
            sb.AppendLine("            tm_wday = (int)value.DayOfWeek;");
            sb.AppendLine("            tm_yday = value.DayOfYear - 1;");
            sb.AppendLine("            tm_isdst = -1;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    File.WriteAllText(filePath, sb.ToString());
}

// Group protocol structs by department
var deptGroups = new Dictionary<string, List<(string name, int sizeOf, JsonElement fields)>>();
foreach (var s in protoStructs.EnumerateObject())
{
    var name = s.Value.GetProperty("Name").GetString();
    var sizeOf = s.Value.GetProperty("SizeOf").GetInt32();
    var dept = s.Value.GetProperty("department").GetString();
    var fields = s.Value.GetProperty("fields");
    if (!deptGroups.ContainsKey(dept))
        deptGroups[dept] = new List<(string, int, JsonElement)>();
    deptGroups[dept].Add((name, sizeOf, fields));
}

int structFileCount = 0;
foreach (var (dept, structList) in deptGroups.OrderBy(kv => kv.Key))
{
    WriteClassFile(
        Path.Combine(structsDir, $"{ToPascalCase(dept)}.cs"),
        "FiestaLibReloaded.Networking.Structs",
        structList.OrderBy(s => s.name),
        opcodeMap
    );
    structFileCount++;
}

// Helper/shared types
var helperList = new List<(string name, int sizeOf, JsonElement fields)>();
foreach (var t in refTypes.EnumerateObject())
{
    var name = t.Value.GetProperty("Name").GetString();
    var sizeOf = t.Value.GetProperty("SizeOf").GetInt32();
    var fields = t.Value.GetProperty("fields");
    helperList.Add((name, sizeOf, fields));
}

// Shared types don't have opcodes, pass empty map
var emptyOpcodeMap = new Dictionary<string, (string DeptPascal, string DeptHex, int OpcodeValue)>();
WriteClassFile(
    Path.Combine(structsDir, "SharedTypes.cs"),
    "FiestaLibReloaded.Networking.Structs",
    helperList.OrderBy(s => s.name),
    emptyOpcodeMap
);
structFileCount++;

Console.WriteLine($"{structFileCount} files");
Console.WriteLine();
Console.WriteLine("=== Done ===");
Console.WriteLine($"  Enums:   {enumFileCount + 1} files");
Console.WriteLine($"  Structs: {structFileCount} files ({deptGroups.Values.Sum(g => g.Count)} protocol + {helperList.Count} helper)");

// ============================================================
// Generate PacketRegistry.cs
// ============================================================
Console.Write("Generating PacketRegistry... ");

{
    // Collect all protocol structs that have a matched opcode
    var registryEntries = new List<(string csName, string deptPascal, string deptHex, int opcodeValue)>();
    foreach (var s in protoStructs.EnumerateObject())
    {
        var name = s.Value.GetProperty("Name").GetString();
        if (name.Contains("::") || name.Contains("<")) continue;
        if (opcodeMap.TryGetValue(name, out var info))
        {
            registryEntries.Add((SafeId(name), info.DeptPascal, info.DeptHex, info.OpcodeValue));
        }
    }

    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated />");
    sb.AppendLine("#nullable enable");
    sb.AppendLine("using FiestaLibReloaded.Networking.Structs;");
    sb.AppendLine();
    sb.AppendLine("namespace FiestaLibReloaded.Networking;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Auto-generated registry mapping packet types to opcodes and vice versa.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("public static class PacketRegistry");
    sb.AppendLine("{");
    sb.AppendLine("    private static readonly Dictionary<Type, ushort> TypeToOpcode = new()");
    sb.AppendLine("    {");
    foreach (var e in registryEntries.OrderBy(e => e.csName))
    {
        var fullOpcode = (int.Parse(e.deptHex.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber) << 8) | e.opcodeValue;
        sb.AppendLine($"        [typeof({e.csName})] = 0x{fullOpcode:X4},");
    }
    sb.AppendLine("    };");
    sb.AppendLine();
    sb.AppendLine("    private static readonly Dictionary<ushort, Type> OpcodeToType = new()");
    sb.AppendLine("    {");
    foreach (var e in registryEntries.OrderBy(e => e.csName))
    {
        var fullOpcode = (int.Parse(e.deptHex.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber) << 8) | e.opcodeValue;
        sb.AppendLine($"        [0x{fullOpcode:X4}] = typeof({e.csName}),");
    }
    sb.AppendLine("    };");
    sb.AppendLine();
    sb.AppendLine("    /// <summary>Get the opcode for a packet body type.</summary>");
    sb.AppendLine("    public static ushort GetOpcode<T>() where T : IFiestaPacketBody");
    sb.AppendLine("        => TypeToOpcode.TryGetValue(typeof(T), out var opcode)");
    sb.AppendLine("            ? opcode");
    sb.AppendLine("            : throw new InvalidOperationException($\"No opcode registered for {typeof(T).Name}\");");
    sb.AppendLine();
    sb.AppendLine("    /// <summary>Get the opcode for a packet body type.</summary>");
    sb.AppendLine("    public static ushort GetOpcode(Type type)");
    sb.AppendLine("        => TypeToOpcode.TryGetValue(type, out var opcode)");
    sb.AppendLine("            ? opcode");
    sb.AppendLine("            : throw new InvalidOperationException($\"No opcode registered for {type.Name}\");");
    sb.AppendLine();
    sb.AppendLine("    /// <summary>Get the packet body type for an opcode.</summary>");
    sb.AppendLine("    public static Type? GetType(ushort opcode)");
    sb.AppendLine("        => OpcodeToType.GetValueOrDefault(opcode);");
    sb.AppendLine();
    sb.AppendLine("    /// <summary>Try to get the opcode for a packet body type.</summary>");
    sb.AppendLine("    public static bool TryGetOpcode<T>(out ushort opcode) where T : IFiestaPacketBody");
    sb.AppendLine("        => TypeToOpcode.TryGetValue(typeof(T), out opcode);");
    sb.AppendLine();
    sb.AppendLine("    /// <summary>Try to get the packet body type for an opcode.</summary>");
    sb.AppendLine("    public static bool TryGetType(ushort opcode, out Type? type)");
    sb.AppendLine("    {");
    sb.AppendLine("        var found = OpcodeToType.TryGetValue(opcode, out type);");
    sb.AppendLine("        return found;");
    sb.AppendLine("    }");
    sb.AppendLine("}");

    File.WriteAllText(Path.Combine(outputDir, "PacketRegistry.cs"), sb.ToString());
    Console.WriteLine($"{registryEntries.Count} entries");
}
