using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

public sealed class ObjectWorkflow
{
    public async Task<ObjectOperationResult> ExecuteAsync(SavedPlan saved, string mode, string? inventoryPath,
        ObjectExportSelection? selection, IProgress<ExtractionProgress> progress, CancellationToken token)
    {
        if (mode is not ("inspect" or "export" or "prepare")) throw new InvalidDataException("未知对象操作。");
        var root = new WorkspaceStore().Prepare(saved.Plan.Options);
        var task = Path.Combine(root, "tasks", saved.Plan.Id.ToString("N"));
        if (!Path.GetFullPath(saved.FilePath).Equals(Path.Combine(task, "plan.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("计划不在对应任务目录。");
        var python = MainForm.FindPython(saved.Plan.Options.Blender);
        if (!File.Exists(python)) throw new InvalidDataException("请填写有效 Blender 路径，以使用自带 Python。");
        var runId = Guid.NewGuid().ToString("N");
        var run = Path.Combine(task, "objects", runId);
        Directory.CreateDirectory(run);
        var info = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "scripts", "object_pipeline.py"),
            "--plan", saved.FilePath, "--run-id", runId, "--mode", mode }) info.ArgumentList.Add(argument);
        if (mode is "export" or "prepare")
        {
            if (inventoryPath == null || selection == null) throw new InvalidDataException("请先解析并选择对象。");
            var selectionPath = Path.Combine(run, "selection.json");
            JsonFiles.Write(selectionPath, selection);
            foreach (var argument in new[] { "--inventory", inventoryPath, "--selection", selectionPath }) info.ArgumentList.Add(argument);
        }
        token.ThrowIfCancellationRequested();
        using var log = new StreamWriter(Path.Combine(run, "objects.log"), false, Encoding.UTF8) { AutoFlush = true };
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("无法启动对象操作。");
        using var cancel = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        string? error = null;
        bool completed = false, accepted = false;
        int lastPercent = 0;
        async Task ReadErrors()
        {
            while (await process.StandardError.ReadLineAsync() is { } line) { lock (log) log.WriteLine(line); }
        }
        var stderr = ReadErrors();
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                lock (log) log.WriteLine(line);
                if (!line.StartsWith("@event ", StringComparison.Ordinal)) continue;
                using var document = JsonDocument.Parse(line[7..]);
                var row = document.RootElement;
                var message = row.GetProperty("message").GetString() ?? "";
                if (row.TryGetProperty("progress", out var percent)) lastPercent = Math.Clamp(percent.GetInt32(), 0, 100);
                progress.Report(new(message, lastPercent));
                if (row.GetProperty("type").GetString() == "error") error = message;
                if (row.GetProperty("type").GetString() == "completed") completed = true;
            }
            await process.WaitForExitAsync();
            await stderr;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !completed) throw new IOException((error ?? "对象操作未完成。") + "\n日志：" + Path.Combine(run, "objects.log"));
            var result = JsonFiles.Read<ObjectOperationResult>(Path.Combine(run, "operation.json"));
            if (result.Schema != "endfield-object-operation-v1" || result.TaskId != saved.Plan.Id
                || result.RunId != runId || result.Mode != mode || result.Status is not ("inspected" or "exported_unverified" or "partial" or "unity_prepared"))
                throw new InvalidDataException("对象结果不属于本次执行或状态不正确。");
            if (result.InventoryPath == null) throw new InvalidDataException("缺少对象列表路径。");
            _ = ReadInventory(saved, result.InventoryPath);
            foreach (var file in result.Files)
                if (!Path.GetFullPath(file.Path).StartsWith(Path.Combine(run, "exports") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(file.Path) || new FileInfo(file.Path).Length != file.Bytes)
                    throw new InvalidDataException("对象输出路径、文件或长度不符合本次结果。");
            accepted = true;
            return result;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            await stderr;
            ObjectOperationResult? result = null;
            try { if (File.Exists(Path.Combine(run, "operation.json"))) result = JsonFiles.Read<ObjectOperationResult>(Path.Combine(run, "operation.json")); }
            catch (Exception ex) { log.WriteLine(ex.Message); }
            if (!accepted && result != null && result.RunId == runId
                && result.Status is ("running" or "inspected" or "exported_unverified" or "partial" or "unity_prepared"))
            {
                result = result with { Status = token.IsCancellationRequested ? "cancelled" : "failed",
                    Message = token.IsCancellationRequested ? "对象操作已取消，保留本次记录。" : error ?? "对象操作中断或输出检查未通过。" };
                JsonFiles.Write(Path.Combine(run, "operation.json"), result);
                try
                {
                    using var taskLock = new FileStream(Path.Combine(task, "extraction.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                    taskLock.Lock(0, 1);
                    try
                    {
                        if (ReadResult(saved)?.RunId == runId) JsonFiles.Write(Path.Combine(task, "objects", "result.json"), result);
                        if (mode == "prepare" && ReadPreparation(saved)?.RunId == runId) JsonFiles.Write(Path.Combine(task, "objects", "preparation.json"), result);
                    }
                    finally { taskLock.Unlock(0, 1); }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { log.WriteLine("本次运行状态已保存；共享状态未更新：" + ex.Message); }
            }
        }
    }

    public static ObjectOperationResult? ReadResult(SavedPlan saved)
    {
        var path = Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "objects", "result.json");
        if (!File.Exists(path)) return null;
        var result = JsonFiles.Read<ObjectOperationResult>(path);
        if (result.Schema != "endfield-object-operation-v1" || result.TaskId != saved.Plan.Id) throw new InvalidDataException("对象结果归属无效。");
        return result;
    }

    public static ObjectOperationResult? ReadPreparation(SavedPlan saved)
    {
        var path = Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "objects", "preparation.json");
        if (!File.Exists(path)) return null;
        var result = JsonFiles.Read<ObjectOperationResult>(path);
        if (result.Schema != "endfield-object-operation-v1" || result.TaskId != saved.Plan.Id || result.Mode != "prepare")
            throw new InvalidDataException("适配包记录归属无效。");
        return result;
    }

    public static ObjectInventory ReadInventory(SavedPlan saved, string path)
    {
        var root = Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "objects") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("对象列表不在当前任务内。");
        var inventory = JsonFiles.Read<ObjectInventory>(path);
        if (inventory.Schema != "endfield-object-inventory-v1" || inventory.TaskId != saved.Plan.Id) throw new InvalidDataException("对象列表归属无效。");
        return inventory;
    }
}
