using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

public sealed class ScanCatalogStateStore
{
    private const string StateSchema = "endfield-scan-catalog-state-v1";
    private const string StateFileName = "last-scan.json";

    public void Save(string workspaceDirectory, string catalogPath, string reportPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceDirectory));
        var index = Path.Combine(root, "index");
        var catalog = RequireSafeFile(root, catalogPath, "扫描索引");
        var report = RequireSafeFile(root, reportPath, "扫描报告");
        if (!IsWithin(catalog, index) || !IsWithin(report, index))
            throw new InvalidDataException("扫描索引和报告必须位于当前工作区的 index 目录中。");
        JsonFiles.Write(Path.Combine(index, StateFileName), new State(StateSchema, catalog, report));
    }

    public bool TryLoad(string workspaceDirectory, out ResourceCatalog? catalog, out string? catalogPath,
        out string? reportPath, out string? warning)
    {
        catalog = null;
        catalogPath = null;
        reportPath = null;
        warning = null;
        if (string.IsNullOrWhiteSpace(workspaceDirectory)) return false;

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceDirectory));
            var index = Path.Combine(root, "index");
            var statePath = Path.Combine(index, StateFileName);
            if (!File.Exists(statePath)) return false;
            if (!IsSafeWorkspaceFile(root, statePath))
                throw new InvalidDataException("最近扫描记录不在当前工作区内。");

            var state = JsonFiles.Read<State>(statePath);
            if (state.Schema != StateSchema)
                throw new InvalidDataException("最近扫描记录版本不兼容。");
            var safeCatalog = RequireSafeFile(root, state.CatalogPath, "扫描索引");
            if (!IsWithin(safeCatalog, index))
                throw new InvalidDataException("最近扫描索引不属于当前工作区。");
            var loaded = new JsonCatalogProvider().Load(safeCatalog);
            string? safeReport = null;
            if (!string.IsNullOrWhiteSpace(state.ReportPath))
            {
                try
                {
                    var candidateReport = RequireSafeFile(root, state.ReportPath, "扫描报告");
                    if (IsWithin(candidateReport, index)) safeReport = candidateReport;
                    else warning = "扫描索引已恢复，但报告路径不属于当前工作区。";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                                              or ArgumentException or NotSupportedException)
                { warning = "扫描索引已恢复，但关联报告不可用：" + error.Message; }
            }
            catalog = loaded;
            catalogPath = safeCatalog;
            reportPath = safeReport;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                                      or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            warning = "最近扫描索引无法恢复，未重新扫描：" + error.Message;
            return false;
        }
    }

    private static string RequireSafeFile(string root, string candidate, string description)
    {
        if (string.IsNullOrWhiteSpace(candidate)) throw new InvalidDataException(description + "路径为空。");
        var fullPath = Path.GetFullPath(candidate);
        if (!IsWithin(fullPath, root) || !File.Exists(fullPath) || !IsSafeWorkspaceFile(root, fullPath))
            throw new InvalidDataException(description + "不存在，或不属于当前工作区。");
        return fullPath;
    }

    private static bool IsSafeWorkspaceFile(string root, string path)
    {
        if (!IsWithin(path, root)) return false;
        var current = new DirectoryInfo(root);
        if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        var relative = Path.GetRelativePath(root, path);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            current = new DirectoryInfo(Path.Combine(current.FullName, parts[i]));
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        }
        var file = new FileInfo(path);
        return file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record State(string Schema, string CatalogPath, string ReportPath);
}
