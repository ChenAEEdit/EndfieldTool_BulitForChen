using System.Text.Json;
using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

public sealed class ProfileCatalogProvider : IResourceCatalogProvider
{
    public ResourceCatalog Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("schema").GetString() != "endfield-chen-tool-profile-v1")
            throw new InvalidDataException("不支持这个角色配置版本。");
        string Text(string key) => root.GetProperty(key).GetString() ?? "";
        var id = Text("character");
        var bundleRows = root.GetProperty("bundles").EnumerateArray().ToArray();
        List<string> BundlesFor(string role) => bundleRows.Where(row => row.GetProperty("roles")
            .EnumerateArray().Any(item => item.GetString() == role))
            .Select(row => row.GetProperty("name").GetString()!).Distinct().ToList();
        var resources = new List<ResourceEntry>
        {
            new() { Id = $"{id}:model", Name = Text("root_name"), Kind = ResourceKind.Model,
                SourcePath = Text("root_name"), BundleIds = BundlesFor("model") },
            new() { Id = $"{id}:avatar", Name = "Avatar / 骨架元数据", Kind = ResourceKind.Avatar,
                SourcePath = Text("root_name"), BundleIds = BundlesFor("model") }
        };
        foreach (var animation in root.GetProperty("animations").EnumerateObject())
            resources.Add(new ResourceEntry
            {
                Id = $"{id}:animation:{animation.Name}", Kind = ResourceKind.Animation,
                Name = animation.Value.GetProperty("clip_name").GetString()!,
                SourcePath = animation.Value.GetProperty("source_asset").GetString()!,
                Rig = animation.Value.GetProperty("rig").GetString()!, BackendKey = animation.Name,
                BundleIds = BundlesFor(animation.Name)
            });
        var catalog = new ResourceCatalog
        {
            GameBuild = Text("build"), GameAssemblySha256 = Text("game_assembly_sha256"),
            SourceDescription = "已支持角色配置：" + Path.GetFullPath(path),
            Bundles = bundleRows.Select(row => new BundleReference
            {
                Id = row.GetProperty("name").GetString()!, Group = row.GetProperty("group").GetString()!,
                Md5 = row.GetProperty("md5").GetString()!
            }).ToList(),
            Characters = [new CharacterEntry { Id = id, DisplayName = Text("display_name"),
                RootName = Text("root_name"), RootPathId = Text("root_path_id"), Resources = resources }]
        };
        CatalogService.Validate(catalog);
        return catalog;
    }
}

public sealed class JsonCatalogProvider : IResourceCatalogProvider
{
    public ResourceCatalog Load(string path)
    {
        var catalog = JsonFiles.Read<ResourceCatalog>(path);
        CatalogService.Validate(catalog);
        return catalog;
    }
}

public static class CatalogService
{
    public static IReadOnlyList<CharacterEntry> Search(ResourceCatalog catalog, string query)
    {
        var terms = query.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return catalog.Characters.Where(character => terms.All(term =>
            new[] { character.Id, character.DisplayName, character.RootName }.Any(value => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)
            || character.Resources.Any(resource => resource.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || resource.SourcePath?.Contains(term, StringComparison.OrdinalIgnoreCase) == true))).ToList();
    }

    public static void Validate(ResourceCatalog catalog)
    {
        if (catalog.Schema != ResourceCatalog.CurrentSchema)
            throw new InvalidDataException("不支持这个资源索引版本。");
        Require(!string.IsNullOrWhiteSpace(catalog.GameBuild), "索引缺少游戏版本。");
        Require(Hex(catalog.GameAssemblySha256, 64), "GameAssembly 摘要必须是 64 位十六进制 SHA256。");
        Require(catalog.Bundles != null && catalog.Characters != null, "索引列表不能为 null。");
        var bundles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bundle in catalog.Bundles!)
        {
            Require(bundle != null && !string.IsNullOrWhiteSpace(bundle.Id), "Bundle 缺少标识。");
            Require(bundles.Add(bundle!.Id), "Bundle 标识重复：" + bundle.Id);
            Require(!string.IsNullOrWhiteSpace(bundle.Group) && Hex(bundle.Md5, 32), "Bundle 分组或 MD5 无效。");
        }
        foreach (var bundle in catalog.Bundles!)
        {
            Require(bundle.DependencyIds != null && bundle.MissingDependencyNames != null, "Bundle 依赖列表不能为 null。");
            Require(bundle.DependencyIds!.All(id => id != null && bundles.Contains(id)), "Bundle 依赖引用不存在：" + bundle.Id);
        }
        var characters = new HashSet<string>(StringComparer.Ordinal);
        var resourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var character in catalog.Characters!)
        {
            Require(character != null && !string.IsNullOrWhiteSpace(character.Id)
                && !string.IsNullOrWhiteSpace(character.DisplayName), "角色缺少标识或名称。");
            Require(characters.Add(character!.Id), "角色标识重复：" + character.Id);
            Require(character.Resources != null, "角色资源列表不能为 null。");
            foreach (var resource in character.Resources!)
            {
                Require(resource != null && !string.IsNullOrWhiteSpace(resource.Id)
                    && !string.IsNullOrWhiteSpace(resource.Name), "资源缺少标识或名称。");
                Require(resourceIds.Add(resource!.Id), "资源标识重复：" + resource.Id);
                Require(Enum.IsDefined(resource.Kind), "不支持这个资源类型。");
                Require(Enum.IsDefined(resource.Association), "不支持这个资源关联类型。");
                Require(resource.SourcePath != null, "资源路径不能为 null。");
                Require(resource.BundleIds != null, "资源依赖列表不能为 null。");
                Require(resource.BundleIds!.All(id => id != null && bundles.Contains(id)), "资源引用了不存在的 Bundle：" + resource.Name);
            }
        }
    }

    private static bool Hex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
