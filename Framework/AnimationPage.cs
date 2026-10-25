using System.Diagnostics;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

namespace EndfieldChenTool.Framework;

internal sealed partial class WorkspaceForm
{
    private readonly NumericUpDown modelImportScale = ScaleInput();
    private readonly NumericUpDown animationPositionScale = ScaleInput();
    private readonly TextBox normalizeRoot = new() { Dock = DockStyle.Fill, PlaceholderText = "可选，例如 Root；将该节点的位置减去首帧偏移" };
    private readonly TextBox excludedDescendants = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox excludedNodes = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox dropUnmatched = new() { Text = "忽略未匹配轨道并记录（默认遇到未匹配路径时停止）", AutoSize = true };
    private readonly CheckBox dropSceneTranslation = new() { Text = "移除场景对象本身的位置轨道，保留骨架位移", Checked = true, AutoSize = true };
    private readonly CheckBox forceLoop = new() { Text = "强制循环（未勾选时沿用源动画设置）", AutoSize = true };
    private readonly CheckBox createPreviewScene = new() { Text = "生成可切换、暂停和重播的独立预览场景", Checked = true, AutoSize = true };
    private readonly TextBox adaptationDetails = ReadOnlyText();
    private readonly Label adaptationHint = Note("");

    private void BuildAnimationPage()
    {
        var page = Page("动画适配");
        var layout = Grid(1, 5);
        foreach (var height in new[] { 90, 276, 100, 85 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(adaptationHint, 0, 0);
        UpdateAdaptationHint();
        var settings = Grid(2, 5);
        settings.ColumnStyles.Add(new(SizeType.Absolute, 225)); settings.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var height in new[] { 37, 37, 38, 80, 84 }) settings.RowStyles.Add(new(SizeType.Absolute, height));
        settings.Controls.Add(Note("模型导入倍率"), 0, 0); settings.Controls.Add(modelImportScale, 1, 0);
        settings.Controls.Add(Note("动画位置倍率"), 0, 1); settings.Controls.Add(animationPositionScale, 1, 1);
        settings.Controls.Add(Note("首帧位移归零的节点"), 0, 2); settings.Controls.Add(normalizeRoot, 1, 2);
        settings.Controls.Add(Note("排除后代（每行一个父路径）"), 0, 3); settings.Controls.Add(excludedDescendants, 1, 3);
        settings.Controls.Add(Note("排除节点（每行一个路径）"), 0, 4); settings.Controls.Add(excludedNodes, 1, 4);
        layout.Controls.Add(settings, 0, 1);
        var flags = Flow(); flags.Controls.AddRange([dropUnmatched, dropSceneTranslation, forceLoop, createPreviewScene]); layout.Controls.Add(flags, 0, 2);
        var actions = Flow();
        var prepare = new Button { Text = "生成 Unity 适配包", Width = 185, Height = 35 };
        prepare.Click += async (_, _) => await RunWorkflowAsync("prepare");
        var read = new Button { Text = "读取 Unity 适配结果", Width = 190, Height = 35 };
        read.Click += async (_, _) => await ReadAdaptationResultAsync();
        var generate = new Button { Text = "导入并生成适配", Width = 185, Height = 35 };
        generate.Click += async (_, _) => await ReadAdaptationResultAsync(generate: true);
        actions.Controls.Add(ActionButton("返回对象选择", () => pages.SelectedIndex = 5)); actions.Controls.Add(prepare);
        actions.Controls.Add(generate); actions.Controls.Add(read);
        actions.Controls.Add(ActionButton("打开适配包", () => RunAction(() =>
        {
            string? path = inventoryTask == null ? null : ObjectWorkflow.ReadPreparation(inventoryTask)?.UnityPackageDirectory;
            if (path == null || !Directory.Exists(path)) throw new InvalidDataException("请先生成适配包。");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        })));
        actions.Controls.Add(ActionButton("查看 Unity 运行日志", () => RunAction(() =>
        {
            var run = inventoryTask == null ? null : UnityAdaptationRunner.ReadLatest(inventoryTask);
            if (run == null || !File.Exists(run.LogPath)) throw new InvalidDataException("尚无 Unity 运行日志；运行失败的阶段信息请查看任务记录。");
            OpenLocal(run.LogPath);
        })));
        actions.Controls.Add(ActionButton("打开生成目录", () => RunAction(() =>
        {
            if (inventoryTask == null) throw new InvalidDataException("请先载入任务。");
            var result = UnityAdaptationResults.ValidatePreparation(inventoryTask);
            string path = UnityProjectFiles.Within(inventoryTask.Plan.Options.UnityProject, result.UnityAssetRoot + "/Generated");
            if (!Directory.Exists(path)) throw new InvalidDataException("尚无生成目录，请先导入并生成适配。");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        })));
        actions.Controls.Add(ActionButton("定位预览场景", () => RunAction(() =>
        {
            if (inventoryTask == null) throw new InvalidDataException("请先载入任务。");
            var result = UnityAdaptationResults.ValidatePreparation(inventoryTask);
            string scene = UnityProjectFiles.Within(inventoryTask.Plan.Options.UnityProject, result.UnityAssetRoot + "/Generated/CharacterPreview.unity");
            if (result.UnityPreviewScene == null || !result.Adapted || !File.Exists(scene))
                throw new InvalidDataException("请勾选生成预览场景，准备新包、生成并核对结果后重试。");
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add("/select," + scene); Process.Start(info);
            adaptationDetails.AppendText("\r\n预览场景：" + scene + "\r\n在当前项目的 Unity Project 窗口打开此场景，点击 Play 后在 Game 视图切换、暂停或重播。\r\n");
        })));
        actions.Controls.Add(ActionButton("查看使用说明", () => RunAction(() => OpenLocal(Path.Combine(AppContext.BaseDirectory, "docs", "动画适配说明.md")))));
        foreach (Control action in actions.Controls) objectActions.Add(action);
        layout.Controls.Add(actions, 0, 3); layout.Controls.Add(adaptationDetails, 0, 4); page.Controls.Add(layout);
        adaptationDetails.Text = "默认模型与动画倍率均为 1；只有当前已收录且版本、模型标识匹配的陈千语配置预填模型倍率 100。\r\n其他角色请明确确认单位。当前只生成 Generic 直接骨骼动画，不进行 Humanoid 重定向、IK 或游戏修正骨骼求解。\r\n自动导入使用已保存计划中的项目与编辑器路径，不创建新项目；“读取 Unity 适配结果”仍可单独使用，不启动 Unity。";
    }

    private void UpdateAdaptationDefaults()
    {
        UpdateAdaptationHint();
        var model = (modelChoice.SelectedItem as ObjectChoice)?.Row;
        modelImportScale.Value = inventoryTask?.Plan.CharacterId == "chen" && model?.Name == "chr_0005_chen_uimodel"
            && model.PathId == "8239783119078153412"
            && inventoryTask.Plan.GameAssemblySha256 == "C24495E51B406F03B03890C4788EE618AE022C991405BE5D5B8B787CB775AE89" ? 100 : 1;
    }

    private void UpdateAdaptationHint() => adaptationHint.Text = "选择模型、Avatar 和动画 → 生成适配包 → 导入并生成适配。请先保存并关闭对应 Unity 编辑器；播放效果仍需检查。\n目标项目（来自已保存计划）："
        + (inventoryTask?.Plan.Options.UnityProject ?? "请先载入任务") + "\n编辑器：" + (inventoryTask?.Plan.Options.UnityEditor ?? "请先载入任务");

    private AnimationPreparationOptions AdaptationOptions() => new()
    {
        ModelImportScale = modelImportScale.Value, PositionScale = animationPositionScale.Value,
        DropUnmatched = dropUnmatched.Checked, DropSceneTranslation = dropSceneTranslation.Checked, ForceLoop = forceLoop.Checked,
        CreatePreviewScene = createPreviewScene.Checked,
        NormalizeRoot = normalizeRoot.Text.Trim(), ExcludedDescendants = PathsFrom(excludedDescendants), ExcludedNodes = PathsFrom(excludedNodes)
    };

    private async Task ReadAdaptationResultAsync(bool generate = false)
    {
        if (scanCancellation != null || extractionCancellation != null) return;
        if (inventoryTask == null) { status.Text = "请先载入任务。"; return; }
        using var cancellation = new CancellationTokenSource(); extractionCancellation = cancellation;
        SetObjectActionsEnabled(false); executeExtraction.Enabled = resumeExtraction.Enabled = scan.Enabled = probe.Enabled = false;
        forceScan.Enabled = false;
        foreach (var action in catalogActions) action.Enabled = false;
        pages.TabPages[3].Enabled = false;
        stopScan.Enabled = true;
        scanProgress.Value = 0;
        if (generate) { scanLog.Clear(); pages.SelectedIndex = 4; }
        try
        {
            ObjectOperationResult result;
            if (generate)
            {
                var progress = new Progress<ExtractionProgress>(row =>
                {
                    if (IsDisposed || cancellation.IsCancellationRequested || !ReferenceEquals(extractionCancellation, cancellation)) return;
                    status.Text = row.Message; scanProgress.Value = Math.Clamp(row.Percent, 0, 100);
                    if (scanLog.TextLength > 150000) scanLog.Text = scanLog.Text[^75000..];
                    scanLog.AppendText(DateTime.Now.ToString("HH:mm:ss ") + row.Message + Environment.NewLine);
                });
                var outcome = await new UnityAdaptationRunner().ExecuteAsync(inventoryTask, progress, cancellation.Token);
                result = outcome.Result;
            }
            else result = await UnityAdaptationResults.ImportAsync(inventoryTask, cancellation.Token);
            scanProgress.Value = 100; pages.SelectedIndex = 6;
            adaptationDetails.Text = DescribeObjectResult(result); status.Text = result.Message;
            foreach (ListViewItem row in tasks.Items)
                if (((SavedPlan)row.Tag!).Plan.Id == inventoryTask.Plan.Id) row.SubItems[2].Text = ObjectStatus(result.Status);
            ShowTask();
        }
        catch (OperationCanceledException) { status.Text = generate ? "已取消导入／生成；本次启动的 Unity 已结束，记录和已有文件保留。" : "已取消读取适配结果。"; }
        catch (Exception error) { status.Text = "适配操作未完成：" + error.Message; adaptationDetails.Text = error.Message; }
        finally
        {
            scanLog.AppendText(status.Text + Environment.NewLine);
            extractionCancellation = null; SetObjectActionsEnabled(true);
            executeExtraction.Enabled = resumeExtraction.Enabled = scan.Enabled = probe.Enabled = forceScan.Enabled = true; stopScan.Enabled = false;
            foreach (var action in catalogActions) action.Enabled = true;
            pages.TabPages[3].Enabled = true;
            try
            {
                var message = status.Text;
                RefreshTasks();
                foreach (ListViewItem row in tasks.Items)
                    if (((SavedPlan)row.Tag!).Plan.Id == inventoryTask.Plan.Id) row.Selected = true;
                ShowTask(); status.Text = message;
                var run = UnityAdaptationRunner.ReadLatest(inventoryTask);
                if (run != null)
                {
                    adaptationDetails.AppendText($"\r\n最近 Unity 操作：{run.Status} · {run.Message}\r\n项目：{run.Project}\r\n日志：{run.LogPath}\r\n复用已有结果：{run.Reused}；已验证播放：{run.PlaybackVerified}\r\n");
                    latestIndexReport = Path.Combine(Path.GetDirectoryName(run.LogPath)!, "run.json");
                }
            }
            catch (Exception error) { scanLog.AppendText("运行记录读取失败：" + error.Message + Environment.NewLine); }
            if (closeAfterScan) BeginInvoke(new Action(Close));
        }
    }

    private static List<string> PathsFrom(TextBox box) => box.Lines.Select(line => line.Trim().Replace('\\', '/')).Where(line => line.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    private static NumericUpDown ScaleInput() => new() { Minimum = .0001m, Maximum = 10000, DecimalPlaces = 4, Value = 1, Dock = DockStyle.Fill };
}
