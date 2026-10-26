using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

public sealed record UnityGenerationRun
{
    public string Schema { get; init; } = "endfield-unity-generation-run-v1";
    public Guid TaskId { get; init; }
    public string SourceRunId { get; init; } = "";
    public string InvocationId { get; init; } = "";
    public string Project { get; init; } = "";
    public string Status { get; init; } = "running";
    public string Stage { get; init; } = "validate";
    public string Message { get; init; } = "正在检查适配包。";
    public string LogPath { get; init; } = "";
    public string ReceiptPath { get; init; } = "";
    public string? EditorBackup { get; init; }
    public string? RuntimeBackup { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; init; }
    public int? ExitCode { get; init; }
    public bool Reused { get; init; }
    public bool PlaybackVerified { get; init; }
}

public sealed record UnityGenerationOutcome(ObjectOperationResult Result, UnityGenerationRun Run);

public sealed class UnityAdaptationRunner
{
    private readonly TimeSpan generationTimeout;

    public UnityAdaptationRunner(TimeSpan? generationTimeout = null)
    {
        this.generationTimeout = generationTimeout ?? TimeSpan.FromMinutes(60);
        if (this.generationTimeout <= TimeSpan.Zero || this.generationTimeout > TimeSpan.FromMinutes(60))
            throw new ArgumentOutOfRangeException(nameof(generationTimeout));
    }
    private const string EditorRelative = "Assets/EndfieldCharacterWorkbench/Editor/CharacterAnimationAdapter.cs";
    private const string RuntimeRelative = "Assets/EndfieldCharacterWorkbench/Runtime/CharacterPreviewControls.cs";
    private const string PreviousEditorHash = "D7AC1920F4772F739F2E849FD9AFD858F5ECDD62A093227BAFB07C60A35320FA";
    private const string Previous08EditorHash = "B0A74860B0F80E0186EFAB5FC3D3EB34021BA10C6D4DF474B5DDE7C3E65C399B";

    public Task<UnityGenerationOutcome> ExecuteAsync(SavedPlan saved, IProgress<ExtractionProgress> progress,
        CancellationToken token) => Task.Run(() => ExecuteCoreAsync(saved, progress, token), token);

