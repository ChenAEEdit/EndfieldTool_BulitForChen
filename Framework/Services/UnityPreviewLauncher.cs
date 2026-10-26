using System.Diagnostics;
using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

public sealed record PreviewOpenOutcome(ObjectOperationResult Result, string Message);

public sealed class UnityPreviewLauncher(bool showEditor = true)
{
    private sealed record Request(string Schema, string RequestId, string ConfigPath, string ConfigSha256, bool Play);
    private sealed record Receipt(string Schema, string RequestId, string? Scene, string? Error, string Status, bool PlayRequested);

    public Task<PreviewOpenOutcome> OpenAsync(SavedPlan saved, IProgress<ExtractionProgress> progress, CancellationToken token)
        => Task.Run(() => OpenCoreAsync(saved, progress, token), token);

    private async Task<PreviewOpenOutcome> OpenCoreAsync(SavedPlan saved, IProgress<ExtractionProgress> progress, CancellationToken token)
    {
        string taskRoot = Path.GetDirectoryName(saved.FilePath)!;
        UnityProjectFiles.NoLinks(taskRoot);
        using var taskLock = new FileStream(Path.Combine(taskRoot, "extraction.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try { taskLock.Lock(0, 1); }
        catch (IOException error) { throw new IOException("当前任务正在运行，请稍后打开预览。", error); }
        Request? sentRequest = null;
        string? sentReceiptPath = null;
        try
        {
            saved = UnityTaskTarget.Resolve(saved);
            string project = Path.GetFullPath(saved.Plan.Options.UnityProject);
            UnityAdaptationRunner.ValidateProject(project, saved.Plan.Options.UnityEditor);
            progress.Report(new("正在核对已有预览，不重新生成动画。", 10));
            var result = await UnityAdaptationResults.ImportLockedAsync(saved, token);
            if (result.UnityPreviewScene == null) throw new InvalidDataException("当前包未生成预览场景，请启用预览后建立新包。");
            string work = UnityProjectFiles.Within(project, "Library/EndfieldCharacterWorkbench");
            Directory.CreateDirectory(work);
            using var projectLock = new FileStream(Path.Combine(work, "import.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            bool open;
            try { UnityAdaptationRunner.RequireClosedProject(project); open = false; }
            catch (IOException) { open = true; }
            string id = Guid.NewGuid().ToString("N");
            string record = Path.Combine(taskRoot, "objects", result.RunId, "preview-open", id);
            Directory.CreateDirectory(record);
            foreach (var script in new[] { ("Editor", "CharacterAnimationAdapter.cs", "adapter-install.json"), ("Runtime", "CharacterPreviewControls.cs", "preview-controls-install.json") })
            {
                string relative = "Assets/EndfieldCharacterWorkbench/" + script.Item1 + "/" + script.Item2;
                string sourcePath = Path.Combine(AppContext.BaseDirectory, "templates", "workspace", script.Item2);
                var source = new ObjectOutputFile { Path = sourcePath, Bytes = new FileInfo(sourcePath).Length, Sha256 = UnityProjectFiles.Hash(sourcePath, token) };
                string destination = UnityProjectFiles.Within(project, relative);
                if (open && (!File.Exists(destination) || UnityProjectFiles.Hash(destination, token) != source.Sha256))
                    throw new InvalidDataException("此项目需要更新播放入口。请保存并关闭对应 Unity 后再点“打开并播放”；之后可直接向已打开的项目发送请求。");
                if (!open) UnityAdaptationRunner.InstallScript(source, relative, script.Item3, project, work, record, token);
            }
            string requestPath = UnityProjectFiles.Within(work, "preview-open.json");
            string receiptPath = UnityProjectFiles.Within(work, "preview-open-result.json");
            if (File.Exists(requestPath))
            {
                var previous = JsonFiles.Read<Request>(requestPath);
                var receipt = File.Exists(receiptPath) ? JsonFiles.Read<Receipt>(receiptPath) : null;
                if (receipt?.RequestId != previous.RequestId) throw new IOException("已有打开预览的请求等待 Unity 响应，请先查看 Unity 窗口。");
            }
            var request = new Request("endfield-preview-open-v1", id, result.UnityAssetRoot + "/adaptation.json", result.UnityConfigSha256!, true);
            JsonFiles.Write(Path.Combine(record, "request.json"), request);
            token.ThrowIfCancellationRequested();
            JsonFiles.Write(requestPath, request);
            sentRequest = request; sentReceiptPath = receiptPath;
            if (!open)
            {
                var start = new ProcessStartInfo(saved.Plan.Options.UnityEditor) { UseShellExecute = false, WorkingDirectory = project,
                    CreateNoWindow = !showEditor, WindowStyle = showEditor ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden };
                // This is the interactive editor the user explicitly asked to open.
                foreach (var argument in new[] { "-projectPath", project, "-executeMethod", "EndfieldCharacterWorkbench.Editor.CharacterAnimationAdapter.OpenRequestedPreview" })
                    start.ArgumentList.Add(argument);
                start.ArgumentList.Add("-logFile"); start.ArgumentList.Add(Path.Combine(record, "unity-open.log"));
                using var process = Process.Start(start) ?? throw new IOException("无法打开 Unity 编辑器。");
            }
            progress.Report(new(open ? "已向当前 Unity 项目发送打开并播放请求。" : "正在打开 Unity 项目并进入预览；请留意 Unity 窗口。", 55));
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromMinutes(3))
            {
                token.ThrowIfCancellationRequested();
                Receipt? receipt = null;
                try { if (File.Exists(receiptPath)) receipt = JsonFiles.Read<Receipt>(receiptPath); }
                catch (IOException) { }
                catch (System.Text.Json.JsonException) { }
                if (receipt?.RequestId == id)
                {
                    JsonFiles.Write(Path.Combine(record, "receipt.json"), receipt);
                    string expectedScene = result.UnityAssetRoot + "/Generated/CharacterPreview.unity";
                    if (receipt.Schema != "endfield-preview-open-result-v1" || receipt.Status != "opened"
                        || !receipt.PlayRequested || receipt.Scene != expectedScene)
                        throw new InvalidDataException(receipt.Error ?? "Unity 未确认打开本次预览。");
                    progress.Report(new("Unity 已打开正确场景并接受 Play 请求。播放姿势仍需检查。", 100));
                    return new(result, "已打开预览并请求播放。请在 Unity 的 Game（游戏）视图查看；无需再次执行生成菜单。");
                }
                await Task.Delay(500, token);
            }
            throw new TimeoutException("Unity 尚未回应打开请求，请检查许可证、编译或保存场景提示。编辑器保留；再次尝试前可在 Unity 菜单打开预览。");
        }
        catch (Exception error)
        {
            if (sentRequest != null && sentReceiptPath != null)
            {
                try
                {
                    var receipt = File.Exists(sentReceiptPath) ? JsonFiles.Read<Receipt>(sentReceiptPath) : null;
                    if (receipt?.RequestId != sentRequest.RequestId)
                        JsonFiles.Write(sentReceiptPath, new Receipt("endfield-preview-open-result-v1", sentRequest.RequestId, null,
                            error.Message, error is OperationCanceledException ? "cancelled" : "failed", false));
                }
                catch (Exception) { /* Preserve the original failure and any editor already opened. */ }
            }
            throw;
        }
        finally { taskLock.Unlock(0, 1); }
    }
}
