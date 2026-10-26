using AnimeStudio;
using System.Text.Json;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
var options = ParseOptions(args);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
if (!options.TryGetValue("input", out var input) ||
    !options.TryGetValue("output", out var output) ||
    !options.TryGetValue("root-name", out var rootName))
{
    Console.Error.WriteLine(
        "Usage: EndfieldModelExporter --input <bundle-dir> --output <dir> " +
        "--root-name <exact-name> [--additional-input <bundle-dir>] " +
        "[--path-id <id>] [--report <json>] [--list-only true]");
    return 2;
}

input = Path.GetFullPath(input);
output = Path.GetFullPath(output);
var reportPath = Path.GetFullPath(options.GetValueOrDefault("report", Path.Combine(output, "model_export_report.json")));
var listOnly = ParseBool(options.GetValueOrDefault("list-only", "false"), "list-only");
long? selectedPathId = options.TryGetValue("path-id", out var pathIdText)
    ? long.Parse(pathIdText, System.Globalization.CultureInfo.InvariantCulture)
    : null;
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

var objects = manager.assetsFileList.SelectMany(file => file.Objects).ToArray();
var roots = objects.OfType<GameObject>()
    .Where(gameObject => string.Equals(gameObject.m_Name, rootName, StringComparison.OrdinalIgnoreCase))
    .Where(gameObject => selectedPathId is null || gameObject.m_PathID == selectedPathId)
    .Where(gameObject => !options.TryGetValue("root-source-asset", out var asset) || gameObject.assetsFile.fileName == asset)
    .Where(gameObject => !options.TryGetValue("root-source-bundle", out var bundle) || gameObject.assetsFile.originalPath == bundle)
    .OrderBy(gameObject => gameObject.assetsFile.originalPath, StringComparer.OrdinalIgnoreCase)
    .ThenBy(gameObject => gameObject.m_PathID)
    .ToArray();
var strictSelection = options.GetValueOrDefault("strict-selection", "false") == "true";
if (strictSelection && roots.Length != 1) throw new InvalidDataException("Selected model root is missing or ambiguous.");

var probes = new List<CandidateProbe>();
foreach (var root in roots)
{
    string? optimizedHierarchyError = null;
    try
    {
        var converter = new ModelConverter(root, CreateConverterOptions(manager.Game));
        probes.Add(new CandidateProbe(root, converter, "animator-aware", null));
        continue;
    }
    catch (Exception error)
    {
        optimizedHierarchyError = error.ToString();
    }

    try
    {
        var converter = new ModelConverter(
            root.m_Name,
            new List<GameObject> { root },
            CreateConverterOptions(manager.Game));
        probes.Add(new CandidateProbe(root, converter, "direct-hierarchy-fallback", optimizedHierarchyError));
    }
    catch (Exception fallbackError)
    {
        probes.Add(new CandidateProbe(
            root,
            null,
            "failed",
            $"Animator-aware conversion:\n{optimizedHierarchyError}\n\nDirect hierarchy fallback:\n{fallbackError}"));
    }
}

var viable = probes
    .Where(probe => probe.Converter is not null && probe.Converter.MeshList.Count > 0)
    .OrderByDescending(probe => probe.VertexCount)
    .ThenByDescending(probe => probe.Converter!.MeshList.Count)
    .ThenBy(probe => probe.Root.assetsFile.originalPath, StringComparer.OrdinalIgnoreCase)
    .ThenBy(probe => probe.Root.m_PathID)
    .ToArray();
var selected = viable.FirstOrDefault();
var avatarCandidates = objects.OfType<Avatar>()
    .Where(avatar => !strictSelection || options.ContainsKey("avatar-path-id"))
    .Where(avatar => !options.TryGetValue("avatar-path-id", out var id) || avatar.m_PathID == long.Parse(id))
    .Where(avatar => !options.TryGetValue("avatar-source-asset", out var asset) || avatar.assetsFile.fileName == asset)
    .Where(avatar => !options.TryGetValue("avatar-source-bundle", out var bundle) || avatar.assetsFile.originalPath == bundle)
    .Select(ProbeAvatar)
    .OrderByDescending(candidate => candidate.ValidHumanBoneCount)
    .ThenByDescending(candidate => candidate.HumanDescriptionBoneCount)
    .ThenByDescending(candidate => candidate.TosCount)
    .ThenByDescending(candidate => candidate.SkeletonNodeCount)
    .ThenBy(candidate => candidate.Avatar.m_Name, StringComparer.OrdinalIgnoreCase)
    .ToArray();