    private async Task<UnityGenerationOutcome> ExecuteCoreAsync(SavedPlan saved,
        IProgress<ExtractionProgress> progress, CancellationToken token)
    {
        var preparation = UnityAdaptationResults.ValidatePreparation(saved);
        string taskRoot = Path.GetDirectoryName(saved.FilePath)!;
        UnityProjectFiles.NoLinks(taskRoot);
        using var taskLock = new FileStream(Path.Combine(taskRoot, "extraction.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        try { taskLock.Lock(0, 1); }
        catch (IOException error) { throw new IOException("当前任务正在提取、解析或导入，请稍后再试。", error); }
        try
        {
            saved = UnityTaskTarget.Resolve(saved);
            preparation = UnityAdaptationResults.ValidatePreparation(saved);
            string invocation = Guid.NewGuid().ToString("N");
            string runRoot = Path.Combine(taskRoot, "objects", preparation.RunId, "unity-runs", invocation);
            UnityProjectFiles.NoLinks(runRoot);
            Directory.CreateDirectory(runRoot);
            string recordPath = Path.Combine(runRoot, "run.json");
            var run = new UnityGenerationRun { TaskId = saved.Plan.Id, SourceRunId = preparation.RunId,
                InvocationId = invocation, Project = Path.GetFullPath(saved.Plan.Options.UnityProject),
                LogPath = Path.Combine(runRoot, "unity.log"), ReceiptPath = Path.Combine(runRoot, "native-completion.json") };
            void Publish(string stage, string message, int percent)
            {
                run = run with { Stage = stage, Message = message };
                JsonFiles.Write(recordPath, run);
                JsonFiles.Write(Path.Combine(taskRoot, "objects", "unity-last-run.json"), run);
                progress.Report(new(message, percent));
            }
            Publish("validate", "正在核对适配包、项目和编辑器。", 2);
            try
            {
                token.ThrowIfCancellationRequested();
                ValidateProject(run.Project, saved.Plan.Options.UnityEditor);
                var packageFiles = ValidatePackage(saved, preparation, token);
                string assetRoot = UnityProjectFiles.Within(run.Project, preparation.UnityAssetRoot!);
                string configPath = preparation.UnityAssetRoot + "/adaptation.json";
                string work = UnityProjectFiles.Within(run.Project, "Library/EndfieldCharacterWorkbench");
                Directory.CreateDirectory(work);
                using var projectLock = new FileStream(Path.Combine(work, "import.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                RequireClosedProject(run.Project);
                Publish("copy", "正在复制经摘要核对的独立适配包。", 12);
                InstallAssets(preparation, packageFiles, assetRoot, work, invocation, token);
                Publish("editor", "正在准备 Unity 编辑器入口。", 30);
                if (packageFiles.TryGetValue(RuntimeRelative, out var runtimeScript))
                    run = run with { RuntimeBackup = InstallScript(runtimeScript, RuntimeRelative, "preview-controls-install.json", run.Project, work, runRoot, token) };
                run = run with { EditorBackup = InstallScript(packageFiles[EditorRelative], EditorRelative, "adapter-install.json", run.Project, work, runRoot, token) };
                RequireSharedScripts(packageFiles, run.Project, token);
                Publish("editor", "适配包已导入现有项目。", 35);
                string generated = Path.Combine(assetRoot, "Generated");
                if (Directory.Exists(generated) && File.Exists(Path.Combine(generated, "adaptation-result.json")))
                {
                    // Reuse is explicit and still checks every native output. Never overwrite edited output.
                    try
                    {
                        RequirePackageInstalled(preparation, packageFiles, assetRoot, token);
                        var prior = await UnityAdaptationResults.ImportLockedAsync(saved, token);
                        run = run with { Status = "adapted_unverified", Reused = true, FinishedAt = DateTimeOffset.Now };
                        Publish("complete", "已核对并复用本包已有的完整 Unity 结果；未重新生成，尚未验证播放。", 100);
                        return new(prior, run);
                    }
                    catch (InvalidDataException)
                    {
                        if (HasGeneratedAssets(generated))
                            throw new InvalidDataException("本包已有生成内容，但结果不完整或已修改。请重新生成独立适配包；已有内容已保留。");
                    }
                }
                if (HasGeneratedAssets(generated))
                    throw new InvalidDataException("本包已有生成内容。请重新生成适配包，保留已有修改。");
                RequireClosedProject(run.Project);
                var info = new ProcessStartInfo(Path.GetFullPath(saved.Plan.Options.UnityEditor))
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = run.Project };
                foreach (string argument in new[] { "-batchmode", "-quit", "-projectPath", run.Project,
                    "-executeMethod", "EndfieldCharacterWorkbench.Editor.CharacterAnimationAdapter.BuildBatch", "-logFile", run.LogPath })
                    info.ArgumentList.Add(argument);
                info.Environment["ENDFIELD_ADAPT_CONFIG"] = configPath;
                info.Environment["ENDFIELD_ADAPT_INVOCATION"] = invocation;
                info.Environment["ENDFIELD_ADAPT_RECEIPT"] = run.ReceiptPath;
                info.Environment["ENDFIELD_ADAPT_CONFIG_SHA256"] = preparation.UnityConfigSha256;
                using var process = new Process { StartInfo = info };
                Publish("unity", "正在启动 Unity 原生生成；首次导入和编译可能需要数分钟。", 40);
                token.ThrowIfCancellationRequested();
                if (!process.Start()) throw new IOException("无法启动 Unity。");
                using var timeout = new CancellationTokenSource(generationTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
                using var cancel = linked.Token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                });
                var watch = Stopwatch.StartNew();
                try
                {
                    var exit = process.WaitForExitAsync();
                    while (!exit.IsCompleted)
                    {
                        await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(5), linked.Token));
                        linked.Token.ThrowIfCancellationRequested();
                        progress.Report(new($"Unity 正在导入／生成，已等待 {watch.Elapsed:mm\\:ss}；日志：{run.LogPath}", 55));
                    }
                    await exit;
                    linked.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
                {
                    string duration = generationTimeout.TotalMinutes >= 1 ? $"{generationTimeout.TotalMinutes:g} 分钟" : $"{generationTimeout.TotalSeconds:g} 秒";
                    throw new TimeoutException($"Unity 运行超过 {duration}，已结束本次启动的进程；请查看日志。");
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    }
                    if (process.HasExited) run = run with { ExitCode = process.ExitCode };
                }
                Publish("verify", "正在检查本次 Unity 回执、生成文件和摘要。", 85);
                if (run.ExitCode != 0)
                {
                    UnityFailureSummary? summary = null;
                    try { summary = UnityFailureSummary.Read(saved); }
                    catch (Exception) { /* Diagnostic parsing must not hide the original Unity failure. */ }
                    throw new InvalidDataException(summary == null
                        ? $"Unity 未正常完成（退出码 {run.ExitCode}）。请在帮助与日志中查看本次日志。"
                        : $"Unity 部分或全部动画未完成：{summary.ConvertedKeys.Count} 条已转换，{summary.Problems.Count} 条未完成。请查看页面中的失败项。");
                }
                if (!File.Exists(run.ReceiptPath))
                    throw new InvalidDataException("Unity 没有生成本次调用回执；可能是项目脚本编译、许可证或入口执行失败。退出码不足以确认成功。");
                var receipt = JsonFiles.Read<NativeReceipt>(run.ReceiptPath);
                if (receipt.Schema != "endfield-unity-invocation-v1" || receipt.InvocationId != invocation
                    || receipt.ConfigPath != configPath || receipt.ConfigSha256 != preparation.UnityConfigSha256 || !receipt.Succeeded)
                    throw new InvalidDataException("本次 Unity 回执无效或生成失败：" + receipt.Error);
                RequireSharedScripts(packageFiles, run.Project, token);
                RequirePackageInstalled(preparation, packageFiles, assetRoot, token);
                var result = await UnityAdaptationResults.ImportLockedAsync(saved, token, invocation);
                run = run with { Status = "adapted_unverified", FinishedAt = DateTimeOffset.Now };
                Publish("complete", "Unity 原生动画、控制器和 Prefab" + (result.UnityPreviewScene == null ? "" : "、独立预览场景") + "已生成并核对；尚未验证播放。", 100);
                return new(result, run);
            }
            catch (Exception error)
            {
                run = run with { Status = token.IsCancellationRequested ? "cancelled" : "failed", FinishedAt = DateTimeOffset.Now };
                Publish(run.Stage, error.Message + " 本次记录：" + recordPath, 0);
                throw;
            }
        }
        finally { taskLock.Unlock(0, 1); }
    }

