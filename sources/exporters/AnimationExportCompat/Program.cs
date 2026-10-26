using AnimeStudio;
using SevenZip;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);
var options = ParseOptions(args);
if (!options.TryGetValue("input", out var input) ||
    !options.TryGetValue("output", out var output) ||
    !options.TryGetValue("name-contains", out var nameContains))
{
    Console.Error.WriteLine("Usage: EndfieldAnimationExporter --input <bundle-dir> --output <dir> --name-contains <token> [--clip-exclude-regex <pattern>] [--additional-input <bundle-dir>] [--report <json>]");
    return 2;
}

input = Path.GetFullPath(input);
output = Path.GetFullPath(output);
var reportPath = Path.GetFullPath(options.GetValueOrDefault("report", Path.Combine(output, "export_report.json")));
var clipExcludePattern = options.GetValueOrDefault("clip-exclude-regex");
var clipExcludeRegex = string.IsNullOrWhiteSpace(clipExcludePattern)
    ? null
    : new Regex(clipExcludePattern, RegexOptions.CultureInvariant);
var inputs = new[] { input }
    .Concat(options.TryGetValue("additional-input", out var additionalInput)
        ? additionalInput.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
        : Array.Empty<string>())
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
var files = inputs
    .SelectMany(path => Directory.Exists(path)
        ? Directory.GetFiles(path, "*.ab", SearchOption.AllDirectories)
        : new[] { path })
    .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
    .Select(group => group.First())
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .ToArray();
if (files.Length == 0)
{
    Console.Error.WriteLine($"No .ab files found under {input}");
    return 2;
}

