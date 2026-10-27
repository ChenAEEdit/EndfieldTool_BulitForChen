using System.Text.RegularExpressions;
using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

public sealed record ResolvedCharacterAssociation(BundleObjectRow Model, BundleObjectRow Avatar,
    IReadOnlyList<BundleObjectRow> Clips, string ControllerKey, int ReferencedClipCount);

public static class CharacterAssociationResolver
{
    public static ResolvedCharacterAssociation Resolve(ObjectInventory inventory, string role)
    {
        if (string.IsNullOrWhiteSpace(role) || !Regex.IsMatch(role, "^[a-z0-9_]+$"))
            throw new InvalidDataException("角色标识格式无效，无法安全关联模型与动画。");

        var byKey = inventory.Objects.ToDictionary(row => row.Key, StringComparer.Ordinal);
        string modelPattern = "^chr_[0-9]+_" + Regex.Escape(role) + "_uimodel$";
        var models = inventory.Objects.Where(row => row.Kind == "GameObject" && row.IsModelCandidate
            && row.HierarchyRoot && row.BoneReferenceCount > 0
            && Regex.IsMatch(row.Name, modelPattern, RegexOptions.IgnoreCase)).ToArray();
        if (models.Length != 1) throw new InvalidDataException(models.Length == 0
            ? "未找到具有骨骼引用的独立角色模型根节点；请检查对象列表或手动选择。"
            : $"找到 {models.Length} 个独立角色模型候选，存在歧义；已停止自动选择。");

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

        return new(model, avatar, [Exact("overview_loop"), Exact("relax_loop")],
            model.LinkedControllerKey, referenced.Length);
    }
}
