// extract-pdb-protocol.csx - Extract all protocol enums and structs from a PDB type dump.
//
// Usage:
//   dotnet script tools/extract-pdb-protocol.csx -- <pdb-dump-file> <output-dir>
//
// Single-pass parser: reads the entire dump once, builds hash-indexed lookup
// tables, then resolves struct fields recursively.
//
// The input file should be created with:
//   llvm-pdbutil dump -types <file.pdb> > dump.txt
//
// Output: all-enums.json, all-structs.json (JSON only)

using System.Text.Json;
using System.Text.RegularExpressions;

// --- Built-in PDB type index -> C type name ---
var BuiltinTypes = new Dictionary<int, string>
{
    [0x0003] = "void",
    [0x0010] = "char",
    [0x0011] = "short",
    [0x0012] = "long",
    [0x0013] = "long long",
    [0x0020] = "unsigned char",
    [0x0021] = "unsigned short",
    [0x0022] = "unsigned long",
    [0x0023] = "unsigned __int64",
    [0x0030] = "bool",
    [0x0040] = "float",
    [0x0041] = "double",
    [0x0070] = "char",
    [0x0071] = "wchar_t",
    [0x0074] = "int",
    [0x0075] = "unsigned int",
    [0x0403] = "void*",
    [0x0410] = "char*",
    [0x0474] = "int*",
};

var BuiltinSizes = new Dictionary<int, int>
{
    [0x0010] = 1, [0x0020] = 1, [0x0030] = 1, [0x0070] = 1,
    [0x0011] = 2, [0x0021] = 2, [0x0071] = 2,
    [0x0012] = 4, [0x0022] = 4, [0x0074] = 4, [0x0075] = 4, [0x0040] = 4,
    [0x0013] = 8, [0x0023] = 8, [0x0041] = 8,
    [0x0403] = 4, [0x0410] = 4, [0x0474] = 4,
};

// --- Data structures ---
record EnumEntry(string Name, int Value);
record FieldMember(string Name, string TypeIdx, string TypeName, int Offset, bool IsBase = false);
record FieldList(List<FieldMember> Members, List<EnumEntry> Enumerates);
record StructDef(string Name, int SizeOf, string FieldListIdx, string ForwardRef);
record ArrayDef(int Size, string ElementType);
record ModifierDef(string Referent, string Modifier);
record PointerDef(string Referent);
record BitfieldDef(string BaseType, int BitOffset, int BitCount);

// --- Global indexes (populated in single pass) ---
var fieldlists = new Dictionary<string, FieldList>();
var structs = new Dictionary<string, StructDef>();
var enums = new Dictionary<string, (string Name, string FieldListIdx)>();
var arrays = new Dictionary<string, ArrayDef>();
var modifiers = new Dictionary<string, ModifierDef>();
var pointers = new Dictionary<string, PointerDef>();
var bitfields = new Dictionary<string, BitfieldDef>();

// --- Parse args ---
if (Args.Count < 2)
{
    Console.WriteLine("Usage: dotnet script tools/extract-pdb-protocol.csx -- <pdb-dump-file> <output-dir>");
    return;
}

var inputFile = Args[0];
var outputDir = Args[1];

if (!File.Exists(inputFile))
{
    Console.Error.WriteLine($"Error: Input file not found: {inputFile}");
    return;
}

Directory.CreateDirectory(outputDir);

Console.WriteLine("=== PDB Protocol Extractor ===");
Console.WriteLine($"Input:  {inputFile}");
Console.WriteLine($"Output: {outputDir}");

// --- Step 0: Read and clean input ---
Console.Write("Reading input... ");
var rawBytes = File.ReadAllBytes(inputFile);

string text;
if (rawBytes.Take(100).Any(b => b == 0))
{
    var cleaned = rawBytes.Where(b => b != 0 && b != '\r').ToArray();
    text = System.Text.Encoding.UTF8.GetString(cleaned);
}
else
{
    text = System.Text.Encoding.UTF8.GetString(rawBytes).Replace("\r", "");
}

var lines = text.Split('\n');
Console.WriteLine($"{lines.Length} lines");

// --- Step 1: Single-pass parse ---
Console.Write("Parsing... ");