if (strictSelection && options.ContainsKey("avatar-path-id") && avatarCandidates.Length != 1)
    throw new InvalidDataException("Selected Avatar is missing or ambiguous.");
var selectedAvatar = strictSelection ? avatarCandidates.FirstOrDefault() : avatarCandidates.FirstOrDefault(candidate => candidate.ValidHumanBoneCount >= 15);
string? fbxPath = null;
string? materialReportPath = null;
string? materialPropertyReportPath = null;
string? avatarRetargetPath = null;
string? humanCandidatesPath = null;
string[] texturePaths = [];
string? exportError = null;

if (!listOnly && selected is not null)
{
    Directory.CreateDirectory(output);
    var safeRootName = SafeName(selected.Root.m_Name);
    if (safeRootName.Length > 80) safeRootName = safeRootName[..80];
    fbxPath = Path.Combine(output, $"{safeRootName}_{selected.Root.m_PathID}.fbx");
    try
    {
        ExportFbxStaged(fbxPath, selected.Converter!, new Fbx.ExportOptions
        {
            eulerFilter = false,
            filterPrecision = 0.25f,
            exportAllNodes = true,
            exportSkins = true,
            exportAnimations = false,
            exportBlendShape = true,
            castToBone = false,
            boneSize = 10,
            scaleFactor = 1.0f,
            fbxVersion = 3,
            fbxFormat = 0,
        });

        texturePaths = selected.Converter!.TextureList
            .Select(texture => Path.Combine(output, TextureFileName(texture.Name)))
            .Where(File.Exists)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        materialReportPath = Path.Combine(output, "materials.json");
        File.WriteAllText(materialReportPath, JsonSerializer.Serialize(
            selected.Converter.MaterialList.Select(material => new
            {
                material.Name,
                textures = material.Textures?.Select(texture => new
                {
                    texture.Name,
                    texture.Dest,
                    offset = new { texture.Offset.X, texture.Offset.Y },
                    scale = new { texture.Scale.X, texture.Scale.Y },
                }).ToArray() ?? [],
            }),
            jsonOptions));

        materialPropertyReportPath = Path.Combine(output, "material_properties.json");
        File.WriteAllText(materialPropertyReportPath, JsonSerializer.Serialize(new
        {
            schema = "endfield-material-properties-v2",
            root_name = selected.Root.m_Name,
            root_path_id = selected.Root.m_PathID,
            materials = CollectResolvedMaterials(selected.Root)
                .Select(material => DescribeMaterialProperties(material, output))
                .ToArray(),
        }, jsonOptions));

        humanCandidatesPath = Path.Combine(output, "avatar_human_candidates.json");
        File.WriteAllText(humanCandidatesPath, JsonSerializer.Serialize(new
        {
            schema = "endfield-human-mapping-candidates-v1",
            candidates = objects.OfType<Avatar>().Where(item => item.m_HumanDescription?.m_Human.Count >= 15)
                .Select(item => new { name = item.m_Name, sourceAsset = item.assetsFile.fileName,
                    sourceBundle = item.assetsFile.originalPath, pathId = item.m_PathID.ToString(),
                    bones = item.m_HumanDescription!.m_Human.Select(bone => new { bone.m_BoneName, bone.m_HumanName }).ToArray() }).ToArray(),
        }, jsonOptions));
        if (selectedAvatar is not null)
        {
            avatarRetargetPath = Path.Combine(output, "avatar_retarget.json");
            File.WriteAllText(avatarRetargetPath, JsonSerializer.Serialize(new
            {
                schema = "endfield-avatar-retarget-v1",
                source = DescribeAvatarProbe(selectedAvatar),
                avatar = DescribeAvatarData(selectedAvatar.Avatar),
            }, jsonOptions));
        }
    }
    catch (Exception error)
    {
        exportError = error.ToString();
    }
}

