using System.Text;
using System.Text.Json;
using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

public static class UnityDiagnosticReport
{
    internal static JsonElement ReadVerifiedReport(SavedPlan saved)
    {
        saved = UnityTaskTarget.Resolve(saved);
        var result = UnityAdaptationResults.ValidatePreparation(saved);
        string config = UnityProjectFiles.Within(saved.Plan.Options.UnityProject, result.UnityAssetRoot + "/adaptation.json");
        string file = UnityProjectFiles.Within(saved.Plan.Options.UnityProject, result.UnityAssetRoot + "/Generated/adaptation-result.json");
        var report = JsonFiles.Read<JsonElement>(file);
        if (UnityProjectFiles.Hash(config, CancellationToken.None) != result.UnityConfigSha256 || report.GetProperty("schema").GetString() != "endfield-unity-adaptation-v1"
            || report.GetProperty("taskId").GetString() != saved.Plan.Id.ToString() || report.GetProperty("runId").GetString() != result.RunId
            || report.GetProperty("configSha256").GetString() != result.UnityConfigSha256) throw new InvalidDataException("轨道报告不属于当前任务和配置。");
        return report;
    }
    public static string Export(SavedPlan saved)
    {
        saved = UnityTaskTarget.Resolve(saved);
        var result = UnityAdaptationResults.ValidatePreparation(saved);
        string path = UnityProjectFiles.Within(saved.Plan.Options.UnityProject, result.UnityAssetRoot + "/Generated/adaptation-result.json");
        var report = ReadVerifiedReport(saved);
        if (report.GetProperty("taskId").GetString() != saved.Plan.Id.ToString() || report.GetProperty("runId").GetString() != result.RunId
            || report.GetProperty("configSha256").GetString() != result.UnityConfigSha256) throw new InvalidDataException("诊断报告来源不符。");
        var text = new StringBuilder("# Unity 动画适配诊断\n\n");
        text.AppendLine("任务：" + saved.Plan.CharacterName + "；运行：" + result.RunId + "\n");
        text.AppendLine("生成状态：`" + Value(report, "status") + "`。本报告不等于实际播放验证。\n");
        text.AppendLine("模型导入倍率：" + Value(report, "modelImportScale") + "；动画位置倍率：" + Value(report, "positionScale") + "。倍率是本次设置，仍需结合角色实际尺寸判断。\n");
        if (report.TryGetProperty("diagnostics", out var diagnostics) && diagnostics.ValueKind == JsonValueKind.Object)
        {
            text.AppendLine("## 模型、骨架与导入权重\n\n| 项目 | 数量／结果 |\n| --- | ---: |");
            foreach (var item in new[] { ("meshes", "蒙皮网格"), ("vertices", "导入顶点"), ("bones", "实际引用骨骼"),
                ("missingBones", "缺失骨骼引用"), ("unweightedVertices", "无权重顶点"), ("invalidWeights", "非法权重或骨骼索引"),
                ("nonNormalizedVertices", "权重和偏离 1 超过 1% 的顶点"),
                ("nonFiniteVertices", "非有限顶点"), ("duplicatePaths", "重复层级路径"), ("missingBaseMaps", "缺图或基础替代材质"), ("geometryPassed", "上述几何检查通过") })
                text.AppendLine("| " + item.Item2 + " | " + Value(diagnostics, item.Item1) + " |");
            if (diagnostics.TryGetProperty("warnings", out var warnings))
                foreach (var warning in warnings.EnumerateArray()) text.AppendLine("\n- " + Escape(warning.GetString() ?? ""));
        }
        text.AppendLine("\n人形映射来源：" + Escape(Value(report, "humanMappingSource")) + "\n");
        text.AppendLine("\n## 动画绑定\n\n| 片段 | 人形播放 | 动态肌肉 | 主体旋转曲线 | 保留曲线 | 未匹配路径 | 结果／错误 |\n| --- | --- | ---: | ---: | ---: | ---: | --- |");
        if (report.TryGetProperty("clips", out var clips) && clips.ValueKind == JsonValueKind.Array)
            foreach (var clip in clips.EnumerateArray())
                text.AppendLine("| " + Escape(Value(clip, "name")) + " | " + Value(clip, "humanoid") + " | " + Value(clip, "movingMuscles")
                    + " | " + Value(clip, "movingBodyRotationChannels") + " | " + Value(clip, "retainedBindings") + " | "
                    + (clip.TryGetProperty("unmatched", out var unknown) ? unknown.GetArrayLength() : 0) + " | "
                    + Escape(Value(clip, "adapted") + " " + Value(clip, "error")) + " |");
        text.AppendLine("\n## 未输出的具体轨道\n\n逐条保留完整源路径、属性和原因；片段失败时，拟保留的轨道也没有生成输出。全部对应记录可在工作台“轨道明细”页取消“只看未输出”查看。\n");
        foreach (var clip in report.GetProperty("clips").EnumerateArray())
        {
            text.AppendLine("\n### " + Escape(Value(clip, "name")) + "\n\n源骨架：" + Escape(Value(clip, "sourceRigName")) + "；换算：" + Escape(Value(clip, "retargetMode")) + "；生成：" + Value(clip, "adapted") + "。\n");
            if (!clip.TryGetProperty("tracks", out var tracks)) { text.AppendLine("旧版本没有逐轨道记录。\n"); continue; }
            bool adapted = clip.GetProperty("adapted").GetBoolean();
            text.AppendLine("| 源哈希 | 源路径 | 属性／组件 | 目标路径 | 处理计划 | 原因 |\n| --- | --- | --- | --- | --- | --- |");
            foreach (var row in tracks.EnumerateArray().Where(t => !adapted || Value(t, "decision") != "retain"))
                text.AppendLine("| " + Value(row, "sourceHash") + " | " + Escape(Value(row, "sourcePath")) + " | " + Escape(Value(row, "property") + " / " + Value(row, "component"))
                    + " | " + Escape(Value(row, "targetPath")) + " | " + Value(row, "decision") + " | " + Escape(TrackAuditPanel.Reason(Value(row, "reason"))) + " |");
        }
        text.AppendLine("\n## 基础预览材质\n\n| 材质 | 已绑定基础贴图 | 贴图 |\n| --- | --- | --- |");
        if (report.TryGetProperty("materials", out var materials) && materials.ValueKind == JsonValueKind.Array)
            foreach (var material in materials.EnumerateArray())
                text.AppendLine("| " + Escape(Value(material, "source")) + " | " + Value(material, "textured") + " | " + Escape(Value(material, "baseMap")) + " |");
        text.AppendLine("\n## 生成错误\n\n" + Escape(Value(report, "error")));
        text.AppendLine("\n基础预览使用导出材质属性中的真实贴图槽位，不能代表原游戏 Shader、实时布料、IK 或所有关节姿势已正确还原。");
        string output = Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "objects", result.RunId, "diagnostics.md");
        UnityProjectFiles.NoLinks(output); File.WriteAllText(output, text.ToString()); return output;
    }
    private static string Value(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";
    private static string Escape(string value) => value.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
}
