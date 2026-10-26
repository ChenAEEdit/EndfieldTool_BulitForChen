using AnimeStudio;
using System.Text.Json;
using NMatrix = System.Numerics.Matrix4x4;
using NVector = System.Numerics.Vector3;
using NQuat = System.Numerics.Quaternion;

internal static class RigEvidenceWriter
{
    private sealed record Node(Transform Transform, string Path, string? Parent, NMatrix SerializedWorld);
    private static NMatrix Local(Transform t) => NMatrix.CreateScale(t.m_LocalScale.X, t.m_LocalScale.Y, t.m_LocalScale.Z)
        * NMatrix.CreateFromQuaternion(new NQuat(t.m_LocalRotation.X, t.m_LocalRotation.Y, t.m_LocalRotation.Z, t.m_LocalRotation.W))
        * NMatrix.CreateTranslation(t.m_LocalPosition.X, t.m_LocalPosition.Y, t.m_LocalPosition.Z);
    // This parser's mesh bind matrices carry translation in M30/M31/M32.
    // Preserve their row layout; transposing loses bone lengths on decomposition.
    private static NMatrix Matrix(AnimeStudio.Matrix4x4 m) => new(m.M00, m.M01, m.M02, m.M03, m.M10, m.M11, m.M12, m.M13,
        m.M20, m.M21, m.M22, m.M23, m.M30, m.M31, m.M32, m.M33);
    private static object Vector(NVector v) => new { x = v.X, y = v.Y, z = v.Z };
    private static object Rotation(NQuat q) => new { x = q.X, y = q.Y, z = q.Z, w = q.W };
    private static bool Equivalent(NMatrix a, NMatrix b)
    {
        if (!NMatrix.Decompose(a, out var sa, out var qa, out var pa) || !NMatrix.Decompose(b, out var sb, out var qb, out var pb)) return false;
        return NVector.Distance(pa, pb) < .0001f && NVector.Distance(sa, sb) < .001f && Math.Abs(NQuat.Dot(NQuat.Normalize(qa), NQuat.Normalize(qb))) > .999999f;
    }
    public static void Write(GameObject root, string key, string? linkedAvatar, string output, Func<AnimeStudio.Object, string> identity)
    {
        var nodes = new Dictionary<string, Node>();
        void Visit(Transform transform, string path, string? parent, NMatrix parentWorld)
        {
            string id = identity(transform);
            var world = parent == null ? NMatrix.Identity : Local(transform) * parentWorld;
            if (!nodes.TryAdd(id, new(transform, path, parent, world))) throw new InvalidDataException("Cyclic or multiply referenced source hierarchy");
            foreach (var pointer in transform.m_Children)
                if (pointer.TryGet(out var child) && child.m_GameObject.TryGet(out var go)) Visit(child, path.Length == 0 ? go.m_Name : path + "/" + go.m_Name, path, world);
        }
        if (root.m_Transform != null) Visit(root.m_Transform, "", null, NMatrix.Identity);
        var duplicate = nodes.Values.GroupBy(n => n.Path, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) throw new InvalidDataException("duplicate-hierarchy-path:" + duplicate.Key);
        var candidates = new Dictionary<string, List<NMatrix>>();
        var rawSkinBinds = new List<object>();
        var warnings = new List<string>();
        foreach (var renderer in nodes.Values)
        {
            if (!renderer.Transform.m_GameObject.TryGet(out var go) || go.m_SkinnedMeshRenderer == null) continue;
            var skin = go.m_SkinnedMeshRenderer;
            if (!skin.m_Mesh.TryGet(out var mesh) || mesh.m_BindPose == null) continue;
            if (skin.m_Bones.Count != mesh.m_BindPose.Length) { warnings.Add("bone-bind-count-mismatch:" + renderer.Path); continue; }
            for (int i = 0; i < skin.m_Bones.Count; i++)
            {
                if (!skin.m_Bones[i].TryGet(out var bone)) continue;
                string boneKey = identity(bone);
                if (!nodes.ContainsKey(boneKey)) { warnings.Add("bone-outside-rig:" + boneKey); continue; }
                var original = mesh.m_BindPose[i];
                rawSkinBinds.Add(new { bone = nodes[boneKey].Path, renderer = renderer.Path,
                    parserFields = new[] { original.M00, original.M01, original.M02, original.M03, original.M10, original.M11, original.M12, original.M13,
                        original.M20, original.M21, original.M22, original.M23, original.M30, original.M31, original.M32, original.M33 } });
                if (!NMatrix.Invert(Matrix(mesh.m_BindPose[i]), out var bind)) { warnings.Add("singular-bind:" + boneKey); continue; }
                // Row-vector System.Numerics equivalent of rendererWorld * bind.inverse.
                var world = bind * renderer.SerializedWorld;
                if (!candidates.TryGetValue(boneKey, out var list)) candidates[boneKey] = list = [];
                list.Add(world);
            }
        }
        var bindWorld = new Dictionary<string, NMatrix>();
        var conflicts = new HashSet<string>();
        foreach (var pair in candidates)
        {
            if (pair.Value.All(m => Equivalent(m, pair.Value[0]))) bindWorld[pair.Key] = pair.Value[0];
            else { conflicts.Add(pair.Key); warnings.Add("inconsistent-skin-bind:" + nodes[pair.Key].Path); }
        }
        var byPath = nodes.Values.ToDictionary(n => n.Path, StringComparer.Ordinal);
        var outputNodes = new List<object>();
        int resolved = 0;
        foreach (var pair in nodes)
        {
            var node = pair.Value;
            var transform = node.Transform;
            var position = new NVector(transform.m_LocalPosition.X, transform.m_LocalPosition.Y, transform.m_LocalPosition.Z);
            var rotation = new NQuat(transform.m_LocalRotation.X, transform.m_LocalRotation.Y, transform.m_LocalRotation.Z, transform.m_LocalRotation.W);
            var scale = new NVector(transform.m_LocalScale.X, transform.m_LocalScale.Y, transform.m_LocalScale.Z);
            string reference = "serialized-local-only";
            if (node.Parent == null || node.Parent.Length == 0 && node.Path == "Root") reference = "rig-root-reference";
            if (bindWorld.TryGetValue(pair.Key, out var world))
            {
                var parentWorld = NMatrix.Identity;
                bool parentProven = node.Parent == null || node.Parent.Length == 0;
                if (node.Parent != null && byPath.TryGetValue(node.Parent, out var parent))
                {
                    parentProven = parent.Path.Length == 0 || parent.Path == "Root" || bindWorld.ContainsKey(identity(parent.Transform));
                    parentWorld = bindWorld.GetValueOrDefault(identity(parent.Transform), parent.SerializedWorld);
                }
                if (parentProven && NMatrix.Invert(parentWorld, out var inverseParent) && NMatrix.Decompose(world * inverseParent, out var s, out var q, out var p))
                { position = p; rotation = NQuat.Normalize(q); scale = s; reference = "skin-bind-pose"; resolved++; }
                else reference = "skin-bind-parent-unproven";
            }
            if (conflicts.Contains(pair.Key)) reference = "ambiguous-skin-bind-pose";
            outputNodes.Add(new { path = node.Path, parent = node.Parent, transformKey = pair.Key, position = Vector(position), rotation = Rotation(rotation), scale = Vector(scale),
                reference, serializedPosition = new { x = transform.m_LocalPosition.X, y = transform.m_LocalPosition.Y, z = transform.m_LocalPosition.Z },
                serializedRotation = new { x = transform.m_LocalRotation.X, y = transform.m_LocalRotation.Y, z = transform.m_LocalRotation.Z, w = transform.m_LocalRotation.W },
                serializedScale = new { x = transform.m_LocalScale.X, y = transform.m_LocalScale.Y, z = transform.m_LocalScale.Z } });
        }
        var clips = root.m_Animation?.m_Animations.Select(pointer => pointer.TryGet(out var clip)
            ? new { key = identity(clip), name = clip.m_Name, pathId = clip.m_PathID.ToString(), sourceAsset = clip.assetsFile.fileName, sourceBundle = clip.assetsFile.originalPath } : null).Where(clip => clip != null).ToArray();
        File.WriteAllText(output, JsonSerializer.Serialize(new { schema = "endfield-rig-evidence-v1", key, name = root.m_Name,
            sourceAsset = root.assetsFile.fileName, sourceBundle = root.assetsFile.originalPath, pathId = root.m_PathID.ToString(), linkedAvatar,
            nodes = outputNodes, referencedClips = clips ?? [], restPoseKind = "skin-bind-matrices-with-per-node-provenance", bindPoseNodes = resolved,
            warnings, rawSkinBinds, retargetingVerified = false }));
    }
}
