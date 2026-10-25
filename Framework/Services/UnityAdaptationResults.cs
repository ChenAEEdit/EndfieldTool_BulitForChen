using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

public static class UnityAdaptationResults
{
    public static async Task<ObjectOperationResult> ImportAsync(SavedPlan saved, CancellationToken token)
    {
        var taskRoot = Path.GetDirectoryName(saved.FilePath)!;
        using var taskLock = new FileStream(Path.Combine(taskRoot, "extraction.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        taskLock.Lock(0, 1);
        try
        {
            var result = ObjectWorkflow.ReadPreparation(saved) ?? throw new InvalidDataException("请先生成 Unity 适配包。");
            if (result.Mode != "prepare" || result.Status is not ("unity_prepared" or "adapted_unverified")
                || result.UnityAssetRoot == null || !Regex.IsMatch(result.UnityAssetRoot, "^Assets/EndfieldCharacterExports/[0-9a-f]{8}_[0-9a-f]{32}$"))
                throw new InvalidDataException("当前任务最近一次对象操作不是有效的适配包。");
            var bundle = BundleExtractionBackend.ReadResult(saved);
            if (bundle?.Status != "bundles_extracted" || bundle.RunId != result.BundleRunId)
                throw new InvalidDataException("Bundle 已变化，请重新生成适配包。");
            using (var planStream = File.OpenRead(saved.FilePath))
                if (bundle.Extra == null || !bundle.Extra.TryGetValue("planSha256", out var planHash)
                    || planHash.GetString() != Convert.ToHexString(SHA256.HashData(planStream)))
                    throw new InvalidDataException("计划已变化，请重新提取并生成适配包。");
            string project = Path.GetFullPath(saved.Plan.Options.UnityProject);
            string generated = Path.GetFullPath(Path.Combine(project, result.UnityAssetRoot, "Generated"));
            string reportPath = Path.Combine(generated, "adaptation-result.json");
            if (!File.Exists(reportPath)) throw new InvalidDataException("没有找到 Unity 原生适配结果。请先复制适配包到计划指定的 Unity 项目并执行生成菜单。");
            var report = JsonFiles.Read<NativeReport>(reportPath);
            if (report.Schema != "endfield-unity-adaptation-v1" || report.TaskId != saved.Plan.Id.ToString()
                || report.RunId != result.RunId || report.ConfigSha256 != result.UnityConfigSha256
                || report.Status != "adapted_unverified" || !report.Adapted || report.PlaybackVerified
                || report.Clips.Count != result.ExpectedAdaptedClips || report.Clips.Any(row => !row.Adapted))
                throw new InvalidDataException("Unity 适配未完成，或结果不属于本次适配包；请查看 Unity 报告。");
            if (report.Outputs.Count != report.Clips.Count + 2 || report.Outputs.Select(row => row.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != report.Outputs.Count)
                throw new InvalidDataException("Unity 适配输出不完整或重复。");
            var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in report.Outputs)
            {
                token.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(Path.Combine(project, file.Path));
                if (Path.IsPathRooted(file.Path) || !path.StartsWith(generated + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(path) || file.Bytes <= 0 || new FileInfo(path).Length != file.Bytes)
                    throw new InvalidDataException("Unity 适配输出文件、路径或长度无效。");
                string hash = await Task.Run(() => { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }, token);
                if (hash != file.Sha256) throw new InvalidDataException("Unity 适配输出已变化，摘要不符。");
                outputPaths.Add(Path.GetFileName(path));
            }
            if (!outputPaths.Contains("Character.prefab") || !outputPaths.Contains("Character.controller")
                || report.Clips.Any(clip => string.IsNullOrEmpty(clip.Output) || !outputPaths.Contains(Path.GetFileName(clip.Output))))
                throw new InvalidDataException("Unity 适配结果缺少控制器、Prefab 或动画。");
            token.ThrowIfCancellationRequested();
            result = result with { Status = "adapted_unverified", Adapted = true, PlaybackVerified = false,
                UnityReportPath = reportPath, Message = $"已读取 Unity 原生适配结果：{report.Clips.Count} 条动画；尚未验证播放。" };
            JsonFiles.Write(Path.Combine(taskRoot, "objects", result.RunId, "operation.json"), result);
            JsonFiles.Write(Path.Combine(taskRoot, "objects", "preparation.json"), result);
            if (ObjectWorkflow.ReadResult(saved)?.RunId == result.RunId)
                JsonFiles.Write(Path.Combine(taskRoot, "objects", "result.json"), result);
            return result;
        }
        finally { taskLock.Unlock(0, 1); }
    }

    private sealed record NativeReport
    {
        public string Schema { get; init; } = "";
        public string TaskId { get; init; } = "";
        public string RunId { get; init; } = "";
        public string ConfigSha256 { get; init; } = "";
        public string Status { get; init; } = "";
        public bool Adapted { get; init; }
        public bool PlaybackVerified { get; init; }
        public List<NativeClip> Clips { get; init; } = [];
        public List<ObjectOutputFile> Outputs { get; init; } = [];
    }
    private sealed record NativeClip
    {
        public bool Adapted { get; init; }
        public string Output { get; init; } = "";
    }
}
