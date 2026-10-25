using System.Text.Json;
using System.Text.Json.Serialization;
using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static T Read<T>(string path)
    {
        if (new FileInfo(path).Length > 256 * 1024 * 1024)
            throw new InvalidDataException("文件超过当前工作台允许的 256 MB，请缩小索引或任务文件。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JsonSerializer.Deserialize<T>(reader.ReadToEnd(), Options)
            ?? throw new InvalidDataException("JSON 文件内容为空。");
    }

    public static void Write<T>(string path, T value)
    {
        var target = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class TaskPlanner : ITaskPlanner
{
    public ExtractionPlan Create(ResourceCatalog catalog, CharacterEntry character,
        IReadOnlyCollection<string> resourceIds, WorkspaceOptions options)
    {
        CatalogService.Validate(catalog);
        if (!catalog.Characters.Contains(character)) throw new InvalidDataException("所选角色不在当前索引中。");
        var requested = resourceIds.ToHashSet(StringComparer.Ordinal);
        if (requested.Count == 0 || requested.Any(id => !character.Resources.Any(resource => resource.Id == id)))
            throw new InvalidDataException("请勾选当前角色的有效资源。");
        if (options.VerifyPlayback && !options.ImportUnity)
            throw new InvalidDataException("实际播放检查需要同时选择 Unity 预览。");
        if (options.ImportUnity && (string.IsNullOrWhiteSpace(options.UnityProject) || string.IsNullOrWhiteSpace(options.UnityEditor)))
            throw new InvalidDataException("生成 Unity 预览需要填写项目和编辑器路径。");
        var selected = character.Resources.Where(resource => requested.Contains(resource.Id)).ToList();
        var candidates = selected.Any(resource => resource.Association == AssociationKind.NameCandidate);
        if (selected.Any(resource => resource.Kind == ResourceKind.Animation))
        {
            if (!candidates && (!character.Resources.Any(resource => resource.Kind == ResourceKind.Model)
                || !character.Resources.Any(resource => resource.Kind == ResourceKind.Avatar)))
                throw new InvalidDataException("当前角色缺少模型或 Avatar 元数据，尚不能生成动画预览提取计划。");
            selected = selected.Concat(character.Resources.Where(resource => resource.Kind != ResourceKind.Animation
                && (!candidates || resource.Association == AssociationKind.Configured))).DistinctBy(resource => resource.Id).ToList();
        }
        var dependencies = selected.SelectMany(resource => resource.BundleIds).ToHashSet(StringComparer.Ordinal);
        var bundlesById = catalog.Bundles.ToDictionary(bundle => bundle.Id);
        var pending = new Stack<string>(dependencies);
        while (pending.TryPop(out var bundleId))
            foreach (var dependency in bundlesById[bundleId].DependencyIds)
                if (dependencies.Add(dependency)) pending.Push(dependency);
        var missing = dependencies.Sum(id => bundlesById[id].MissingDependencyNames.Count);
        return new ExtractionPlan
        {
            CharacterId = character.Id, CharacterName = character.DisplayName,
            RootName = character.RootName, RootPathId = character.RootPathId,
            GameBuild = catalog.GameBuild, GameAssemblySha256 = catalog.GameAssemblySha256,
            CatalogSource = catalog.SourceDescription, Options = options,
            CompatibilityStatus = catalog.CompatibilityStatus, CompatibilityNote = catalog.CompatibilityNote,
            RequestedResourceIds = requested.Order().ToList(), Resources = selected,
            Bundles = catalog.Bundles.Where(bundle => dependencies.Contains(bundle.Id)).ToList(),
            Stages = [
                new("版本与依赖校验", StageState.Pending, $"执行时重新校验游戏摘要及依赖；当前有 {missing} 项缺失或不明确的依赖记录。"),
                new("角色与骨架确认", candidates ? StageState.Pending : StageState.NotRequested, "明文名称候选必须先解析对象、根节点和 Avatar，不能直接视为已适配角色。"),
                new("Bundle 提取", StageState.Pending, "执行时导出 Bundle 与清单依赖并校验 MD5。"),
                new("模型／动画对象解码", StageState.Pending, "Bundle 完成后手动解析对象、确认选择，再导出 FBX／原始 .anim；不自动适配。"),
                new("动画适配", selected.Any(resource => resource.Kind == ResourceKind.Animation) ? StageState.Pending : StageState.NotRequested, "按所选片段确认单位、路径和播放方式。"),
                new("骨骼权重检查", options.CheckWeights ? StageState.Pending : StageState.NotRequested, "未运行检查。"),
                new("Unity 预览", options.ImportUnity ? StageState.Pending : StageState.NotRequested, "未写入 Unity 项目。"),
                new("实际播放检查", options.VerifyPlayback ? StageState.Pending : StageState.NotRequested, "未运行检查。")
            ]
        };
    }
}

public sealed class WorkspaceStore
{
    private const string MarkerName = "character-workspace.json";
    private const string Schema = "endfield-character-workspace-v1";
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EndfieldCharacterTool", "settings.json");

    public string Prepare(WorkspaceOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.WorkspaceDirectory)) throw new InvalidDataException("请选择独立输出工作区。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.WorkspaceDirectory));
        if (string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不能将磁盘根目录作为工作区。");
        foreach (var protectedPath in new[] { options.GameDirectory, options.UnityProject, AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(protectedPath)) continue;
            var protectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedPath));
            if (Inside(root, protectedRoot) || Inside(protectedRoot, root))
                throw new InvalidDataException("工作区需要独立于游戏、Unity 项目和工具目录。");
        }
        var marker = Path.Combine(root, MarkerName);
        if (File.Exists(marker)) ReadMarker(marker);
        else
        {
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new InvalidDataException("该目录已有其他文件。请选择空目录或角色工作台已创建的工作区。");
            JsonFiles.Write(marker, new WorkspaceMarker(Schema));
        }
        return root;
    }

    public SavedPlan Save(ExtractionPlan plan)
    {
        var root = Prepare(plan.Options);
        var path = Path.Combine(root, "tasks", plan.Id.ToString("N"), "plan.json");
        JsonFiles.Write(path, plan);
        return new SavedPlan(path, plan);
    }

    public IReadOnlyList<SavedPlan> List(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return [];
        var root = Path.GetFullPath(directory);
        var marker = Path.Combine(root, MarkerName);
        if (!File.Exists(marker)) return [];
        ReadMarker(marker);
        var tasks = Path.Combine(root, "tasks");
        if (!Directory.Exists(tasks)) return [];
        // Each task lives one level below tasks. Do not recurse through arbitrary resource trees.
        return Directory.EnumerateDirectories(tasks).Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Select(path => Path.Combine(path, "plan.json")).Where(File.Exists)
            .Select(path => new SavedPlan(path, JsonFiles.Read<ExtractionPlan>(path)))
            .Where(item => item.Plan.Schema == "endfield-character-plan-v1")
            .OrderByDescending(item => item.Plan.CreatedAt).ToList();
    }

    private static void ReadMarker(string path)
    {
        if (JsonFiles.Read<WorkspaceMarker>(path).Schema != Schema)
            throw new InvalidDataException("工作区标识不兼容。");
    }

    private static bool Inside(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private sealed record WorkspaceMarker(string Schema);
}
