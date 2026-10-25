namespace EndfieldChenTool.Framework.Domain;

public enum ResourceKind { Model, Avatar, Animation }
public enum StageState { Pending, NotRequested, Completed, Failed, Unsupported }
public enum AssociationKind { Configured, NameCandidate }

public sealed record BundleReference
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Md5 { get; init; }
    public string VirtualPath { get; init; } = "";
    public List<string> DependencyIds { get; init; } = [];
    public List<string> MissingDependencyNames { get; init; } = [];
}

public sealed record ResourceEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ResourceKind Kind { get; init; }
    public string SourcePath { get; init; } = "";
    public string Rig { get; init; } = "Unknown";
    public string BackendKey { get; init; } = "";
    public AssociationKind Association { get; init; }
    public string Evidence { get; init; } = "";
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
    public string CoverageNote { get; init; } = "";
    public string CompatibilityStatus { get; init; } = "Unknown";
    public string CompatibilityNote { get; init; } = "当前安装版本尚未检查。";
}

public sealed record IndexProgress(string Message, int Percent);
public sealed record GameIndexResult(ResourceCatalog? Catalog, string? CatalogPath, string ReportPath,
    string LogPath, string CompatibilityStatus);

public interface IGameResourceIndexer
{
    Task<GameIndexResult> BuildAsync(WorkspaceOptions options, bool force, IProgress<IndexProgress> progress,
        CancellationToken cancellationToken, bool probeOnly = false);
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
    public string CompatibilityStatus { get; init; } = "Unknown";
    public string CompatibilityNote { get; init; } = "当前安装版本尚未检查。";
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

public sealed record ExtractionProgress(string Message, int Percent);

public sealed record ExtractedBundleFile
{
    public string Group { get; init; } = "";
    public string VirtualPath { get; init; } = "";
    public string Path { get; init; } = "";
    public string Md5 { get; init; } = "";
    public long Length { get; init; }
    public bool Cached { get; init; }
}

public sealed record BundleExtractionResult
{
    public string Schema { get; init; } = "endfield-bundle-extraction-v1";
    public Guid TaskId { get; init; }
    public string RunId { get; init; } = "";
    public string Status { get; init; } = "running";
    public string Message { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string OutputDirectory { get; init; } = "";
    public int TotalBundles { get; init; }
    public int ExtractedFiles { get; init; }
    public int ReusedFiles { get; init; }
    public string BundleExtraction { get; init; } = "running";
    public string ObjectDecoding { get; init; } = "not_run";
    public string AnimationAdaptation { get; init; } = "not_run";
    public string Weights { get; init; } = "not_run";
    public string UnityPreview { get; init; } = "not_run";
    public string PlaybackVerification { get; init; } = "not_run";
    public List<ExtractedBundleFile> Files { get; init; } = [];
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; init; }
}

public interface IExtractionBackend
{
    string Id { get; }
    bool Supports(ExtractionPlan plan);
    Task<BundleExtractionResult> ExecuteAsync(SavedPlan saved, IProgress<ExtractionProgress> progress,
        CancellationToken cancellationToken);
}

public sealed record BundleObjectRow
{
    public string Key { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string PathId { get; init; } = "";
    public string SourceAsset { get; init; } = "";
    public string SourceBundle { get; init; } = "";
    public bool IsModelCandidate { get; init; }
    public string? LinkedAvatarKey { get; init; }
    public int RendererCount { get; init; }
    public int BoneReferenceCount { get; init; }
    public int HierarchyNodes { get; init; }
    public override string ToString() => $"{Name} [{PathId}]";
}

public sealed record ObjectInventory
{
    public string Schema { get; init; } = "";
    public Guid TaskId { get; init; }
    public string RunId { get; init; } = "";
    public List<BundleObjectRow> Objects { get; init; } = [];
}

public sealed record AnimationPreparationOptions
{
    public decimal ModelImportScale { get; init; } = 1;
    public decimal PositionScale { get; init; } = 1;
    public bool DropUnmatched { get; init; }
    public bool DropSceneTranslation { get; init; } = true;
    public bool ForceLoop { get; init; }
    public bool CreatePreviewScene { get; init; } = true;
    public string NormalizeRoot { get; init; } = "";
    public List<string> ExcludedDescendants { get; init; } = [];
    public List<string> ExcludedNodes { get; init; } = [];
}

public sealed record ObjectExportSelection(string? ModelKey, string? AvatarKey, List<string> ClipKeys,
    AnimationPreparationOptions? Adaptation = null);

public sealed record ObjectOutputFile
{
    public string Kind { get; init; } = "";
    public string Path { get; init; } = "";
    public long Bytes { get; init; }
    public string Sha256 { get; init; } = "";
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; init; }
}

public sealed record ObjectOperationResult
{
    public string Schema { get; init; } = "endfield-object-operation-v1";
    public Guid TaskId { get; init; }
    public string RunId { get; init; } = "";
    public string BundleRunId { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Status { get; init; } = "";
    public string Message { get; init; } = "";
    public string? InventoryPath { get; init; }
    public string OutputDirectory { get; init; } = "";
    public string? UnityPackageDirectory { get; init; }
    public string? UnityAssetRoot { get; init; }
    public string? UnityConfigSha256 { get; init; }
    public int ExpectedAdaptedClips { get; init; }
    public string? UnityReportPath { get; init; }
    public string? UnityPreviewScene { get; init; }
    public bool Adapted { get; init; }
    public bool PlaybackVerified { get; init; }
    public List<ObjectOutputFile> Files { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<string> Failures { get; init; } = [];
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; init; }
}