var reTypeHeader = new Regex(@"^\s*(0x[0-9A-Fa-f]+)\s*\|\s*(\w+)\s*\[size\s*=\s*(\d+)\]\s*(?:`([^`]*)`)?");
var reFieldlistHeader = new Regex(@"^\s*(0x[0-9A-Fa-f]+)\s*\|\s*LF_FIELDLIST\s*\[size\s*=\s*(\d+)\]");
var reEnumerate = new Regex(@"LF_ENUMERATE\s*\[(\w+)\s*=\s*(\d+)\]");
var reMember = new Regex(@"LF_MEMBER\s*\[name\s*=\s*`([^`]*)`.*?Type\s*=\s*(0x[0-9A-Fa-f]+)(?:\s*\(([^)]*)\))?.*?offset\s*=\s*(\d+)");
var reBclass = new Regex(@"LF_BCLASS.*?type\s*=\s*(0x[0-9A-Fa-f]+).*?offset\s*=\s*(\d+)");
var reFieldListRef = new Regex(@"field list:\s*(0x[0-9A-Fa-f]+)");
var reSizeof = new Regex(@"sizeof\s+(\d+)");
var reForwardRef = new Regex(@"forward ref\s*\((?:->|<-)\s*(0x[0-9A-Fa-f]+)\)");
var reArray = new Regex(@"size:\s*(\d+).*?element type:\s*(0x[0-9A-Fa-f]+)");
var reModifier = new Regex(@"referent\s*=\s*(0x[0-9A-Fa-f]+).*?modifiers\s*=\s*(\w+)");
var rePointer = new Regex(@"referent\s*=\s*(0x[0-9A-Fa-f]+)");
var reBitfield = new Regex(@"type\s*=\s*(0x[0-9A-Fa-f]+).*?bit offset\s*=\s*(\d+).*?#\s*bits\s*=\s*(\d+)");

string currentFlIdx = null;
var currentFlMembers = new List<FieldMember>();
var currentFlEnumerates = new List<EnumEntry>();

void SaveCurrentFieldList()
{
    if (currentFlIdx != null)
    {
        fieldlists[currentFlIdx] = new FieldList(
            new List<FieldMember>(currentFlMembers),
            new List<EnumEntry>(currentFlEnumerates)
        );
        currentFlIdx = null;
        currentFlMembers.Clear();
        currentFlEnumerates.Clear();
    }
}

