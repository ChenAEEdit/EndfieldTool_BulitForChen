using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

public sealed class GameResourceIndexer : IGameResourceIndexer
{
    public async Task<GameIndexResult> BuildAsync(WorkspaceOptions options, bool force,
        IProgress<IndexProgress> progress, CancellationToken cancellationToken)
    {
        var python = MainForm.FindPython(options.Blender);
        if (!File.Exists(python)) throw new InvalidDataException("找不到 Python，请在环境页面填写 Blender 路径。");
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "index_game_resources.py");
        var crypto = Path.Combine(AppContext.BaseDirectory, "runtime", "crypto", "VfsCrypto.exe");
        if (!File.Exists(script) || !File.Exists(crypto)) throw new InvalidDataException("索引组件缺失，请使用完整的角色工作台运行目录。");
        var root = new WorkspaceStore().Prepare(options);
        var logDirectory = Path.Combine(root, "index", "logs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, Guid.NewGuid().ToString("N") + ".log");
        using var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var argument in new[] { script, "--game", options.GameDirectory, "--workspace", root })
            start.ArgumentList.Add(argument);
        if (force) start.ArgumentList.Add("--force");
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("无法启动资源索引程序。");
        using var cancellation = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        string? error = null, catalogPath = null, reportPath = null;
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
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!line.StartsWith("@event ", StringComparison.Ordinal)) continue;
                using var document = JsonDocument.Parse(line[7..]);
                var row = document.RootElement;
                var message = row.GetProperty("message").GetString() ?? "";
                log.WriteLine(DateTimeOffset.Now.ToString("O") + " " + message);
                if (row.TryGetProperty("progress", out var percent)) progress.Report(new(message, Math.Clamp(percent.GetInt32(), 0, 100)));
                switch (row.GetProperty("type").GetString())
                {
                    case "error": error = message; break;
                    case "completed":
                        catalogPath = row.GetProperty("catalog").GetString();
                        reportPath = row.GetProperty("report").GetString();
                        break;
                }
            }
            await process.WaitForExitAsync();
            await errorTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || catalogPath == null || reportPath == null)
                throw new InvalidDataException((error ?? ("索引未完成。" + stderr)) + "\n扫描日志：" + logPath);
            foreach (var resultPath in new[] { catalogPath, reportPath })
                if (!Path.GetFullPath(resultPath).StartsWith(Path.Combine(root, "index") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(resultPath)) throw new InvalidDataException("索引结果不在本工作区中或尚未生成。");
            var catalog = await Task.Run(() => new JsonCatalogProvider().Load(catalogPath), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new GameIndexResult(catalog, catalogPath, reportPath, logPath);
        }
        finally
        {
            // Also stop the child if event parsing or result loading fails unexpectedly.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await errorTask;
            if (stderr.Length > 0) log.WriteLine(stderr.ToString());
            if (cancellationToken.IsCancellationRequested) log.WriteLine("扫描已取消，未将本次任务标为完成。");
        }
    }
}
