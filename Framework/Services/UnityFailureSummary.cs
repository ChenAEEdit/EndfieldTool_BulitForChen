using System.Text.Json;
using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

public sealed record UnityClipProblem(string Name, string Error, int Unmatched);
public sealed record UnityFailureSummary(Guid TaskId, string RunId, string Project, List<string> ConvertedKeys,
    List<UnityClipProblem> Problems, string Error, bool PreviewCreated)
{
    public string Describe() => $"当前适配包报告：{ConvertedKeys.Count} 条已转换，{Problems.Count} 条未完成。\r\n"
        + (PreviewCreated ? "已生成预览；完整文件核对与播放验证状态另行显示。" : "本次未生成预览场景。")
        + "\r\n" + Error + "\r\n" + string.Join("\r\n", Problems.Select(row =>
            $"• {row.Name}：{row.Error}" + (row.Unmatched > 0 ? $"（{row.Unmatched} 个未匹配路径）" : "")))
        + "\r\n报告中的转换状态不等于已核对完整输出或验证播放。";

    public static UnityFailureSummary? Read(SavedPlan original)
    {
        var saved = UnityTaskTarget.Resolve(original);
        var preparation = UnityAdaptationResults.ValidatePreparation(saved);
        string assetRoot = UnityProjectFiles.Within(saved.Plan.Options.UnityProject, preparation.UnityAssetRoot!);
        string configPath = UnityProjectFiles.Within(assetRoot, "adaptation.json");
        string reportPath = UnityProjectFiles.Within(assetRoot, "Generated/adaptation-result.json");
        if (!File.Exists(reportPath)) return null;
        if (UnityProjectFiles.Hash(configPath, CancellationToken.None) != preparation.UnityConfigSha256)
            throw new InvalidDataException("项目配置摘要不符，不能据此选择重试动画。");
        var report = JsonFiles.Read<JsonElement>(reportPath);
        var config = JsonFiles.Read<JsonElement>(configPath);
        if (report.GetProperty("schema").GetString() != "endfield-unity-adaptation-v1"
            || report.GetProperty("taskId").GetString() != saved.Plan.Id.ToString()
            || report.GetProperty("runId").GetString() != preparation.RunId
            || report.GetProperty("configSha256").GetString() != preparation.UnityConfigSha256)
            throw new InvalidDataException("生成报告不属于当前任务及适配包。");
        var converted = new List<string>();
        var problems = new List<UnityClipProblem>();
        var rows = report.GetProperty("clips").EnumerateArray().ToList();
        foreach (var clip in config.GetProperty("clips").EnumerateArray())
        {
            string id = clip.GetProperty("id").GetString()!, name = clip.GetProperty("name").GetString()!;
            // Match both identity and output, not just a possibly repeated clip name.
            var matches = rows.Where(row => row.GetProperty("name").GetString() == name
                && row.GetProperty("output").GetString() == preparation.UnityAssetRoot + "/Generated/Clip_" + id + ".anim").ToList();
            if (matches.Count == 1 && matches[0].GetProperty("adapted").GetBoolean()) converted.Add(id);
            else
            {
                var failed = rows.Where(row => row.GetProperty("name").GetString() == name).ToList();
                var row = matches.Count == 1 ? matches[0] : failed.Count == 1 ? failed[0] : default;
                problems.Add(new(name, row.ValueKind == JsonValueKind.Undefined ? "尚未执行或无法确认结果。" : row.GetProperty("error").GetString() ?? "未完成。",
                    row.ValueKind != JsonValueKind.Undefined && row.TryGetProperty("unmatched", out var unknown) ? unknown.GetArrayLength() : 0));
            }
        }
        return new(saved.Plan.Id, preparation.RunId, saved.Plan.Options.UnityProject, converted, problems,
            report.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "",
            report.TryGetProperty("previewCreated", out var preview) && preview.GetBoolean());
    }
}
