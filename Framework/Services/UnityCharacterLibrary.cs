using System.Text.Json;
using System.Text.RegularExpressions;

namespace EndfieldChenTool.Framework.Services;

internal static class UnityCharacterLibrary
{
    internal const string PreviewScene = "Assets/EndfieldCharacterWorkbench/Preview/CharacterPreview.unity";
    internal static string Root(string libraryRoot)
        => Regex.IsMatch(libraryRoot, "^Assets/EndfieldCharacterLibrary/[A-F0-9]{64}$") ? libraryRoot
            : throw new InvalidDataException("角色库目录无效。");

    internal static string Input(string assetRoot, string libraryRoot, string relative)
        => libraryRoot.Length > 0 && relative.StartsWith("Model/", StringComparison.Ordinal)
            ? Root(libraryRoot) + "/" + relative : assetRoot + "/" + relative;

    internal static async Task VerifyAsync(string project, string libraryRoot, string receiptHash,
        IReadOnlyDictionary<string, string> modelInputs, CancellationToken token)
    {
        string root = Root(libraryRoot), receipt = UnityProjectFiles.Within(project, root + "/Generated/library.json");
        if (await Task.Run(() => UnityProjectFiles.Hash(receipt, token), token) != receiptHash)
            throw new InvalidDataException("角色库记录已变化，请保留原文件并核对模型版本。");
        var doc = JsonFiles.Read<JsonElement>(receipt);
        if (doc.GetProperty("schema").GetString() != "endfield-character-library-v1" || doc.GetProperty("root").GetString() != root)
            throw new InvalidDataException("角色库记录归属不符。");
        var sources = doc.GetProperty("inputs").EnumerateArray().ToArray();
        if (sources.Length != modelInputs.Count || sources.Any(row => !modelInputs.TryGetValue(row.GetProperty("path").GetString()!, out var sha)
            || row.GetProperty("sha256").GetString() != sha)) throw new InvalidDataException("动画包与角色库模型版本不符。");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in doc.GetProperty("outputs").EnumerateArray().Concat(doc.GetProperty("metadata").EnumerateArray()))
        {
            string relative = row.GetProperty("path").GetString()!;
            if (!relative.StartsWith(root + "/", StringComparison.Ordinal) || !expected.Add(relative))
                throw new InvalidDataException("角色库清单存在重复或外部路径。");
            string path = UnityProjectFiles.Within(project, relative);
            if (new FileInfo(path).Length != row.GetProperty("bytes").GetInt64()
                || await Task.Run(() => UnityProjectFiles.Hash(path, token), token) != row.GetProperty("sha256").GetString())
                throw new InvalidDataException("角色库资源或导入设置已修改；不会覆盖已有编辑。");
        }
        if (!expected.Contains(root + "/Generated/Character.prefab")) throw new InvalidDataException("角色库缺少角色 Prefab。");
    }
}
