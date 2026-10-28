using System.Text.RegularExpressions;
using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

public sealed record ResolvedCharacterAssociation(BundleObjectRow Model, BundleObjectRow Avatar,
    IReadOnlyList<BundleObjectRow> Clips, string ControllerKey, int ReferencedClipCount)
{
    public IReadOnlyList<BundleObjectRow> RelatedModels { get; init; } = [];
}

public static class CharacterAssociationResolver
{
    public static ResolvedCharacterAssociation Resolve(ObjectInventory inventory, string role)
        => Resolve(inventory, role, includeSpecialIdle: false);

    public static ResolvedCharacterAssociation Resolve(ObjectInventory inventory, string role, bool includeSpecialIdle)
    {
        if (string.IsNullOrWhiteSpace(role) || !Regex.IsMatch(role, "^[a-z0-9_]+$"))
            throw new InvalidDataException("角色标识格式无效，无法安全关联模型与动画。");

        var byKey = inventory.Objects.ToDictionary(row => row.Key, StringComparer.Ordinal);
        string modelPattern = "^chr_[0-9]+_" + Regex.Escape(role) + "_uimodel$";
        var models = inventory.Objects.Where(row => row.Kind == "GameObject" && row.IsModelCandidate
            && row.HierarchyRoot && row.BoneReferenceCount > 0
            && Regex.IsMatch(row.Name, modelPattern, RegexOptions.IgnoreCase)).ToArray();
        if (models.Length == 0)
        {
            var alternatives = inventory.Objects.Where(row => row.Kind == "GameObject" && row.IsModelCandidate
                && row.HierarchyRoot && row.BoneReferenceCount > 0
                && Regex.IsMatch(row.Name, "^chr_[0-9]+_" + Regex.Escape(role) + "_(?:post)?model$", RegexOptions.IgnoreCase)).ToArray();
            throw new InvalidDataException(alternatives.Length == 0
                ? "未找到具有骨骼引用的独立角色本体；请检查模型发现记录。"
                : $"未找到具有可验证待机引用链的 UI 本体；发现 {alternatives.Length} 个独立 model/postmodel 候选，不能替代确认。"
                    + (alternatives.All(row => string.IsNullOrEmpty(row.LinkedControllerKey)) ? "这些候选均没有实际控制器引用。" : "候选引用仍需核对。"));
        }
        if (models.Length != 1) throw new InvalidDataException($"找到 {models.Length} 个独立角色本体候选，存在歧义；已停止自动选择。有关联证据的道具子模型不计作本体歧义。");

        var model = models[0];
        if (!string.IsNullOrEmpty(model.ParentGameObjectKey))
            throw new InvalidDataException("模型报告为独立根节点，但仍有父对象引用；已停止自动选择。");
        if (string.IsNullOrEmpty(model.LinkedAvatarKey) || !byKey.TryGetValue(model.LinkedAvatarKey, out var avatar)
            || avatar.Kind != "Avatar")
            throw new InvalidDataException("模型没有可验证的 Animator Avatar 引用；已停止自动选择。");
        if (string.IsNullOrEmpty(model.LinkedControllerKey)
            || model.ControllerEvidence != "actual-animator-controller-reference")
            throw new InvalidDataException("模型没有受支持的实际 AnimatorController 引用；已停止自动选择。");
        if (model.ControllerClipKeys.Count == 0 || model.ControllerClipKeys.Distinct(StringComparer.Ordinal).Count() != model.ControllerClipKeys.Count
            || model.ControllerClipKeys.Any(key => !byKey.TryGetValue(key, out var clip) || clip.Kind != "AnimationClip"))
            throw new InvalidDataException("AnimatorController 引用的动画不完整或无效；已停止自动选择。");

        var referenced = model.ControllerClipKeys.Select(key => byKey[key]).ToArray();
        BundleObjectRow Exact(string action)
        {
            var pattern = "^A_actor_" + Regex.Escape(role) + "_ui_" + action + "(?:_[0-9]+)?$";
            var matches = referenced.Where(row => Regex.IsMatch(row.Name, pattern, RegexOptions.IgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException(matches.Length == 0
                ? $"控制器引用中没有唯一的 {action} 专属待机动画；已停止自动选择。"
                : $"控制器引用中有多个 {action} 专属待机候选；已停止自动选择。");
            return matches[0];
        }

        bool DescendsFromBody(BundleObjectRow row)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(row.ParentGameObjectKey))
            {
                if (!visited.Add(row.Key)) throw new InvalidDataException("模型组成父层级存在循环。");
                if (row.ParentGameObjectKey == model.Key) return true;
                if (!byKey.TryGetValue(row.ParentGameObjectKey, out row!)) return false;
            }
            return false;
        }
        var idleClips = new List<BundleObjectRow> { Exact("overview_loop"), Exact("relax_loop") };
        var specials = referenced.Where(row => Regex.IsMatch(row.Name,
            "^A_actor_" + Regex.Escape(role) + "_ui_(?:relax_(?:sp_[0-9]+|react)|idle_to_relax)$", RegexOptions.IgnoreCase)).ToArray();
        if (includeSpecialIdle && specials.GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("控制器引用的专属待机特殊片段存在歧义；已停止自动选择。");
        if (includeSpecialIdle) idleClips.AddRange(specials.OrderBy(row => row.Name, StringComparer.Ordinal));
        return new(model, avatar, idleClips,
            model.LinkedControllerKey, referenced.Length) {
                RelatedModels = inventory.Objects.Where(row => row.IsModelCandidate && row.Key != model.Key && DescendsFromBody(row)).ToArray()
            };
    }
}
