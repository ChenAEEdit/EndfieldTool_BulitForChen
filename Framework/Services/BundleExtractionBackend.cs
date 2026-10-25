using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

public sealed class BundleExtractionBackend : IExtractionBackend
{
    public string Id => "vfs-bundles-v1";

    public bool Supports(ExtractionPlan plan) => plan.Schema == "endfield-character-plan-v1"
        && plan.Bundles.Count > 0 && plan.GameAssemblySha256.Length == 64;

    public async Task<BundleExtractionResult> ExecuteAsync(SavedPlan saved,
        IProgress<ExtractionProgress> progress, CancellationToken cancellationToken)
    {
        var plan = JsonFiles.Read<ExtractionPlan>(saved.FilePath);
        if (plan.Id != saved.Plan.Id || !Supports(plan)) throw new InvalidDataException("计划不受支持或内容已变化，请重新保存计划。");
        var root = new WorkspaceStore().Prepare(plan.Options);
        var taskDirectory = Path.Combine(root, "tasks", plan.Id.ToString("N"));
        if (!Path.GetFullPath(saved.FilePath).Equals(Path.Combine(taskDirectory, "plan.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("计划文件不在对应的工作区任务目录。");
        var python = MainForm.FindPython(plan.Options.Blender);
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "extract_plan_bundles.py");
        if (!File.Exists(python) || !File.Exists(script)) throw new InvalidDataException("缺少 Python 或提取脚本，请使用完整工作台并填写 Blender 路径。");
        var runId = Guid.NewGuid().ToString("N");
        var runDirectory = Path.Combine(taskDirectory, "runs", runId);
        Directory.CreateDirectory(runDirectory);
        var resultPath = Path.Combine(taskDirectory, "result.json");
        var logPath = Path.Combine(runDirectory, "extraction.log");
        using var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var argument in new[] { script, "--plan", saved.FilePath, "--run-id", runId }) start.ArgumentList.Add(argument);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("无法启动提取程序。");
        using var cancellation = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stderr = new StringBuilder();
        async Task ReadErrors()
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                stderr.AppendLine(line);
                if (stderr.Length > 8000) stderr.Remove(0, stderr.Length - 4000);
            }
        }
        var errorTask = ReadErrors();
        bool completed = false;
        bool succeeded = false;
        string? latestError = null;
        string? backendError = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!line.StartsWith("@event ", StringComparison.Ordinal)) { log.WriteLine(line); continue; }
                using var document = JsonDocument.Parse(line[7..]);
                var row = document.RootElement;
                string message = row.GetProperty("message").GetString() ?? "";
                log.WriteLine(DateTimeOffset.Now.ToString("O") + " " + message);
                if (row.TryGetProperty("progress", out var percent)) progress.Report(new(message, Math.Clamp(percent.GetInt32(), 0, 100)));
                var kind = row.GetProperty("type").GetString();
                if (kind == "error") latestError = message;
                if (kind == "completed") completed = true;
            }
            await process.WaitForExitAsync();
            await errorTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !completed)
                throw new IOException((latestError ?? ("提取进程未完成。" + stderr)) + "\n日志：" + logPath);
            var result = ReadResult(saved) ?? throw new InvalidDataException("本次提取未生成结果记录。");
            if (result.RunId != runId || result.Status != "bundles_extracted" || result.Files.Count != result.TotalBundles
                || result.TotalBundles <= 0) throw new InvalidDataException("本次提取结果不完整或不属于本次执行。");
            var expectedOutput = Path.Combine(taskDirectory, "bundles");
            if (!Path.GetFullPath(result.OutputDirectory).Equals(expectedOutput, StringComparison.OrdinalIgnoreCase)
                || result.Files.Any(file => !Path.GetFullPath(file.Path).StartsWith(expectedOutput + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(file.Path) || new FileInfo(file.Path).Length != file.Length))
                throw new InvalidDataException("输出文件位置或长度与本次结果不符。");
            succeeded = true;
            return result;
        }
        catch (Exception error) { backendError = error.Message; throw; }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await errorTask;
            if (stderr.Length > 0) log.WriteLine(stderr.ToString());
            // Amend only this run's in-progress record; another process's result is never overwritten.
            BundleExtractionResult? current = null;
            try { current = ReadResult(saved); }
            catch (Exception error) { log.WriteLine("结果记录无法读取：" + error.Message); }
            if (!succeeded && current != null && current.RunId == runId && (current.Status is "running" or "bundles_extracted"))
            {
                string state = cancellationToken.IsCancellationRequested ? "cancelled" : "failed";
                current = current with { Status = state, BundleExtraction = state, FinishedAt = DateTimeOffset.UtcNow,
                    Message = state == "cancelled" ? "提取已取消；再次执行会重新校验并复用完整文件。" : latestError ?? backendError ?? "提取中断，详见运行日志。" };
                JsonFiles.Write(Path.Combine(runDirectory, "result.json"), current);
                JsonFiles.Write(resultPath, current);
            }
        }
    }

    public static BundleExtractionResult? ReadResult(SavedPlan saved)
    {
        var path = Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "result.json");
        if (!File.Exists(path)) return null;
        var result = JsonFiles.Read<BundleExtractionResult>(path);
        if (result.Schema != "endfield-bundle-extraction-v1" || result.TaskId != saved.Plan.Id)
            throw new InvalidDataException("提取结果不属于这个任务。");
        return result;
    }
}
