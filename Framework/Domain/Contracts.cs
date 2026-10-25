namespace EndfieldChenTool.Framework.Domain;

public enum ResourceKind { Model, Avatar, Animation }
public enum StageState { Pending, NotRequested, Completed, Failed, Unsupported }

public sealed record BundleReference
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Md5 { get; init; }
}

public sealed record ResourceEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ResourceKind Kind { get; init; }
    public string SourcePath { get; init; } = "";
    public string Rig { get; init; } = "Unknown";
    public string BackendKey { get; init; } = "";
    public List<string> BundleIds { get; init; } = [];
}

public sealed record CharacterEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string RootName { get; init; } = "";
    public string RootPathId { get; init; } = "";
    public List<ResourceEntry> Resources { get; init; } = [];
}

// Metadata only. Importing a catalog never establishes decode/adaptation/verification success.
public sealed record ResourceCatalog
{
    public const string CurrentSchema = "endfield-character-catalog-v1";
    public string Schema { get; init; } = CurrentSchema;
    public required string GameBuild { get; init; }
    public required string GameAssemblySha256 { get; init; }
    public string SourceDescription { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<BundleReference> Bundles { get; init; } = [];
    public List<CharacterEntry> Characters { get; init; } = [];
}

public sealed record WorkspaceOptions
{
    public string GameDirectory { get; init; } = @"F:\AAA_ZMD\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data";
    public string WorkspaceDirectory { get; init; } = @"D:\AAAChatGPT\EndfieldCharacterWorkspace";
    public string UnityProject { get; init; } = @"D:\Unity 3D\Projects\Codex_ZMD";
    public string UnityEditor { get; init; } = @"D:\Unity 3D\Editor\6000.0.84f1\Editor\Unity.exe";
    public string Blender { get; init; } = @"D:\Blender\5.0.1\blender.exe";
    public bool ImportUnity { get; init; }
    public bool CheckWeights { get; init; }
    public bool VerifyPlayback { get; init; }
}

public sealed record StageRecord(string Name, StageState State, string Detail);

public sealed record ExtractionPlan
{
    public string Schema { get; init; } = "endfield-character-plan-v1";
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Status { get; init; } = "planned";
    public required string CharacterId { get; init; }
    public required string CharacterName { get; init; }
    public string RootName { get; init; } = "";
    public string RootPathId { get; init; } = "";
    public required string GameBuild { get; init; }
    public required string GameAssemblySha256 { get; init; }
    public required string CatalogSource { get; init; }
    public required WorkspaceOptions Options { get; init; }
    public List<string> RequestedResourceIds { get; init; } = [];
    public List<ResourceEntry> Resources { get; init; } = [];
    public List<BundleReference> Bundles { get; init; } = [];
    public List<StageRecord> Stages { get; init; } = [];
}

public sealed record SavedPlan(string FilePath, ExtractionPlan Plan);

public interface IResourceCatalogProvider
{
    ResourceCatalog Load(string path);
}

public interface ITaskPlanner
{
    ExtractionPlan Create(ResourceCatalog catalog, CharacterEntry character,
        IReadOnlyCollection<string> resourceIds, WorkspaceOptions options);
}

// Execution plugs in here in the next milestone; the framework supplies no fake executor.
public interface IExtractionBackend
{
    string Id { get; }
    bool Supports(ExtractionPlan plan);
    Task ExecuteAsync(ExtractionPlan plan, IProgress<StageRecord> progress, CancellationToken cancellationToken);
}