int i = 0;
while (i < lines.Length)
{
    var line = lines[i];
    i++;

    var flm = reFieldlistHeader.Match(line);
    if (flm.Success)
    {
        SaveCurrentFieldList();
        currentFlIdx = flm.Groups[1].Value.ToLower();
        continue;
    }

    var m = reTypeHeader.Match(line);
    if (m.Success)
    {
        SaveCurrentFieldList();

        var idx = m.Groups[1].Value.ToLower();
        var kind = m.Groups[2].Value;
        var name = m.Groups[4].Success ? m.Groups[4].Value : "";

        if (kind == "LF_STRUCTURE" || kind == "LF_CLASS" || kind == "LF_UNION")
        {
            string flRef = null, fwdRef = null;
            int sz = 0;

            while (i < lines.Length)
            {
                var subline = lines[i];
                if (reTypeHeader.IsMatch(subline) || reFieldlistHeader.IsMatch(subline)) break;

                var fm = reForwardRef.Match(subline);
                if (fm.Success) fwdRef = fm.Groups[1].Value.ToLower();

                var fl = reFieldListRef.Match(subline);
                if (fl.Success && fl.Groups[1].Value.ToLower() != "0x0000")
                    flRef = fl.Groups[1].Value.ToLower();

                var sm = reSizeof.Match(subline);
                if (sm.Success) sz = int.Parse(sm.Groups[1].Value);

                i++;
            }

            if (fwdRef == null && flRef != null)
                structs[idx] = new StructDef(name, sz, flRef, null);
            else if (fwdRef != null)
                structs[idx] = new StructDef(name, 0, null, fwdRef);

            continue;
        }
        else if (kind == "LF_ENUM")
        {
            string flRef = null;
            while (i < lines.Length)
            {
                var subline = lines[i];
                if (reTypeHeader.IsMatch(subline) || reFieldlistHeader.IsMatch(subline)) break;

                var fl = reFieldListRef.Match(subline);
                if (fl.Success && fl.Groups[1].Value.ToLower() != "0x0000")
                    flRef = fl.Groups[1].Value.ToLower();
                i++;
            }
            enums[idx] = (name, flRef);
            continue;
        }
        else if (kind == "LF_ARRAY")
        {
            int arrSize = 0;
            string elemType = null;

            var am = reArray.Match(line);
            if (am.Success)
            {
                arrSize = int.Parse(am.Groups[1].Value);
                elemType = am.Groups[2].Value.ToLower();
            }
            else
            {
                while (i < lines.Length)
                {
                    var subline = lines[i];
                    if (reTypeHeader.IsMatch(subline) || reFieldlistHeader.IsMatch(subline)) break;
                    am = reArray.Match(subline);
                    if (am.Success)
                    {
                        arrSize = int.Parse(am.Groups[1].Value);
                        elemType = am.Groups[2].Value.ToLower();
                    }
                    i++;
                }
            }
            if (elemType != null)
                arrays[idx] = new ArrayDef(arrSize, elemType);
            continue;
        }
        else if (kind == "LF_MODIFIER")
        {
            var mm = reModifier.Match(line);
            if (!mm.Success && i < lines.Length)
            {
                mm = reModifier.Match(lines[i]);
                i++;
            }
            if (mm.Success)
                modifiers[idx] = new ModifierDef(mm.Groups[1].Value.ToLower(), mm.Groups[2].Value);
            continue;
        }
        else if (kind == "LF_POINTER")
        {
            var pm = rePointer.Match(line);
            if (!pm.Success && i < lines.Length)
            {
                pm = rePointer.Match(lines[i]);
                i++;
            }
            if (pm.Success)
                pointers[idx] = new PointerDef(pm.Groups[1].Value.ToLower());
            continue;
        }
        else if (kind == "LF_BITFIELD")
        {
            // Format: type = 0x0022 (unsigned long), bit offset = 0, # bits = 8
            var bfm = reBitfield.Match(line);
            if (!bfm.Success && i < lines.Length)
            {
                bfm = reBitfield.Match(lines[i]);
                i++;
            }
            if (bfm.Success)
                bitfields[idx] = new BitfieldDef(
                    bfm.Groups[1].Value.ToLower(),
                    int.Parse(bfm.Groups[2].Value),
                    int.Parse(bfm.Groups[3].Value));
            continue;
        }
        else
        {
            while (i < lines.Length && !reTypeHeader.IsMatch(lines[i]) && !reFieldlistHeader.IsMatch(lines[i]))
                i++;
            continue;
        }
    }

    // Inside a fieldlist
    if (currentFlIdx != null)
    {
        var em = reEnumerate.Match(line);
        if (em.Success)
        {
            currentFlEnumerates.Add(new EnumEntry(em.Groups[1].Value, int.Parse(em.Groups[2].Value)));
            continue;
        }

        var mm = reMember.Match(line);
        if (mm.Success)
        {
            currentFlMembers.Add(new FieldMember(
                mm.Groups[1].Value,
                mm.Groups[2].Value.ToLower(),
                mm.Groups[3].Success ? mm.Groups[3].Value : null,
                int.Parse(mm.Groups[4].Value)
            ));
            continue;
        }

        var bm = reBclass.Match(line);
        if (bm.Success)
        {
            currentFlMembers.Add(new FieldMember(
                "__base",
                bm.Groups[1].Value.ToLower(),
                null,
                int.Parse(bm.Groups[2].Value),
                IsBase: true
            ));
            continue;
        }
    }
}

SaveCurrentFieldList();

Console.WriteLine("done.");
Console.WriteLine($"  {fieldlists.Count} fieldlists, {structs.Count} structs, {enums.Count} enums, {arrays.Count} arrays, {bitfields.Count} bitfields");

// --- Resolve forward refs ---
Console.Write("Resolving forward refs... ");
int resolvedCount = 0;
foreach (var (idx, s) in structs.ToList())
{
    if (s.ForwardRef != null && structs.TryGetValue(s.ForwardRef, out var target) && target.ForwardRef == null)
    {
        structs[idx] = new StructDef(s.Name, target.SizeOf, target.FieldListIdx, null);
        resolvedCount++;
    }
}
Console.WriteLine($"{resolvedCount} resolved");