var report = new
{
    schema = "endfield-model-export-v2",
    input,
    inputs,
    output,
    root_name = rootName,
    requested_path_id = selectedPathId,
    list_only = listOnly,
    bundle_file_count = files.Length,
    loaded_asset_file_count = manager.assetsFileList.Count,
    object_count = objects.Length,
    matching_root_count = roots.Length,
    viable_root_count = viable.Length,
    selected = selected is null ? null : DescribeProbe(selected),
    avatar_retarget = avatarRetargetPath,
    avatar_human_candidates = humanCandidatesPath,
    avatar_retarget_available = avatarRetargetPath is not null && File.Exists(avatarRetargetPath),
    selected_avatar = selectedAvatar is null ? null : DescribeAvatarProbe(selectedAvatar),
    avatar_candidates = avatarCandidates.Select(DescribeAvatarProbe).ToArray(),
    unresolved_external_file_count = selected is null ? 0 : CollectUnresolvedExternalFiles(selected.Root).Length,
    unresolved_external_files = selected is null ? [] : CollectUnresolvedExternalFiles(selected.Root),
    material_reference_audit = selected is null ? null : DescribeMaterialReferenceAudit(selected.Root),
    candidates = probes.Select(DescribeProbe).ToArray(),
    fbx = fbxPath,
    fbx_export_transport = "short-owned-temp-directory-and-sha256-verified-copy",
    fbx_bytes = fbxPath is not null && File.Exists(fbxPath) ? new FileInfo(fbxPath).Length : 0,
    material_report = materialReportPath,
    material_properties_report = materialPropertyReportPath,
    texture_count = texturePaths.Length,
    textures = texturePaths.Select(path => new { path, bytes = new FileInfo(path).Length }).ToArray(),
    export_error = exportError,
};
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, jsonOptions));

Console.WriteLine(
    $"bundles={files.Length} matching_roots={roots.Length} viable_roots={viable.Length} " +
    $"meshes={selected?.Converter?.MeshList.Count ?? 0} vertices={selected?.VertexCount ?? 0} " +
    $"textures={texturePaths.Length} avatar_bones={selectedAvatar?.ValidHumanBoneCount ?? 0} " +
    $"fbx={fbxPath ?? "none"} report={reportPath}");
if (exportError is not null)
{
    Console.Error.WriteLine(exportError);
    return 1;
}
return selected is null ? 1 : 0;

