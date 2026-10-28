using AnimeStudio;
using System.Collections;
using System.Text.Json;

internal static class SerializedEvidenceWriter
{
    public static void Write(AnimeStudio.Object[] objects, string output, Func<AnimeStudio.Object, string> key)
    {
        object? Ref<T>(PPtr<T> p) where T : AnimeStudio.Object
        {
            if (p.m_PathID == 0) return null;
            // Unsupported native components are parsed as Object, not Component.
            // Resolve their identity without the narrower PPtr<T> runtime cast.
            bool resolved = p.Cast<AnimeStudio.Object>().TryGet(out var value);
            return new { fileId = p.m_FileID, pathId = p.m_PathID.ToString(), key = resolved ? key(value) : null,
                name = resolved ? value.Name : null, kind = resolved ? value.type.ToString() : null };
        }
        var rows = new List<object>();
        foreach (var item in objects)
        {
            object? data = null;
            string? error = null;
            try
            {
                data = item switch
                {
                    GameObject go => new {
                        parent = go.m_Transform == null ? null : Ref(go.m_Transform.m_Father),
                        transformKey = go.m_Transform == null ? null : key(go.m_Transform),
                        children = go.m_Transform?.m_Children.Select(p => Ref(p)).ToArray(),
                        active = Flag(go, "m_IsActive"),
                        localPosition = go.m_Transform == null ? null : Vector(go.m_Transform.m_LocalPosition),
                        localRotation = go.m_Transform == null ? null : (object)new { x = go.m_Transform.m_LocalRotation.X, y = go.m_Transform.m_LocalRotation.Y, z = go.m_Transform.m_LocalRotation.Z, w = go.m_Transform.m_LocalRotation.W },
                        localScale = go.m_Transform == null ? null : Vector(go.m_Transform.m_LocalScale),
                        components = go.m_Components.Select(p => Ref(p)).ToArray(),
                        mesh = go.m_SkinnedMeshRenderer != null ? Ref(go.m_SkinnedMeshRenderer.m_Mesh) : go.m_MeshFilter != null ? Ref(go.m_MeshFilter.m_Mesh) : null,
                        materials = ((Renderer?)go.m_SkinnedMeshRenderer ?? go.m_MeshRenderer)?.m_Materials.Select(p => Ref(p)).ToArray(),
                        renderer = ((Renderer?)go.m_SkinnedMeshRenderer ?? go.m_MeshRenderer) is { } renderer ? new {
                            key = key(renderer), kind = renderer.GetType().Name, enabled = Flag(renderer, "m_Enabled")
                        } : null
                    },
                    AnimationClip clip => new {
                        events = clip.m_Events.Select(e => new { e.time, e.functionName, e.data, e.floatParameter, e.intParameter, e.messageOptions, objectReference = Ref(e.objectReferenceParameter) }).ToArray(),
                        bindings = clip.m_ClipBindingConstant?.genericBindings?.Select(b => new { b.path, b.attribute, type = b.typeID.ToString(), b.customType, b.isPPtrCurve, b.isIntCurve, script = Ref(b.script) }).ToArray()
                    },
                    AssetBundle bundle => new { container = bundle.m_Container.Select(pair => new { path = pair.Key, asset = Ref(pair.Value.asset) }).ToArray() },
                    MonoBehaviour behaviour => new { gameObject = Ref(behaviour.m_GameObject), script = Ref(behaviour.m_Script), serialized = Normalize(item.ToType(), 0), references = References(item, key) },
                    AnimatorController => new { serialized = Normalize(item.ToType(), 0), references = References(item, key) },
                    _ when item.type == ClassIDType.LODGroup => new { serialized = Normalize(item.ToType(), 0), references = References(item, key) },
                    _ when item.type is ClassIDType.ParticleSystem or ClassIDType.ParticleSystemRenderer or ClassIDType.PlayableDirector =>
                        new { serialized = ComponentSummary(item), references = References(item, key), runtimeReconstructionSupported = false },
                    _ => null
                };
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            if (data != null || error != null)
                rows.Add(new { key = key(item), name = item.Name, kind = item.type.ToString(), sourceAsset = item.assetsFile.fileName,
                    sourceBundle = item.assetsFile.originalPath, pathId = item.m_PathID.ToString(), data, error });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { schema = "endfield-serialized-evidence-v1", objects = rows }));
    }

    static object Vector(AnimeStudio.Vector3 value) => new { x = value.X, y = value.Y, z = value.Z };
    static object ComponentSummary(AnimeStudio.Object value)
    {
        var data = value.ToType();
        var result = new Dictionary<string, object?>();
        foreach (string name in new[] { "m_GameObject", "m_Enabled", "m_Mesh", "m_Mesh1", "m_Mesh2", "m_Mesh3", "m_Materials", "m_RenderMode", "lengthInSec" })
            if (data != null && data.Contains(name)) result[name] = Normalize(data[name], 0);
        return result;
    }
    static bool? Flag(AnimeStudio.Object value, string name)
    {
        var data = value.ToType();
        return data != null && data.Contains(name) ? Convert.ToBoolean(data[name]) : null;
    }
    static object[] References(AnimeStudio.Object owner, Func<AnimeStudio.Object, string> key)
    {
        var result = new List<object>();
        void Walk(object? value, string path)
        {
            if (value is IDictionary data)
            {
                if (data.Contains("m_FileID") && data.Contains("m_PathID"))
                {
                    long id = Convert.ToInt64(data["m_PathID"]);
                    if (id == 0) return;
                    var pointer = new PPtr<AnimeStudio.Object>(Convert.ToInt32(data["m_FileID"]), id, owner.assetsFile);
                    result.Add(new { path, fileId = pointer.m_FileID, pathId = id.ToString(), key = pointer.TryGet(out var item) ? key(item) : null,
                        name = pointer.TryGet(out item) ? item.Name : null, kind = pointer.TryGet(out item) ? item.type.ToString() : null });
                    return;
                }
                foreach (DictionaryEntry entry in data) Walk(entry.Value, path + "/" + entry.Key);
            }
            else if (value is IEnumerable items && value is not string)
            { int index = 0; foreach (var item in items) Walk(item, path + "/" + index++); }
        }
        Walk(owner.ToType(), "");
        return result.ToArray();
    }
    static object? Normalize(object? value, int depth)
    {
        if (depth > 64) throw new InvalidDataException("Serialized evidence nesting limit exceeded.");
        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>();
            foreach (DictionaryEntry row in dictionary) result.Add(row.Key.ToString()!, Normalize(row.Value, depth + 1));
            return result;
        }
        if (value is string || value == null) return value;
        if (value is IEnumerable list)
        {
            var result = new List<object?>();
            foreach (var row in list)
            {
                if (result.Count >= 100000) throw new InvalidDataException("Serialized evidence array limit exceeded.");
                result.Add(Normalize(row, depth + 1));
            }
            return result;
        }
        if (value is float f && !float.IsFinite(f)) return f.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is double d && !double.IsFinite(d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return value;
    }
}