// --- Type resolution helpers ---
string ResolveType(string typeIdx, int depth = 0)
{
    if (depth > 10) return $"[{typeIdx}]";

    var idxInt = Convert.ToInt32(typeIdx, 16);
    if (BuiltinTypes.TryGetValue(idxInt, out var builtinName))
        return builtinName;
    if (structs.TryGetValue(typeIdx, out var s))
    {
        if (s.ForwardRef != null && structs.TryGetValue(s.ForwardRef, out var target))
            return target.Name;
        return s.Name;
    }
    if (enums.TryGetValue(typeIdx, out var en))
        return en.Name;
    if (arrays.TryGetValue(typeIdx, out var arr))
    {
        var elemType = ResolveType(arr.ElementType, depth + 1);
        var elemSize = GetTypeSize(arr.ElementType);
        if (elemSize > 0)
            return $"{elemType}[{arr.Size / elemSize}]";
        return $"{elemType}[?{arr.Size}B]";
    }
    if (modifiers.TryGetValue(typeIdx, out var mod))
        return ResolveType(mod.Referent, depth + 1);
    if (pointers.TryGetValue(typeIdx, out var ptr))
        return ResolveType(ptr.Referent, depth + 1) + "*";
    if (bitfields.TryGetValue(typeIdx, out var bf))
    {
        var baseTypeName = ResolveType(bf.BaseType, depth + 1);
        return $"{baseTypeName}:{bf.BitCount}";
    }
    return $"[{typeIdx}]";
}

int GetTypeSize(string typeIdx)
{
    var idxInt = Convert.ToInt32(typeIdx, 16);
    if (BuiltinSizes.TryGetValue(idxInt, out var sz)) return sz;
    if (structs.TryGetValue(typeIdx, out var s))
    {
        if (s.SizeOf > 0) return s.SizeOf;
        if (s.ForwardRef != null && structs.TryGetValue(s.ForwardRef, out var target))
            return target.SizeOf;
        return 0;
    }
    if (arrays.TryGetValue(typeIdx, out var arr)) return arr.Size;
    if (modifiers.TryGetValue(typeIdx, out var mod)) return GetTypeSize(mod.Referent);
    if (pointers.ContainsKey(typeIdx)) return 4;
    if (bitfields.TryGetValue(typeIdx, out var bf)) return 0; // bitfields share space
    return 0;
}

// --- Find PROTOCOL_COMMAND department table ---
Console.WriteLine();
Console.Write("Finding PROTOCOL_COMMAND... ");

FieldList protocolCommandFl = null;

foreach (var (flIdx, fl) in fieldlists)
{
    if (fl.Enumerates.Count < 10) continue;
    var nameMap = fl.Enumerates.ToDictionary(e => e.Name, e => e.Value);
    if (nameMap.GetValueOrDefault("NC_NULL") == 0 &&
        nameMap.GetValueOrDefault("NC_LOG") == 1 &&
        nameMap.GetValueOrDefault("NC_MISC") == 2)
    {
        protocolCommandFl = fl;
        break;
    }
}

if (protocolCommandFl == null)
{
    Console.Error.WriteLine("ERROR: Could not find PROTOCOL_COMMAND enum!");
    return;
}

var departments = new Dictionary<string, int>();
foreach (var e in protocolCommandFl.Enumerates)
    departments[e.Name.Replace("NC_", "")] = e.Value;

Console.WriteLine($"{departments.Count} departments");

var deptNames = departments.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
var deptNamesSorted = deptNames.OrderByDescending(d => d.Length).ToList();

// --- Collect per-department enum values ---
Console.Write("Collecting enums... ");

var allEnums = new Dictionary<string, int>();
foreach (var fl in fieldlists.Values)
    foreach (var e in fl.Enumerates)
        if (e.Name.StartsWith("NC_") && e.Name.IndexOf('_', 3) > 0)
            allEnums[e.Name] = e.Value;

var deptEnums = new Dictionary<string, Dictionary<string, int>>();
foreach (var (enumName, enumVal) in allEnums)
{
    var stripped = enumName[3..];
    string bestDept = null;
    foreach (var dept in deptNamesSorted)
    {
        if (stripped.StartsWith(dept + "_"))
        {
            bestDept = dept;
            break;
        }
    }
    bestDept ??= "_UNCATEGORIZED";
    if (!deptEnums.ContainsKey(bestDept))
        deptEnums[bestDept] = new Dictionary<string, int>();
    deptEnums[bestDept][enumName] = enumVal;
}

Console.WriteLine($"{allEnums.Count} values");

// Write all-enums.json
var enumsJson = new Dictionary<string, object>();
foreach (var dept in deptNames)
{
    if (!deptEnums.ContainsKey(dept)) continue;
    enumsJson[dept] = new
    {
        id = departments[dept],
        hex = $"0x{departments[dept]:X2}",
        opcodes = deptEnums[dept].OrderBy(e => e.Value)
            .ToDictionary(e => e.Key, e => e.Value)
    };
}
File.WriteAllText(
    Path.Combine(outputDir, "all-enums.json"),
    JsonSerializer.Serialize(enumsJson, new JsonSerializerOptions { WriteIndented = true })
);

// --- Collect all PROTO_NC_* structs ---
Console.Write("Collecting structs... ");