    public static UnityGenerationRun? ReadLatest(SavedPlan saved)
    {
        string path = Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "objects", "unity-last-run.json");
        if (!File.Exists(path)) return null;
        var run = JsonFiles.Read<UnityGenerationRun>(path);
        if (run.Schema != "endfield-unity-generation-run-v1" || run.TaskId != saved.Plan.Id
            || !Guid.TryParseExact(run.SourceRunId, "N", out _) || !Guid.TryParseExact(run.InvocationId, "N", out _))
            throw new InvalidDataException("Unity 运行记录无效。");
        string root = Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "objects", run.SourceRunId, "unity-runs", run.InvocationId);
        if (!string.Equals(run.LogPath, Path.Combine(root, "unity.log"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(run.ReceiptPath, Path.Combine(root, "native-completion.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unity 日志路径不属于本次运行。");
        return run;
    }

    internal static void ValidateProject(string project, string editor)
    {
        UnityProjectFiles.NoLinks(project);
        string versionPath = UnityProjectFiles.Within(project, "ProjectSettings/ProjectVersion.txt");
        if (!Directory.Exists(Path.Combine(project, "Assets")) || !Directory.Exists(Path.Combine(project, "Packages")) || !File.Exists(versionPath))
            throw new InvalidDataException("计划中的 Unity 项目无效；请使用已存在的项目，不会自动创建新项目。");
        if (!File.Exists(editor) || !Path.GetFileName(editor).Equals("Unity.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("计划中的 Unity 编辑器路径无效。");
        var version = Regex.Match(File.ReadAllText(versionPath), @"(?m)^m_EditorVersion:\s*(\S+)\s*$");
        string actual = FileVersionInfo.GetVersionInfo(editor).ProductVersion?.Split('_')[0] ?? "";
        if (!version.Success || version.Groups[1].Value != actual)
            throw new InvalidDataException($"Unity 编辑器版本与项目不一致（项目 {version.Groups[1].Value}，编辑器 {actual}），请核对计划设置。");
    }

    internal static void RequireClosedProject(string project)
    {
        string path = UnityProjectFiles.Within(project, "Temp/UnityLockfile");
        if (!File.Exists(path)) return;
        try { using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("这个项目正在 Unity 中打开。请保存并关闭对应编辑器后重试；工具不会结束你已打开的 Unity。", error); }
    }

    private static Dictionary<string, ObjectOutputFile> ValidatePackage(SavedPlan saved, ObjectOperationResult result, CancellationToken token)
    {
        string run = Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "objects", result.RunId);
        string package = UnityProjectFiles.Within(run, "exports/unity-package");
        if (result.UnityPackageDirectory == null || !Path.GetFullPath(result.UnityPackageDirectory).Equals(package, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("适配包不属于本次任务。");
        var files = new Dictionary<string, ObjectOutputFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in result.Files.Where(row => row.Kind == "UnityPackage"))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(package, file.Path).Replace('\\', '/');
            string path = UnityProjectFiles.Within(package, relative);
            if (!Path.GetFullPath(file.Path).Equals(path, StringComparison.OrdinalIgnoreCase) || !files.TryAdd(relative, file))
                throw new InvalidDataException("适配包文件重复或路径无效。");
            UnityProjectFiles.Verify(path, file, token);
        }
        string assetRoot = result.UnityAssetRoot!;
        string configRelative = assetRoot + "/adaptation.json";
        if (!files.ContainsKey(EditorRelative) || !files.TryGetValue(configRelative, out var configFile)
            || configFile.Sha256 != result.UnityConfigSha256)
            throw new InvalidDataException("适配包缺少编辑器入口或配置摘要不符。");
        var config = JsonFiles.Read<PackageConfig>(configFile.Path);
        if (config.Schema != "endfield-unity-adaptation-config-v1" || config.TaskId != saved.Plan.Id.ToString()
            || config.RunId != result.RunId || config.Clips.Count != result.ExpectedAdaptedClips)
            throw new InvalidDataException("适配配置与当前任务不符。");
        var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in config.Inputs)
        {
            string key = assetRoot + "/" + input.Path;
            if (!(input.Path.StartsWith("Model/", StringComparison.Ordinal) || input.Path.StartsWith("Sources/", StringComparison.Ordinal))
                || !inputs.Add(key) || !files.TryGetValue(key, out var file) || file.Sha256 != input.Sha256)
                throw new InvalidDataException("适配配置的输入清单与包文件不一致。");
        }
        if (!inputs.Contains(assetRoot + "/" + config.Model)
            || config.Clips.Any(clip => !inputs.Contains(assetRoot + "/" + clip.Source))
            || files.Keys.Any(key => key.StartsWith(assetRoot + "/", StringComparison.Ordinal) && key != configRelative && !inputs.Contains(key))
            || files.Keys.Any(key => key != EditorRelative && key != RuntimeRelative && key != "使用说明.md" && !key.StartsWith(assetRoot + "/", StringComparison.Ordinal)))
            throw new InvalidDataException("适配包包含未声明的内容或缺少模型／动画。");
        // Older packages lack the batch entry. Ask for a fresh package rather than altering its manifest.
        if (config.AdapterVersion is not ("0.8.0" or "0.9.0" or "0.10.0" or "0.11.1")) throw new InvalidDataException("适配包版本不受支持，请重新生成。");
        if (config.AdapterVersion is ("0.9.0" or "0.10.0" or "0.11.1") && !files.ContainsKey(RuntimeRelative))
            throw new InvalidDataException("适配包缺少预览播放脚本，请重新生成完整包。");
        return files;
    }

    private static void InstallAssets(ObjectOperationResult result, Dictionary<string, ObjectOutputFile> files,
        string destination, string work, string invocation, CancellationToken token)
    {
        if (Directory.Exists(destination)) { RequirePackageInstalled(result, files, destination, token); return; }
        string stage = UnityProjectFiles.Within(work, "staging/" + invocation);
        Directory.CreateDirectory(stage);
        foreach (var entry in files.Where(pair => pair.Key.StartsWith(result.UnityAssetRoot + "/", StringComparison.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            string relative = entry.Key[(result.UnityAssetRoot!.Length + 1)..];
            UnityProjectFiles.Copy(entry.Value.Path, UnityProjectFiles.Within(stage, relative), entry.Value, token);
        }
        token.ThrowIfCancellationRequested();
        UnityProjectFiles.NoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(stage, destination);
        RequirePackageInstalled(result, files, destination, token);
    }

    private static void RequirePackageInstalled(ObjectOperationResult result, Dictionary<string, ObjectOutputFile> files,
        string destination, CancellationToken token)
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in files.Where(pair => pair.Key.StartsWith(result.UnityAssetRoot + "/", StringComparison.Ordinal)))
        {
            string relative = entry.Key[(result.UnityAssetRoot!.Length + 1)..];
            string path = UnityProjectFiles.Within(destination, relative);
            UnityProjectFiles.Verify(path, entry.Value, token); expected.Add(path);
        }
        var pending = new Stack<string>(); pending.Push(destination);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested(); UnityProjectFiles.NoLinks(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                UnityProjectFiles.NoLinks(path);
                if (Directory.Exists(path))
                {
                    if (path != Path.Combine(destination, "Generated")) pending.Push(path);
                }
                else if (!expected.Contains(path) && !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("项目中的适配包有新增或修改的内容，请重新生成独立包：" + path);
            }
        }
    }

    internal static string? InstallScript(ObjectOutputFile source, string relative, string markerName,
        string project, string work, string runRoot, CancellationToken token)
    {
        string destination = UnityProjectFiles.Within(project, relative);
        string marker = UnityProjectFiles.Within(work, markerName);
        string? backup = null;
        string? priorHash = File.Exists(destination) ? UnityProjectFiles.Hash(destination, token) : null;
        if (priorHash != null && priorHash != source.Sha256)
        {
            var installed = File.Exists(marker) ? JsonFiles.Read<EditorInstall>(marker) : null;
            bool knownEditor = relative == EditorRelative && (priorHash == PreviousEditorHash || priorHash == Previous08EditorHash);
            if (!knownEditor && (installed?.Schema != "endfield-unity-editor-install-v1" || installed.Sha256 != priorHash))
                throw new InvalidDataException("项目中的适配脚本不是已记录版本或有手工修改，已保留。请先合并脚本，或另存该脚本后重新操作。");
            backup = Path.Combine(runRoot, "project-backup", Path.GetFileName(relative));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(destination, backup, overwrite: false);
            if (UnityProjectFiles.Hash(backup, token) != priorHash) throw new IOException("适配脚本备份摘要不符。");
        }
        if (priorHash != source.Sha256)
        {
            string temporary = UnityProjectFiles.Within(work, "adapter-" + Guid.NewGuid().ToString("N") + ".tmp");
            UnityProjectFiles.Copy(source.Path, temporary, source, token);
            token.ThrowIfCancellationRequested();
            if (priorHash != null && UnityProjectFiles.Hash(destination, token) != priorHash)
                throw new IOException("复制期间项目适配脚本被修改，已停止。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(temporary, destination, overwrite: priorHash != null);
        }
        UnityProjectFiles.Verify(destination, source, token);
        JsonFiles.Write(marker, new EditorInstall { Sha256 = source.Sha256 });
        return backup;
    }

    private static bool HasGeneratedAssets(string path) => Directory.Exists(path)
        && (Directory.EnumerateDirectories(path).Any() || Directory.EnumerateFiles(path).Any(file =>
            Path.GetFileName(file) != "adaptation-result.json" && !file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)));

    private static void RequireSharedScripts(Dictionary<string, ObjectOutputFile> files, string project, CancellationToken token)
    {
        foreach (string relative in new[] { EditorRelative, RuntimeRelative })
            if (files.TryGetValue(relative, out var file)) UnityProjectFiles.Verify(UnityProjectFiles.Within(project, relative), file, token);
    }

    private sealed record EditorInstall
    {
        public string Schema { get; init; } = "endfield-unity-editor-install-v1";
        public string Sha256 { get; init; } = "";
    }
    private sealed record NativeReceipt
    {
        public string Schema { get; init; } = "";
        public string InvocationId { get; init; } = "";
        public string ConfigPath { get; init; } = "";
        public string ConfigSha256 { get; init; } = "";
        public string Error { get; init; } = "";
        public bool Succeeded { get; init; }
    }
    private sealed record PackageConfig
    {
        public string Schema { get; init; } = "";
        public string TaskId { get; init; } = "";
        public string RunId { get; init; } = "";
        public string AdapterVersion { get; init; } = "";
        public string Model { get; init; } = "";
        public List<PackageInput> Inputs { get; init; } = [];
        public List<PackageClip> Clips { get; init; } = [];
    }
    private sealed record PackageInput(string Path, string Sha256);
    private sealed record PackageClip(string Source);
}

internal static class UnityProjectFiles
{
    internal static string Within(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || !path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件路径超出指定目录。");
        NoLinks(path); return path;
    }

    internal static void NoLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("自动导入不通过符号链接或目录联接：" + current);
            current = Path.GetDirectoryName(current);
        }
    }

    internal static string Hash(string path, CancellationToken token)
    {
        NoLinks(path); token.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024]; int count;
        while ((count = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void Verify(string path, ObjectOutputFile file, CancellationToken token)
    {
        NoLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes || Hash(path, token) != file.Sha256)
            throw new InvalidDataException("文件缺失、长度或摘要不符：" + path);
    }

    internal static void Copy(string source, string destination, ObjectOutputFile file, CancellationToken token)
    {
        Verify(source, file, token); NoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var input = File.OpenRead(source))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[1024 * 1024]; int count;
            while ((count = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
            output.Flush(flushToDisk: true);
        }
        Verify(destination, file, token);
    }
}
