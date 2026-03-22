// merge-extractions.csx - Merge multiple PDB extraction JSON outputs into one.
//
// Usage:
//   dotnet script tools/merge-extractions.csx -- <output-dir> <input-dir1> <input-dir2> ...

using System.Text.Json;

if (Args.Count < 3)
{
    Console.WriteLine("Usage: dotnet script tools/merge-extractions.csx -- <output-dir> <input-dir1> <input-dir2> ...");
    return;
}

var outputDir = Args[0];
var inputDirs = Args.Skip(1).ToList();

Directory.CreateDirectory(outputDir);

Console.WriteLine("=== Merge PDB Extractions ===");
Console.WriteLine($"Output: {outputDir}");
Console.WriteLine($"Inputs: {string.Join(", ", inputDirs.Select(Path.GetFileName))}");

// --- Merge enums ---
Console.Write("Merging enums... ");

var mergedEnums = new Dictionary<string, Dictionary<string, int>>();
var mergedDeptMeta = new Dictionary<string, (int id, string hex)>();

foreach (var dir in inputDirs)
{
    var enumFile = Path.Combine(dir, "all-enums.json");
    if (!File.Exists(enumFile)) continue;

    var json = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(enumFile));

    foreach (var dept in json.EnumerateObject())
    {
        var deptObj = dept.Value;
        var id = deptObj.GetProperty("id").GetInt32();
        var hex = deptObj.GetProperty("hex").GetString();
        mergedDeptMeta[dept.Name] = (id, hex);

        if (!mergedEnums.ContainsKey(dept.Name))
            mergedEnums[dept.Name] = new Dictionary<string, int>();

        foreach (var op in deptObj.GetProperty("opcodes").EnumerateObject())
            mergedEnums[dept.Name].TryAdd(op.Name, op.Value.GetInt32());
    }
}

int totalEnums = mergedEnums.Values.Sum(d => d.Count);
Console.WriteLine($"{totalEnums} values across {mergedEnums.Count} departments");

// Write merged all-enums.json
var mergedEnumsJson = new Dictionary<string, object>();
foreach (var (dept, opcodes) in mergedEnums.OrderBy(kv => mergedDeptMeta.GetValueOrDefault(kv.Key).id))
{
    var (id, hex) = mergedDeptMeta.GetValueOrDefault(dept, (0, "0x00"));
    mergedEnumsJson[dept] = new
    {
        id,
        hex,
        opcodes = opcodes.OrderBy(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value)
    };
}

File.WriteAllText(
    Path.Combine(outputDir, "all-enums.json"),
    JsonSerializer.Serialize(mergedEnumsJson, new JsonSerializerOptions { WriteIndented = true })
);

// --- Merge structs ---
Console.Write("Merging structs... ");

var mergedProtoStructs = new Dictionary<string, JsonElement>();
var mergedHelperTypes = new Dictionary<string, JsonElement>();

foreach (var dir in inputDirs)
{
    var structFile = Path.Combine(dir, "all-structs.json");
    if (!File.Exists(structFile)) continue;

    var json = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(structFile));

    if (json.TryGetProperty("protocol_structs", out var protoStructs))
    {
        foreach (var s in protoStructs.EnumerateObject())
        {
            if (!mergedProtoStructs.ContainsKey(s.Name))
            {
                mergedProtoStructs[s.Name] = s.Value;
            }
            else
            {
                // Keep the one with more fields
                var existing = mergedProtoStructs[s.Name];
                var existingFieldCount = existing.TryGetProperty("fields", out var ef) ? ef.GetArrayLength() : 0;
                var newFieldCount = s.Value.TryGetProperty("fields", out var nf) ? nf.GetArrayLength() : 0;
                if (newFieldCount > existingFieldCount)
                    mergedProtoStructs[s.Name] = s.Value;
            }
        }
    }

    if (json.TryGetProperty("referenced_types", out var refTypes))
    {
        foreach (var t in refTypes.EnumerateObject())
        {
            if (!mergedHelperTypes.ContainsKey(t.Name))
            {
                mergedHelperTypes[t.Name] = t.Value;
            }
            else
            {
                var existing = mergedHelperTypes[t.Name];
                var existingFieldCount = existing.TryGetProperty("fields", out var ef) ? ef.GetArrayLength() : 0;
                var newFieldCount = t.Value.TryGetProperty("fields", out var nf) ? nf.GetArrayLength() : 0;
                if (newFieldCount > existingFieldCount)
                    mergedHelperTypes[t.Name] = t.Value;
            }
        }
    }
}

Console.WriteLine($"{mergedProtoStructs.Count} proto structs + {mergedHelperTypes.Count} helper types");

File.WriteAllText(
    Path.Combine(outputDir, "all-structs.json"),
    JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["protocol_structs"] = mergedProtoStructs,
        ["referenced_types"] = mergedHelperTypes,
    }, new JsonSerializerOptions { WriteIndented = true })
);

Console.WriteLine("=== Done ===");
