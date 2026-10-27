using AnimeStudio;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
var options = new Dictionary<string, string>();
for (int index = 0; index + 1 < args.Length; index += 2) options.Add(args[index], args[index + 1]);
var input = Path.GetFullPath(options["--input"]);
var reportPath = Path.GetFullPath(options["--report"]);
var output = Path.GetDirectoryName(reportPath)!;
Directory.CreateDirectory(output);
var files = Directory.GetFiles(input, "*.ab", SearchOption.TopDirectoryOnly).Order().ToArray();
if (files.Length == 0) throw new InvalidDataException("No Bundle files selected.");
var manager = new AssetsManager { Game = GameManager.GetGame(GameType.ArknightsEndfield), Silent = true, SkipProcess = false };
manager.LoadFiles(files);
var all = manager.assetsFileList.SelectMany(file => file.Objects).ToArray();
var rows = new List<object>();
foreach (var item in all)
{
    string? kind = item switch { GameObject => "GameObject", Mesh => "Mesh", Avatar => "Avatar", AnimationClip => "AnimationClip", _ => null };
    if (kind == null) continue;
    string key = Key(item);
    int renderers = 0, bones = 0, nodes = 0;
    bool candidate = false, hierarchyRoot = false;
    string? linkedAvatar = null, linkedControllerKey = null, parentGameObjectKey = null, tos = null, rig = null, rigError = null;
    string controllerEvidence = "none";
    var controllerClips = new List<string>();
    if (item is GameObject gameObject)
    {
        bool root = gameObject.m_Transform != null && !gameObject.m_Transform.m_Father.TryGet(out _);
        // An unresolved non-null parent is not proof of an independent asset root.
        hierarchyRoot = gameObject.m_Transform != null && gameObject.m_Transform.m_Father.m_PathID == 0;
        if (gameObject.m_Transform != null && gameObject.m_Transform.m_Father.TryGet(out var parentTransform)
            && parentTransform.m_GameObject.TryGet(out var parentObject)) parentGameObjectKey = Key(parentObject);
        if (root || gameObject.m_Animator != null)
        {
            var pending = new Stack<Transform>();
            if (gameObject.m_Transform != null) pending.Push(gameObject.m_Transform);
            var visited = new HashSet<string>();
            while (pending.TryPop(out var transform))
            {
                if (!visited.Add(Key(transform))) continue;
                nodes++;
                if (transform.m_GameObject.TryGet(out var child))
                {
                    if (child.m_SkinnedMeshRenderer != null)
                    {
                        renderers++; bones += child.m_SkinnedMeshRenderer.m_Bones.Count;
                    }
                    else if (child.m_MeshRenderer != null) renderers++;
                }
                foreach (var pointer in transform.m_Children) if (pointer.TryGet(out var next)) pending.Push(next);
            }
            candidate = renderers > 0 || gameObject.m_Animator != null;
        }
        if (gameObject.m_Animator != null && gameObject.m_Animator.m_Avatar.TryGet(out var avatar)) linkedAvatar = Key(avatar);
        if (gameObject.m_Animator != null && gameObject.m_Animator.m_Controller.TryGet(out var controller))
        {
            linkedControllerKey = Key(controller);
            if (controller is AnimatorController actual)
            {
                controllerEvidence = "actual-animator-controller-reference";
                foreach (var pointer in actual.m_AnimationClips)
                    if (pointer.TryGet(out var referencedClip)) controllerClips.Add(Key(referencedClip));
            }
            else controllerEvidence = "unsupported-controller-type:" + controller.GetType().Name;
        }
        if (candidate)
        {
            rig = Path.Combine(output, "rig-" + key + ".json");
            try { RigEvidenceWriter.Write(gameObject, key, linkedAvatar, rig, Key); }
            catch (InvalidDataException error) { rig = null; rigError = error.Message; }
        }
    }
    if (item is Avatar sourceAvatar)
    {
        tos = Path.Combine(output, "avatar-" + key + ".json");
        File.WriteAllText(tos, JsonSerializer.Serialize(new { avatar = new { m_TOS = sourceAvatar.m_TOS } }));
        nodes = sourceAvatar.m_TOS.Count;
    }
    rows.Add(new { key, kind, name = item.Name ?? "", pathId = item.m_PathID.ToString(),
        sourceAsset = item.assetsFile.fileName, sourceBundle = item.assetsFile.originalPath,
        isModelCandidate = candidate, hierarchyRoot, parentGameObjectKey, rendererCount = renderers, boneReferenceCount = bones,
        hierarchyNodes = nodes, linkedAvatarKey = linkedAvatar, linkedControllerKey, controllerEvidence,
        controllerClipKeys = controllerClips.Distinct().Order().ToArray(), avatarTosPath = tos, rigEvidencePath = rig, rigEvidenceError = rigError,
        bindingPathHashes = item is AnimationClip sourceClip ? sourceClip.m_ClipBindingConstant?.genericBindings?
            .Where(binding => binding.typeID == ClassIDType.Transform).Select(binding => binding.path).Distinct().Order().ToArray() ?? [] : Array.Empty<uint>() });
}
File.WriteAllText(reportPath, JsonSerializer.Serialize(new { schema = "endfield-object-inventory-v1", input,
    objectCount = all.Length, loadedAssets = manager.assetsFileList.Count, objects = rows,
    decodedAnimations = false, playbackVerified = false }));
Console.WriteLine($"Read {all.Length} objects; listed {rows.Count}; report={reportPath}");
return 0;

static string Key(AnimeStudio.Object item) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
    item.assetsFile.originalPath + "\n" + item.assetsFile.fileName + "\n" + item.m_PathID)))[..24];