var manager = new AssetsManager
{
    Game = GameManager.GetGame(GameType.ArknightsEndfield),
    Silent = true,
    SkipProcess = false,
};
manager.LoadFiles(files);
var globalTos = new Dictionary<uint, string> { [0] = string.Empty };
void AddGlobalTos(GameObject parent, string parentPath)
{
    foreach (var childPtr in parent.m_Transform.m_Children)
    {
        if (childPtr.TryGet(out var childTransform) && childTransform.m_GameObject.TryGet(out var child))
        {
            var path = parentPath.Length > 0 ? parentPath + '/' + child.m_Name : child.m_Name;
            globalTos.TryAdd(CRC.CalculateDigestUTF8(path), path);
            AddGlobalTos(child, path);
        }
    }
}
foreach (var rootTransform in manager.assetsFileList.SelectMany(file => file.Objects).OfType<Transform>()
    .Where(transform => !options.ContainsKey("selection") && !transform.m_Father.TryGet(out _)))
{
    if (rootTransform.m_GameObject.TryGet(out var root)) AddGlobalTos(root, string.Empty);
}
if (options.TryGetValue("avatar-tos", out var avatarTos))
{
    globalTos.Clear(); globalTos[0] = "";
    using var avatarDocument = JsonDocument.Parse(File.ReadAllText(avatarTos));
    foreach (var path in avatarDocument.RootElement.GetProperty("avatar").GetProperty("m_TOS").EnumerateObject())
        globalTos[uint.Parse(path.Name)] = path.Value.GetString()!;
}
Directory.CreateDirectory(output);
// Keep source-path evidence separate from the selected target Avatar. Never
// resolve a CRC collision by whichever dictionary happened to be loaded first.
var pathEvidence = new Dictionary<uint, Dictionary<string, HashSet<string>>>();
void AddEvidence(uint hash, string path, string source)
{
    if (hash != 0 && CRC.CalculateDigestUTF8(path) != hash) return;
    if (!pathEvidence.TryGetValue(hash, out var paths)) pathEvidence[hash] = paths = new(StringComparer.Ordinal);
    if (!paths.TryGetValue(path, out var sources)) paths[path] = sources = new(StringComparer.Ordinal);
    sources.Add(source);
}
foreach (var pair in globalTos) AddEvidence(pair.Key, pair.Value, "selected-target-path-table");
var rigCandidates = new List<(string Key, string Name, string File, JsonElement Data, HashSet<uint> Hashes)>();
if (options.TryGetValue("rig-catalog", out var rigCatalog))
{
    string catalogDirectory = Path.GetDirectoryName(Path.GetFullPath(rigCatalog))! + Path.DirectorySeparatorChar;
    using var catalog = JsonDocument.Parse(File.ReadAllText(rigCatalog));
    foreach (var row in catalog.RootElement.GetProperty("objects").EnumerateArray())
    {
        if (!row.TryGetProperty("rigEvidencePath", out var rigPath) || rigPath.ValueKind != JsonValueKind.String) continue;
        string path = Path.GetFullPath(rigPath.GetString()!);
        if (!path.StartsWith(catalogDirectory, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source rig evidence outside inventory snapshot");
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (sha != row.GetProperty("rigEvidenceSha256").GetString()) throw new InvalidDataException("Source rig evidence changed");
        using var rigDocument = JsonDocument.Parse(File.ReadAllText(path));
        var rig = rigDocument.RootElement.Clone();
        if (rig.GetProperty("schema").GetString() != "endfield-rig-evidence-v1") throw new InvalidDataException("Unknown source rig evidence schema");
        string key = rig.GetProperty("key").GetString()!, name = rig.GetProperty("name").GetString()!;
        var hashes = new HashSet<uint>();
        foreach (var node in rig.GetProperty("nodes").EnumerateArray())
        {
            string bone = node.GetProperty("path").GetString()!;
            uint hash = bone.Length == 0 ? 0 : CRC.CalculateDigestUTF8(bone);
            hashes.Add(hash); AddEvidence(hash, bone, key);
        }
        string copy = Path.Combine(output, "rig-" + key + ".json"); File.Copy(path, copy, false);
        rigCandidates.Add((key, name, copy, rig, hashes));
    }
}

var allClips = manager.assetsFileList.SelectMany(file => file.Objects).OfType<AnimationClip>().ToArray();
var candidateClips = allClips
    .Where(clip => clip.m_Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
    .ToArray();
if (options.TryGetValue("selection", out var selectionPath))
{
    using var selection = JsonDocument.Parse(File.ReadAllText(selectionPath));
    var exact = selection.RootElement.EnumerateArray().Select(row => (
        SourceAsset: row.GetProperty("sourceAsset").GetString(),
        SourceBundle: row.GetProperty("sourceBundle").GetString(),
        PathId: long.Parse(row.GetProperty("pathId").GetString()!))).ToHashSet();
    candidateClips = allClips.Where(clip => exact.Contains((clip.assetsFile.fileName, clip.assetsFile.originalPath, clip.m_PathID))).ToArray();
    if (candidateClips.Length != exact.Count) throw new InvalidDataException("Selected clips are missing or ambiguous.");
}
var excludedClips = candidateClips
    .Where(clip => clipExcludeRegex?.IsMatch(clip.m_Name) == true)
    .ToArray();
var clips = candidateClips
    .Where(clip => clipExcludeRegex?.IsMatch(clip.m_Name) != true)
    .ToArray();
var exports = new List<object>();
var failures = new List<object>();
foreach (var clip in clips)
{
    try
    {
        var yaml = clip.Convert();
        var usedPaths = new Dictionary<uint, string>();
        yaml = Regex.Replace(yaml, "(?m)^    path: *(.*)$", match =>
        {
            string originalPath = match.Groups[1].Value.TrimEnd('\r');
            if (originalPath.StartsWith('"')) originalPath = JsonSerializer.Deserialize<string>(originalPath)!;
            uint hash = originalPath.StartsWith("path_", StringComparison.Ordinal) && uint.TryParse(originalPath.AsSpan(5), out var parsed)
                ? parsed : originalPath.Length == 0 ? 0 : CRC.CalculateDigestUTF8(originalPath);
            // A readable path emitted by the library remains useful evidence, but
            // cannot override conflicting paths discovered in source skeletons.
            if (!originalPath.StartsWith("path_", StringComparison.Ordinal)) AddEvidence(hash, originalPath, "decoded-source-path");
            string path = pathEvidence.TryGetValue(hash, out var paths) && paths.Count == 1 ? paths.Keys.Single() : "path_" + hash;
            usedPaths[hash] = path;
            return "    path: " + (path.IndexOfAny([':', '#', '"', '\\']) >= 0 ? JsonSerializer.Serialize(path) : path);
        });
        var sourceSuffix = options.ContainsKey("selection") ? "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            clip.assetsFile.originalPath + "\n" + clip.assetsFile.fileName)))[..12] : "";
        var stem = SafeName(clip.m_Name);
        if (options.ContainsKey("selection") && stem.Length > 48) stem = stem[..48];
        var fileName = $"{stem}_{clip.m_PathID}{sourceSuffix}.anim";
        var target = Path.Combine(output, fileName);
        File.WriteAllText(target, yaml);
        string auditPath = target + ".paths.json";
        var pathAudit = new { schema = "endfield-animation-path-evidence-v1", sourceAsset = clip.assetsFile.fileName, sourceBundle = clip.assetsFile.originalPath,
            pathId = clip.m_PathID.ToString(), name = clip.m_Name,
            paths = usedPaths.OrderBy(pair => pair.Key).Select(pair => new { hash = pair.Key.ToString(), path = pair.Value,
                status = pair.Value.StartsWith("path_", StringComparison.Ordinal) ? pathEvidence.TryGetValue(pair.Key, out var candidates) && candidates.Count > 1 ? "ambiguous-source-hash" : "unresolved-source-hash" : "resolved",
                candidates = pathEvidence.TryGetValue(pair.Key, out var evidence) ? evidence.Select(item => new { path = item.Key, sources = item.Value.Order().ToArray() }).ToArray() : [] }).ToArray(),
            rigs = rigCandidates.Select(rig => new { key = rig.Key, name = rig.Name, file = rig.File,
                matchedPaths = usedPaths.Keys.Count(rig.Hashes.Contains), totalPaths = usedPaths.Count,
                sourceAsset = rig.Data.GetProperty("sourceAsset").GetString(), sourceBundle = rig.Data.GetProperty("sourceBundle").GetString(), pathId = rig.Data.GetProperty("pathId").GetString() }).ToArray() };
        File.WriteAllText(auditPath, JsonSerializer.Serialize(pathAudit, new JsonSerializerOptions { WriteIndented = true }));
        exports.Add(new
        {
            clip.m_Name,
            clip.m_PathID,
            source_asset_file = clip.assetsFile.fileName,
            source_bundle = clip.assetsFile.originalPath,
            output = target,
            path_evidence = auditPath,
            bytes = new FileInfo(target).Length,
            curves = Regex.Matches(yaml, "(?m)^  - curve:").Count,
            paths = Regex.Matches(yaml, "(?m)^    path:").Count,
            keys = Regex.Matches(yaml, "(?m)^        time:").Count,
            unknown_paths = Regex.Matches(yaml, "(?m)^    path: path_[0-9]+$").Count,
        });
    }
    catch (Exception error)
    {
        failures.Add(new
        {
            clip.m_Name,
            clip.m_PathID,
            source_asset_file = clip.assetsFile.fileName,
            source_bundle = clip.assetsFile.originalPath,
            error = error.Message,
        });
        Console.Error.WriteLine($"{clip.m_Name}: {error.Message}");
    }
}

var staleExports = failures.Count == 0
    ? ReadManagedOutputs(reportPath, "output")
        .Where(path => IsManagedOutput(path, output, ".anim"))
        .Except(exports.Select(GetOutput).OfType<string>(), StringComparer.OrdinalIgnoreCase)
        .ToArray()
    : [];
foreach (var staleExport in staleExports)
    File.Delete(staleExport);

var report = new
{
    schema = "endfield-animation-export-v3",
    input,
    inputs,
    output,
    name_contains = nameContains,
    clip_exclude_regex = clipExcludePattern,
    bundle_file_count = files.Length,
    loaded_asset_file_count = manager.assetsFileList.Count,
    all_clip_count = allClips.Length,
    all_clip_names = allClips.Select(c => new { c.m_Name, c.m_PathID, bundle = c.assetsFile.originalPath }).ToArray(),
    candidate_clip_count = candidateClips.Length,
    excluded_clip_count = excludedClips.Length,
    excluded_clips = excludedClips.Select(clip => new
    {
        clip.m_Name,
        clip.m_PathID,
        source_asset_file = clip.assetsFile.fileName,
        source_bundle = clip.assetsFile.originalPath,
    }).ToArray(),
    selected_clip_count = clips.Length,
    exported_clip_count = exports.Count,
    failed_clip_count = failures.Count,
    removed_stale_export_count = staleExports.Length,
    removed_stale_exports = staleExports,
    exports,
    failures,
};
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"bundles={files.Length} selected={clips.Length} excluded={excludedClips.Length} exported={exports.Count} failed={failures.Count} stale={staleExports.Length} report={reportPath}");
return exports.Count == 0 ? 1 : 0;

static Dictionary<string, string> ParseOptions(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index + 1 < values.Length; index += 2)
    {
        if (!values[index].StartsWith("--", StringComparison.Ordinal))
            continue;
        result[values[index][2..]] = values[index + 1];
    }
    return result;
}

static string SafeName(string value)
{
    foreach (var character in Path.GetInvalidFileNameChars())
        value = value.Replace(character, '_');
    return string.IsNullOrWhiteSpace(value) ? "unnamed" : value;
}

static string? GetOutput(object value) =>
    value.GetType().GetProperty("output")?.GetValue(value) as string;

static IEnumerable<string> ReadManagedOutputs(string reportPath, string property)
{
    if (!File.Exists(reportPath))
        return [];
    try
    {
        using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
        if (!document.RootElement.TryGetProperty("exports", out var exports))
            return [];
        return exports.EnumerateArray()
            .Where(item => item.TryGetProperty(property, out _))
            .Select(item => item.GetProperty(property).GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(value!))
            .ToArray();
    }
    catch (JsonException)
    {
        return [];
    }
}

static bool IsManagedOutput(string path, string output, string extension)
{
    var fullPath = Path.GetFullPath(path);
    var relative = Path.GetRelativePath(output, fullPath);
    return !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
        !Path.IsPathRooted(relative) &&
        string.Equals(Path.GetExtension(fullPath), extension, StringComparison.OrdinalIgnoreCase);
}