static void ExportFbxStaged(string target, IImported imported, Fbx.ExportOptions exportOptions)
{
    // FBXWrapper changes the process working directory. Windows limits this API
    // even when managed file IO supports the long destination path.
    string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
    string stage = Path.GetFullPath(Path.Combine(tempRoot, "EndfieldFbx-" + Guid.NewGuid().ToString("N")));
    if (!stage.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(stage).Length != "EndfieldFbx-".Length + 32 || stage.Length > 180 || Directory.Exists(stage))
        throw new IOException("无法建立独立的短路径 FBX 临时目录，请检查系统临时目录设置。");
    string previousDirectory = Environment.CurrentDirectory;
    Directory.CreateDirectory(stage);
    try
    {
        try { ModelExporter.ExportFbx(Path.Combine(stage, Path.GetFileName(target)), imported, exportOptions); }
        finally { Environment.CurrentDirectory = previousDirectory; }
        string destinationRoot = Path.GetFullPath(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(destinationRoot);
        foreach (string source in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
        {
            string destination = Path.GetFullPath(Path.Combine(destinationRoot, Path.GetRelativePath(stage, source)));
            if (!destination.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("FBX 输出超出目标目录。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
            using var original = File.OpenRead(source); using var copied = File.OpenRead(destination);
            if (!System.Security.Cryptography.SHA256.HashData(original).SequenceEqual(System.Security.Cryptography.SHA256.HashData(copied)))
                throw new IOException("FBX 或贴图复制摘要不一致：" + destination);
        }
        if (!File.Exists(target) || new FileInfo(target).Length == 0) throw new IOException("底层 FBX 导出没有生成有效模型。");
    }
    finally
    {
        // Only the exact, newly created GUID directory above can be removed.
        string verifiedStage = Path.GetFullPath(stage);
        if (verifiedStage != stage || !verifiedStage.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("FBX 临时目录归属校验失败，已停止清理。");
        Directory.Delete(verifiedStage, recursive: true);
    }
}

static ModelConverter.Options CreateConverterOptions(Game game) => new()
{
    imageFormat = ImageFormat.Png,
    game = game,
    collectAnimations = false,
    exportMaterials = true,
    materials = new HashSet<Material>(),
    uvs = new Dictionary<string, (bool, int)>
    {
        ["UV0"] = (true, 0),
        ["UV1"] = (true, 1),
        ["UV2"] = (false, 0),
        ["UV3"] = (false, 0),
        ["UV4"] = (false, 0),
        ["UV5"] = (false, 0),
        ["UV6"] = (false, 0),
        ["UV7"] = (false, 0),
    },
    texs = new Dictionary<string, int>(),
};

static object DescribeProbe(CandidateProbe probe) => new
{
    path_id = probe.Root.m_PathID,
    source_asset_file = probe.Root.assetsFile.fileName,
    source_bundle = probe.Root.assetsFile.originalPath,
    strategy = probe.Strategy,
    animator_has_transform_hierarchy = probe.Root.m_Animator?.m_HasTransformHierarchy,
    avatar = probe.Root.m_Animator is null
        ? null
        : DescribeReference(probe.Root.m_Animator.m_Avatar, probe.Root.m_Animator.assetsFile),
    mesh_count = probe.Converter?.MeshList.Count ?? 0,
    vertex_count = probe.VertexCount,
    face_count = probe.Converter?.MeshList.Sum(mesh =>
        mesh.SubmeshList?.Sum(submesh => submesh.FaceList?.Count ?? 0) ?? 0) ?? 0,
    bone_binding_count = probe.Converter?.MeshList.Sum(mesh => mesh.BoneList?.Count ?? 0) ?? 0,
    frame_count = probe.Converter is null ? 0 : CountFrames(probe.Converter.RootFrame),
    material_count = probe.Converter?.MaterialList.Count ?? 0,
    texture_count = probe.Converter?.TextureList.Count ?? 0,
    morph_count = probe.Converter?.MorphList.Count ?? 0,
    morph_channel_count = probe.Converter?.MorphList.Sum(morph => morph.Channels?.Count ?? 0) ?? 0,
    meshes = probe.Converter?.MeshList.Select(mesh => new
    {
        mesh.Path,
        vertices = mesh.VertexList?.Count ?? 0,
        submeshes = mesh.SubmeshList?.Count ?? 0,
        faces = mesh.SubmeshList?.Sum(submesh => submesh.FaceList?.Count ?? 0) ?? 0,
        bones = mesh.BoneList?.Count ?? 0,
    }).ToArray() ?? [],
    textures = probe.Converter?.TextureList.Select(texture => new
    {
        texture.Name,
        bytes = texture.Data?.Length ?? 0,
    }).ToArray() ?? [],
    renderers = DescribeRenderers(probe.Root),
    error = probe.Error,
};

static object[] DescribeRenderers(GameObject root)
{
    var result = new List<object>();
    Visit(root.m_Transform, root.m_Name);
    return result.ToArray();

    void Visit(Transform transform, string path)
    {
        if (transform.m_GameObject.TryGet(out var gameObject))
        {
            if (gameObject.m_SkinnedMeshRenderer is not null)
            {
                var renderer = gameObject.m_SkinnedMeshRenderer;
                result.Add(new
                {
                    path,
                    type = nameof(SkinnedMeshRenderer),
                    source_asset_file = renderer.assetsFile.fileName,
                    source_bundle = renderer.assetsFile.originalPath,
                    mesh = DescribeReference(renderer.m_Mesh, renderer.assetsFile),
                    materials = renderer.m_Materials.Select(pointer =>
                        DescribeReference(pointer, renderer.assetsFile)).ToArray(),
                    bone_pointer_count = renderer.m_Bones.Count,
                    resolved_bone_count = renderer.m_Bones.Count(pointer => pointer.TryGet(out _)),
                    blend_shape_weight_count = renderer.m_BlendShapeWeights?.Length ?? 0,
                });
            }
            else if (gameObject.m_MeshRenderer is not null)
            {
                var renderer = gameObject.m_MeshRenderer;
                result.Add(new
                {
                    path,
                    type = nameof(MeshRenderer),
                    source_asset_file = renderer.assetsFile.fileName,
                    source_bundle = renderer.assetsFile.originalPath,
                    mesh = gameObject.m_MeshFilter is null
                        ? null
                        : DescribeReference(gameObject.m_MeshFilter.m_Mesh, gameObject.m_MeshFilter.assetsFile),
                    materials = renderer.m_Materials.Select(pointer =>
                        DescribeReference(pointer, renderer.assetsFile)).ToArray(),
                    bone_pointer_count = 0,
                    resolved_bone_count = 0,
                    blend_shape_weight_count = 0,
                });
            }
        }

        foreach (var childPointer in transform.m_Children)
        {
            if (!childPointer.TryGet(out var child) || !child.m_GameObject.TryGet(out var childObject))
                continue;
            Visit(child, path + "/" + childObject.m_Name);
        }
    }
}

static AvatarProbe ProbeAvatar(Avatar avatar)
{
    var human = avatar.m_Avatar.m_Human;
    return new AvatarProbe(
        avatar,
        human.m_HumanBoneIndex.Count(index => index >= 0),
        human.m_Skeleton.m_Node.Count,
        human.m_Skeleton.m_AxesArray.Count,
        avatar.m_TOS.Count,
        avatar.m_HumanDescription?.m_Human.Count ?? 0);
}

static object DescribeAvatarProbe(AvatarProbe probe) => new
{
    name = probe.Avatar.m_Name,
    path_id = probe.Avatar.m_PathID,
    source_asset_file = probe.Avatar.assetsFile.fileName,
    source_bundle = probe.Avatar.assetsFile.originalPath,
    valid_human_bone_count = probe.ValidHumanBoneCount,
    skeleton_node_count = probe.SkeletonNodeCount,
    axes_count = probe.AxesCount,
    tos_count = probe.TosCount,
    human_description_bone_count = probe.HumanDescriptionBoneCount,
};

static object DescribeAvatarData(Avatar avatar)
{
    var human = avatar.m_Avatar.m_Human;
    return new
    {
        m_Name = avatar.m_Name,
        m_Avatar = new
        {
            m_Human = new
            {
                m_RootX = DescribeXForm(human.m_RootX),
                m_Skeleton = new
                {
                    m_Node = human.m_Skeleton.m_Node.Select(node => new
                    {
                        node.m_ParentId,
                        node.m_AxesId,
                    }).ToArray(),
                    m_ID = human.m_Skeleton.m_ID,
                    m_AxesArray = human.m_Skeleton.m_AxesArray.Select(axes => new
                    {
                        m_PreQ = DescribeVector4(axes.m_PreQ),
                        m_PostQ = DescribeVector4(axes.m_PostQ),
                        m_Sgn = DescribeVector(axes.m_Sgn),
                        m_Limit = new
                        {
                            m_Min = DescribeVector(axes.m_Limit.m_Min),
                            m_Max = DescribeVector(axes.m_Limit.m_Max),
                        },
                        axes.m_Length,
                        axes.m_Type,
                    }).ToArray(),
                },
                m_SkeletonPose = new
                {
                    m_X = human.m_SkeletonPose.m_X.Select(DescribeXForm).ToArray(),
                },
                m_LeftHand = new { human.m_LeftHand.m_HandBoneIndex },
                m_RightHand = new { human.m_RightHand.m_HandBoneIndex },
                human.m_HumanBoneIndex,
                human.m_HumanBoneMass,
                human.m_Scale,
                human.m_ArmTwist,
                human.m_ForeArmTwist,
                human.m_UpperLegTwist,
                human.m_LegTwist,
                human.m_ArmStretch,
                human.m_LegStretch,
                human.m_FeetSpacing,
                human.m_HasLeftHand,
                human.m_HasRightHand,
                human.m_HasTDoF,
            },
        },
        m_TOS = avatar.m_TOS.ToDictionary(
            item => item.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item => item.Value),
        m_HumanDescription = avatar.m_HumanDescription is null ? null : new
        {
            m_Human = avatar.m_HumanDescription.m_Human.Select(bone => new
            {
                bone.m_BoneName,
                bone.m_HumanName,
            }).ToArray(),
        },
    };
}

static object DescribeXForm(XForm value) => new
{
    t = DescribeVector(value.t),
    q = DescribeQuaternion(value.q),
    s = DescribeVector(value.s),
};

static object DescribeVector4(Vector4 value) => new
{
    x = value.X,
    y = value.Y,
    z = value.Z,
    w = value.W,
};

static object DescribeQuaternion(Quaternion value) => new
{
    x = value.X,
    y = value.Y,
    z = value.Z,
    w = value.W,
};

static object DescribeVector(object value) => value switch
{
    Vector3 vector => new { x = vector.X, y = vector.Y, z = vector.Z, w = 0.0f },
    Vector4 vector => new { x = vector.X, y = vector.Y, z = vector.Z, w = vector.W },
    _ => throw new InvalidOperationException($"Unsupported Avatar vector type: {value.GetType().Name}"),
};

static Material[] CollectResolvedMaterials(GameObject root)
{
    var materials = new Dictionary<(string File, long PathId), Material>();
    Visit(root.m_Transform);
    return materials.Values
        .OrderBy(material => material.m_Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(material => material.assetsFile.fileName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(material => material.m_PathID)
        .ToArray();

    void Visit(Transform transform)
    {
        if (transform.m_GameObject.TryGet(out var gameObject))
        {
            var pointers = gameObject.m_SkinnedMeshRenderer?.m_Materials
                ?? gameObject.m_MeshRenderer?.m_Materials;
            if (pointers is not null)
            {
                foreach (var pointer in pointers)
                {
                    if (pointer.TryGet(out var material))
                        materials[(material.assetsFile.fileName, material.m_PathID)] = material;
                }
            }
        }
        foreach (var childPointer in transform.m_Children)
            if (childPointer.TryGet(out var child)) Visit(child);
    }
}

static object DescribeMaterialReferenceAudit(GameObject root)
{
    var materials = CollectResolvedMaterials(root);
    var shaderReferences = materials.Select(material =>
    {
        TryResolveReference(material.m_Shader, material.assetsFile, out var shader, out var resolution);
        return new
        {
            material = material.m_Name,
            reference = DescribeReference(material.m_Shader, material.assetsFile),
            resolution,
            raw_shader_object_instantiated = shader is not null,
            metadata_only = false,
            object_bytes = shader?.byteSize,
            target_key = shader is not null
                ? $"{shader.assetsFile.fileName}:{shader.m_PathID}"
                : $"unresolved:{material.assetsFile.fileName}:{material.m_Shader.m_FileID}:{material.m_Shader.m_PathID}",
        };
    }).ToArray();
    var textureReferences = materials.SelectMany(material =>
        material.m_SavedProperties.m_TexEnvs
            .Where(texEnv => !texEnv.Value.m_Texture.IsNull)
            .Select(texEnv =>
            {
                TryResolveReference(
                    texEnv.Value.m_Texture,
                    material.assetsFile,
                    out var texture,
                    out var resolution);
                return new
                {
                    material = material.m_Name,
                    slot = texEnv.Key,
                    reference = DescribeReference(texEnv.Value.m_Texture, material.assetsFile),
                    resolution,
                    texture_instantiated = texture is not null,
                };
            })).ToArray();
    var uniqueShaderGroups = shaderReferences
        .GroupBy(item => item.target_key, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    return new
    {
        material_count = materials.Length,
        shader_reference_count = shaderReferences.Length,
        resolved_shader_reference_count = shaderReferences.Count(item => item.raw_shader_object_instantiated),
        unresolved_shader_reference_count = shaderReferences.Count(item => !item.raw_shader_object_instantiated),
        metadata_only_shader_reference_count = shaderReferences.Count(item => item.metadata_only == true),
        complete_shader_reference_count = shaderReferences.Count(item =>
            item.raw_shader_object_instantiated && item.metadata_only != true),
        unique_shader_target_count = uniqueShaderGroups.Length,
        unique_resolved_shader_object_count = uniqueShaderGroups.Count(group =>
            group.Any(item => item.raw_shader_object_instantiated)),
        metadata_only_unique_shader_object_count = uniqueShaderGroups.Count(group =>
            group.Any(item => item.metadata_only == true)),
        complete_unique_shader_object_count = uniqueShaderGroups.Count(group =>
            group.Any(item => item.raw_shader_object_instantiated && item.metadata_only != true)),
        texture_reference_count = textureReferences.Length,
        resolved_texture_reference_count = textureReferences.Count(item => item.texture_instantiated),
        unresolved_texture_reference_count = textureReferences.Count(item => !item.texture_instantiated),
        shaders = shaderReferences,
        textures = textureReferences,
    };
}

static object DescribeMaterialProperties(Material material, string output)
{
    TryResolveReference(material.m_Shader, material.assetsFile, out var shader, out var shaderResolution);
    var shaderProperties = shader?.m_ParsedForm?.m_PropInfo?.m_Props;
    return new
    {
        name = material.m_Name,
        path_id = material.m_PathID,
        source_asset_file = material.assetsFile.fileName,
        source_bundle = material.assetsFile.originalPath,
        shader = DescribeReference(material.m_Shader, material.assetsFile),
        shader_name = shader?.Name,
        shader_resolution = shaderResolution,
        shader_metadata_only = false,
        shader_object_bytes = shader?.byteSize,
        texture_slots = material.m_SavedProperties.m_TexEnvs.Select(texEnv =>
        {
            TryResolveReference(
                texEnv.Value.m_Texture,
                material.assetsFile,
                out var texture,
                out var textureResolution);
            var property = shaderProperties?.FirstOrDefault(candidate =>
                string.Equals(candidate.m_Name, texEnv.Key, StringComparison.Ordinal));
            var exportedPath = texture is Texture2D
                ? Path.Combine(output, TextureFileName(texture.m_Name + ".png"))
                : null;
            return new
            {
                slot = texEnv.Key,
                texture = DescribeReference(texEnv.Value.m_Texture, material.assetsFile),
                texture_name = texture?.m_Name,
                texture_type = texture?.GetType().Name,
                texture_resolution = textureResolution,
                exported_png = exportedPath,
                exported_png_exists = exportedPath is not null && File.Exists(exportedPath),
                offset = new { texEnv.Value.m_Offset.X, texEnv.Value.m_Offset.Y },
                scale = new { texEnv.Value.m_Scale.X, texEnv.Value.m_Scale.Y },
                shader_property = DescribeShaderProperty(property),
            };
        }).ToArray(),
        ints = material.m_SavedProperties.m_Ints?.Select(value => new
        {
            name = value.Key,
            value = value.Value,
        }).ToArray() ?? [],
        floats = material.m_SavedProperties.m_Floats.Select(value => new
        {
            name = value.Key,
            value = value.Value,
        }).ToArray(),
        colors = material.m_SavedProperties.m_Colors.Select(value => new
        {
            name = value.Key,
            value = new { value.Value.R, value.Value.G, value.Value.B, value.Value.A },
        }).ToArray(),
    };
}

static object? DescribeShaderProperty(SerializedProperty? property) => property is null
    ? null
    : new
    {
        name = property.m_Name,
        description = property.m_Description,
        attributes = property.m_Attributes,
        type = property.m_Type.ToString(),
        flags = property.m_Flags.ToString(),
        default_value = property.m_DefValue,
        default_texture = new
        {
            name = property.m_DefTexture.m_DefaultName,
            dimension = property.m_DefTexture.m_TexDim.ToString(),
        },
    };

static string[] CollectUnresolvedExternalFiles(GameObject root)
{
    var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (root.m_Animator is not null)
        Add(root.m_Animator.m_Avatar, root.m_Animator.assetsFile);
    Visit(root.m_Transform);
    return result.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();

    void Visit(Transform transform)
    {
        if (transform.m_GameObject.TryGet(out var gameObject))
        {
            if (gameObject.m_SkinnedMeshRenderer is not null)
            {
                var renderer = gameObject.m_SkinnedMeshRenderer;
                Add(renderer.m_Mesh, renderer.assetsFile);
                foreach (var material in renderer.m_Materials) Add(material, renderer.assetsFile);
            }
            else if (gameObject.m_MeshRenderer is not null)
            {
                var renderer = gameObject.m_MeshRenderer;
                if (gameObject.m_MeshFilter is not null)
                    Add(gameObject.m_MeshFilter.m_Mesh, gameObject.m_MeshFilter.assetsFile);
                foreach (var material in renderer.m_Materials) Add(material, renderer.assetsFile);
            }
        }
        foreach (var childPointer in transform.m_Children)
            if (childPointer.TryGet(out var child)) Visit(child);
    }

    void Add<T>(PPtr<T> pointer, SerializedFile owner) where T : AnimeStudio.Object
    {
        if (pointer.TryGet(out _) || pointer.m_FileID <= 0 || pointer.m_FileID > owner.m_Externals.Count)
            return;
        result.Add(owner.m_Externals[pointer.m_FileID - 1].fileName);
    }
}

static object DescribeReference<T>(PPtr<T> pointer, SerializedFile owner) where T : AnimeStudio.Object
{
    TryResolveReference(pointer, owner, out var target, out var resolution);
    string? externalFile = null;
    if (pointer.m_FileID > 0 && pointer.m_FileID - 1 < owner.m_Externals.Count)
        externalFile = owner.m_Externals[pointer.m_FileID - 1].fileName;
    return new
    {
        file_id = pointer.m_FileID,
        path_id = pointer.m_PathID,
        external_file = externalFile,
        resolved = target is not null,
        resolution,
        target_type = target?.GetType().Name,
        target_name = target?.Name,
        target_asset_file = target?.assetsFile.fileName,
        target_bundle = target?.assetsFile.originalPath,
    };
}

static bool TryResolveReference<T>(
    PPtr<T> pointer,
    SerializedFile owner,
    out T? target,
    out string resolution) where T : AnimeStudio.Object
{
    if (pointer.TryGet(out target))
    {
        resolution = "pptr-cache";
        return true;
    }

    SerializedFile? targetFile = null;
    if (pointer.m_FileID == 0)
    {
        targetFile = owner;
    }
    else if (pointer.m_FileID > 0 && pointer.m_FileID <= owner.m_Externals.Count)
    {
        var externalName = owner.m_Externals[pointer.m_FileID - 1].fileName;
        targetFile = owner.assetsManager.assetsFileList.FirstOrDefault(file =>
            string.Equals(file.fileName, externalName, StringComparison.OrdinalIgnoreCase));
    }

    if (targetFile is null)
    {
        target = null;
        resolution = pointer.IsNull ? "null" : "target-file-missing";
        return false;
    }

    if (!targetFile.ObjectsDic.TryGetValue(pointer.m_PathID, out var candidate))
    {
        target = null;
        var rawObject = targetFile.m_Objects.FirstOrDefault(item => item.m_PathID == pointer.m_PathID);
        resolution = rawObject is null
            ? "pathid-missing"
            : $"raw-object-not-instantiated:class-{rawObject.classID}:bytes-{rawObject.byteSize}";
        return false;
    }

    if (candidate is T typed)
    {
        target = typed;
        resolution = "direct-cab-pathid";
        return true;
    }

    target = null;
    resolution = "type-mismatch:" + candidate.GetType().Name;
    return false;
}

static int CountFrames(ImportedFrame frame)
{
    var count = 1;
    for (var index = 0; index < frame.Count; index++)
        count += CountFrames(frame[index]);
    return count;
}

static Dictionary<string, string> ParseOptions(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index + 1 < values.Length; index += 2)
    {
        if (!values[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Expected an option at argument {index + 1}: {values[index]}");
        result[values[index][2..]] = values[index + 1];
    }
    return result;
}

static bool ParseBool(string value, string optionName) =>
    bool.TryParse(value, out var result)
        ? result
        : throw new ArgumentException($"--{optionName} must be true or false, got: {value}");

static string SafeName(string value)
{
    foreach (var character in Path.GetInvalidFileNameChars())
        value = value.Replace(character, '_');
    return string.IsNullOrWhiteSpace(value) ? "unnamed" : value;
}

static string TextureFileName(string textureName)
{
    var safeRaw = SafeName(Path.GetFileNameWithoutExtension(textureName));
    if (safeRaw.Length > 100) safeRaw = safeRaw[..100];
    return safeRaw + ".png";
}

sealed record CandidateProbe(GameObject Root, ModelConverter? Converter, string Strategy, string? Error)
{
    public int VertexCount => Converter?.MeshList.Sum(mesh => mesh.VertexList?.Count ?? 0) ?? 0;
}

sealed record AvatarProbe(
    Avatar Avatar,
    int ValidHumanBoneCount,
    int SkeletonNodeCount,
    int AxesCount,
    int TosCount,
    int HumanDescriptionBoneCount);

