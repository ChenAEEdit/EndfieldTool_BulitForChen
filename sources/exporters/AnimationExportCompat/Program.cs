using AnimeStudio;
using SevenZip;
using System.Text.Json;
using System.Text.RegularExpressions;

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
foreach (var rootTransform in manager.assetsFileList.SelectMany(file => file.Objects).OfType<Transform>().Where(transform => !transform.m_Father.TryGet(out _)))
{
    if (rootTransform.m_GameObject.TryGet(out var root)) AddGlobalTos(root, string.Empty);
}
Directory.CreateDirectory(output);

var allClips = manager.assetsFileList.SelectMany(file => file.Objects).OfType<AnimationClip>().ToArray();
var candidateClips = allClips
    .Where(clip => clip.m_Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
    .ToArray();
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
        var yaml = clip.Convert();        yaml = Regex.Replace(yaml, "(?m)^    path: path_(\\d+)$", match =>
        {
            return uint.TryParse(match.Groups[1].Value, out var hash) && globalTos.TryGetValue(hash, out var path)
                ? "    path: " + path
                : match.Value;
        });
        var fileName = $"{SafeName(clip.m_Name)}_{clip.m_PathID}.anim";
        var target = Path.Combine(output, fileName);
        File.WriteAllText(target, yaml);
        exports.Add(new
        {
            clip.m_Name,
            clip.m_PathID,
            source_asset_file = clip.assetsFile.fileName,
            source_bundle = clip.assetsFile.originalPath,
            output = target,
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


