using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record Association(string ModelKey, string AvatarKey, string ControllerKey, List<string> ClipKeys, int ReferencedClips,
    string Evidence = "standalone-model-root -> actual Animator Avatar and Controller -> exact referenced AnimationClip objects");

internal static class AssociationResolver
{
    public static Association Resolve(JsonElement inventory, string role)
    {
        var objects = inventory.GetProperty("objects").EnumerateArray().ToArray();
        var byKey = objects.ToDictionary(o => Text(o, "key"), StringComparer.Ordinal);
        var models = objects.Where(o => Text(o, "kind") == "GameObject" && Bool(o, "isModelCandidate")
            && Bool(o, "hierarchyRoot") && o.GetProperty("boneReferenceCount").GetInt32() > 0
            && Regex.IsMatch(Text(o, "name"), "^chr_[0-9]+_" + Regex.Escape(role) + "_uimodel$")).ToArray();
        if (models.Length != 1) throw new InvalidDataException("absent-or-ambiguous-model");
        var model = models.Single();
        if (Text(model, "parentGameObjectKey").Length > 0) throw new InvalidDataException("contradictory-hierarchy-root");
        string avatar = Text(model, "linkedAvatarKey");
        if (!byKey.TryGetValue(avatar, out var avatarObject) || Text(avatarObject, "kind") != "Avatar") throw new InvalidDataException("broken-avatar-reference");
        string controller = Text(model, "linkedControllerKey");
        if (controller.Length == 0 || Text(model, "controllerEvidence") != "actual-animator-controller-reference") throw new InvalidDataException("missing-controller-evidence");
        var references = model.GetProperty("controllerClipKeys").EnumerateArray().Select(c => c.GetString()!).ToHashSet(StringComparer.Ordinal);
        if (references.Count == 0 || references.Any(key => !byKey.TryGetValue(key, out var clip) || Text(clip, "kind") != "AnimationClip")) throw new InvalidDataException("missing-controller-clip");
        var chosen = new List<string>();
        foreach (var action in new[] { "overview_loop", "relax_loop" })
        {
            var matches = references.Select(key => byKey[key]).Where(o => Regex.IsMatch(Text(o, "name"), "^A_actor_" + Regex.Escape(role) + "_ui_" + action + "(?:_[0-9]+)?$")).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("absent-or-ambiguous-idle-action:" + action);
            chosen.Add(Text(matches.Single(), "key"));
        }
        return new(Text(model, "key"), avatar, controller, chosen, references.Count);
    }

    static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
    static bool Bool(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.True;
}