string ClassifyDept(string structName)
{
    var stripped = structName.Replace("PROTO_NC_", "");
    foreach (var dept in deptNamesSorted)
        if (stripped.StartsWith(dept + "_"))
            return dept;
    return stripped.Split('_')[0];
}

List<object> ResolveFields(string fieldListIdx)
{
    if (fieldListIdx == null || !fieldlists.TryGetValue(fieldListIdx, out var fl))
        return new List<object>();

    var result = new List<object>();
    foreach (var member in fl.Members)
    {
        var typeStr = ResolveType(member.TypeIdx);
        var size = GetTypeSize(member.TypeIdx);
        var field = new Dictionary<string, object>
        {
            ["name"] = member.Name,
            ["offset"] = member.Offset,
            ["type"] = typeStr,
            ["size"] = size,
        };
        if (member.IsBase) field["is_base"] = true;
        if (bitfields.TryGetValue(member.TypeIdx, out var bf))
        {
            field["bit_offset"] = bf.BitOffset;
            field["bit_count"] = bf.BitCount;
        }
        result.Add(field);
    }
    return result;
}

var protoStructs = new Dictionary<string, object>();

foreach (var (idx, s) in structs)
{
    if (!s.Name.StartsWith("PROTO_NC_")) continue;
    if (s.Name.Contains("::")) continue;
    if (s.FieldListIdx == null) continue;

    var dept = ClassifyDept(s.Name);
    var fields = ResolveFields(s.FieldListIdx);

    if (!protoStructs.ContainsKey(s.Name) || s.SizeOf > 0)
        protoStructs[s.Name] = new { s.Name, s.SizeOf, department = dept, fields };
}

Console.WriteLine($"{protoStructs.Count} protocol structs");

// --- Collect referenced types recursively ---
Console.Write("Collecting referenced types... ");

var referencedTypes = new Dictionary<string, object>();

void CollectReferencedType(string typeIdx, HashSet<string> visited)
{
    if (visited.Contains(typeIdx)) return;
    visited.Add(typeIdx);

    var idxInt = Convert.ToInt32(typeIdx, 16);
    if (BuiltinTypes.ContainsKey(idxInt)) return;

    if (structs.TryGetValue(typeIdx, out var s))
    {
        if (s.Name.Contains("::")) return;

        var fields = ResolveFields(s.FieldListIdx);
        if (!referencedTypes.ContainsKey(s.Name))
        {
            referencedTypes[s.Name] = new { s.Name, s.SizeOf, fields };
        }
        if (s.FieldListIdx != null && fieldlists.TryGetValue(s.FieldListIdx, out var fl))
        {
            foreach (var member in fl.Members)
                CollectReferencedType(member.TypeIdx, visited);
        }
        return;
    }

    if (arrays.TryGetValue(typeIdx, out var arr))
    {
        CollectReferencedType(arr.ElementType, visited);
        return;
    }
    if (modifiers.TryGetValue(typeIdx, out var mod))
    {
        CollectReferencedType(mod.Referent, visited);
        return;
    }
    if (pointers.TryGetValue(typeIdx, out var ptr))
    {
        CollectReferencedType(ptr.Referent, visited);
        return;
    }
}

var visitedTypes = new HashSet<string>();
foreach (var (idx, s) in structs)
{
    if (!s.Name.StartsWith("PROTO_NC_")) continue;
    if (s.Name.Contains("::")) continue;
    if (s.FieldListIdx == null) continue;

    if (fieldlists.TryGetValue(s.FieldListIdx, out var fl))
        foreach (var member in fl.Members)
            CollectReferencedType(member.TypeIdx, visitedTypes);
}

var helperTypes = referencedTypes
    .Where(kv => !((string)((dynamic)kv.Value).Name).StartsWith("PROTO_NC_"))
    .ToDictionary(kv => kv.Key, kv => kv.Value);

Console.WriteLine($"{helperTypes.Count} helper types");

// Write all-structs.json
File.WriteAllText(
    Path.Combine(outputDir, "all-structs.json"),
    JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["protocol_structs"] = protoStructs,
        ["referenced_types"] = helperTypes,
    }, new JsonSerializerOptions { WriteIndented = true })
);

Console.WriteLine();
Console.WriteLine("=== Done ===");
Console.WriteLine($"  all-enums.json   - {allEnums.Count} enum values across {deptEnums.Count} departments");
Console.WriteLine($"  all-structs.json - {protoStructs.Count} proto structs + {helperTypes.Count} helper types");
