using System.Diagnostics;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

namespace EndfieldChenTool.Framework;

internal sealed partial class WorkspaceForm
{
    private readonly NumericUpDown modelImportScale = ScaleInput();
    private readonly NumericUpDown animationPositionScale = ScaleInput();
    private readonly ComboBox sourceRigChoice = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 900 };
    private readonly TextBox normalizeRoot = new() { Dock = DockStyle.Fill, PlaceholderText = "可选，例如 Root；将该节点的位置减去首帧偏移" };
    private readonly TextBox excludedDescendants = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox excludedNodes = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox dropUnmatched = new() { Text = "忽略未匹配轨道并记录（默认遇到未匹配路径时停止）", AutoSize = true };
    private readonly CheckBox dropSceneTranslation = new() { Text = "移除场景对象本身的位置轨道，保留骨架位移", Checked = true, AutoSize = true };
    private readonly CheckBox forceLoop = new() { Text = "强制循环（未勾选时沿用源动画设置）", AutoSize = true };
    private readonly CheckBox createPreviewScene = new() { Text = "生成可切换、暂停和重播的独立预览场景", Checked = true, AutoSize = true };
    private readonly TextBox adaptationDetails = ReadOnlyText();
    private readonly TrackAuditPanel trackAudit = new();
    private readonly Label adaptationHint = Note("");

    private readonly Button retryConverted = new() { Text = "仅用已转换项重新生成", Dock = DockStyle.Fill, Height = 36, Enabled = false };
    private readonly Button previewAction = new() { Text = "生成 Unity 预览", Dock = DockStyle.Fill, Height = 43 };

    private void BuildAnimationPage()
    {
        var layout = Grid(1, 6);
        layout.RowStyles.Add(new(SizeType.Absolute, 69));
        layout.RowStyles.Add(new(SizeType.Absolute, 48));
        layout.RowStyles.Add(new(SizeType.Absolute, 84));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 40));
        layout.Controls.Add(adaptationHint, 0, 0);
        previewAction.BackColor = Accent; previewAction.ForeColor = Color.White; previewAction.FlatStyle = FlatStyle.Flat;
        previewAction.Click += async (_, _) => await RunWorkflowAsync("preview");
        objectActions.Add(previewAction); layout.Controls.Add(previewAction, 0, 1);
        var resultActions = Flow();
        var open = new Button { Text = "打开并播放预览", Width = 185, Height = 35 };
        open.Click += async (_, _) => await ReadAdaptationResultAsync(openPreview: true);
        resultActions.Controls.Add(open);
        resultActions.Controls.Add(ActionButton("定位预览场景", () => RunAction(LocatePreview)));
        resultActions.Controls.Add(ActionButton("查看本次日志", () => RunAction(() =>
        {
            var run = inventoryTask == null ? null : UnityAdaptationRunner.ReadLatest(inventoryTask);
            if (run == null) throw new InvalidDataException("尚无 Unity 运行记录。");
            ShowInlineLog(run.LogPath);
        })));
        foreach (Control action in resultActions.Controls) objectActions.Add(action);
        layout.Controls.Add(resultActions, 0, 2);
        var advanced = Grid(1, 3);
        advanced.RowStyles.Add(new(SizeType.Absolute, 278));
        advanced.RowStyles.Add(new(SizeType.Absolute, 100));
        advanced.RowStyles.Add(new(SizeType.Percent, 100));
        var settings = Grid(2, 6);
        settings.ColumnStyles.Add(new(SizeType.Absolute, 110)); settings.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var height in new[] { 34, 34, 38, 67, 67, 38 }) settings.RowStyles.Add(new(SizeType.Absolute, height));
        settings.Controls.Add(Note("模型倍率"), 0, 0); settings.Controls.Add(modelImportScale, 1, 0);
        settings.Controls.Add(Note("位置倍率"), 0, 1); settings.Controls.Add(animationPositionScale, 1, 1);
        settings.Controls.Add(Note("位移归零节点"), 0, 2); settings.Controls.Add(normalizeRoot, 1, 2);
        settings.Controls.Add(Note("排除后代路径"), 0, 3); settings.Controls.Add(excludedDescendants, 1, 3);
        settings.Controls.Add(Note("排除节点路径"), 0, 4); settings.Controls.Add(excludedNodes, 1, 4);
        settings.Controls.Add(Note("源骨架候选"), 0, 5); settings.Controls.Add(sourceRigChoice, 1, 5);
        advanced.Controls.Add(settings, 0, 0);
        var flags = Flow(); flags.Controls.AddRange([dropUnmatched, dropSceneTranslation, forceLoop, createPreviewScene]);
        advanced.Controls.Add(flags, 0, 1);
        var actions = Flow();
        var prepare = new Button { Text = "仅生成适配包", Width = 145, Height = 35 };
        prepare.Click += async (_, _) => await RunWorkflowAsync("prepare");
        var generate = new Button { Text = "导入已有适配包", Width = 145, Height = 35 };
        generate.Click += async (_, _) => await ReadAdaptationResultAsync(generate: true);
        var read = new Button { Text = "核对已有结果", Width = 145, Height = 35 };
        read.Click += async (_, _) => await ReadAdaptationResultAsync();
        actions.Controls.AddRange([prepare, generate, read]);
        actions.Controls.Add(ActionButton("打开适配包目录", () => RunAction(() =>
        {
            string? path = inventoryTask == null ? null : ObjectWorkflow.ReadPreparation(inventoryTask)?.UnityPackageDirectory;
            if (path == null || !Directory.Exists(path)) throw new InvalidDataException("请先生成适配包。");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        })));
        actions.Controls.Add(ActionButton("查看诊断报告", () => RunAction(() =>
        {
            if (inventoryTask == null) throw new InvalidDataException("请先载入任务。");
            adaptationDetails.Text = File.ReadAllText(UnityDiagnosticReport.Export(inventoryTask));
        })));
        foreach (Control action in actions.Controls) objectActions.Add(action);
        advanced.Controls.Add(actions, 0, 2);
        var advancedPreview = Collapsible("高级：单位、骨骼规则与单步执行", advanced, 470, limitHeight: false);
        layout.Controls.Add(advancedPreview, 0, 3);
        var resultTabs = new TabControl { Dock = DockStyle.Fill };
        var resultPage = new TabPage("结果"); resultPage.Controls.Add(adaptationDetails);
        var auditPage = new TabPage("轨道明细"); auditPage.Controls.Add(trackAudit);
        resultTabs.TabPages.AddRange([resultPage, auditPage]); layout.Controls.Add(resultTabs, 0, 4);
        retryConverted.Click += async (_, _) => await RetryConvertedAsync();
        objectActions.Add(retryConverted); layout.Controls.Add(retryConverted, 0, 5);
        previewPane.Controls.Add(ScrollableWorkflow(layout, advancedPreview));
        UpdateAdaptationHint();
        adaptationDetails.Text = "选择左侧模型、骨架和动作，点击“生成 Unity 预览”。\r\n工具自动准备适配包、导入 Unity 并核对结果。\r\n\r\n名称相关不等于兼容；遇到失败会在此列出原因。生成完成后仍需检查实际播放。";
    }

    private void LocatePreview()
    {
        if (inventoryTask == null) throw new InvalidDataException("请先载入任务。");
        var saved = UnityTaskTarget.Resolve(inventoryTask);
        var result = UnityAdaptationResults.ValidatePreparation(saved);
        string scene = UnityProjectFiles.Within(saved.Plan.Options.UnityProject, result.UnityAssetRoot + "/Generated/CharacterPreview.unity");
        if (!result.Adapted || !File.Exists(scene)) throw new InvalidDataException("当前目标项目尚无完整预览，请先生成并核对结果。");
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add("/select," + scene); Process.Start(info);
        adaptationDetails.AppendText("\r\n在 Unity 的 Project 窗口打开该场景，点击 Play：\r\n" + scene + "\r\n");
    }

    private static string PreviewResultText(SavedPlan original, ObjectOperationResult result)
    {
        var saved = UnityTaskTarget.Resolve(original);
        string audit = "";
        try
        {
            var report = UnityDiagnosticReport.ReadVerifiedReport(saved);
            var tracks = report.GetProperty("clips").EnumerateArray().SelectMany(c => c.TryGetProperty("tracks", out var t) ? t.EnumerateArray().ToArray() : []).ToArray();
            if (tracks.Length > 0)
            {
                int omitted = tracks.Count(t => t.GetProperty("decision").GetString() != "retain");
                audit = $"轨道：输出 {tracks.Length - omitted}／源 {tracks.Length}；未输出 {omitted}。" + (omitted > 0 ? "请查看“轨道明细”，本次不是完整保留。" : "") + "\r\n";
            }
        }
        catch (Exception error) { audit = "轨道明细暂不可读：" + error.Message + "\r\n"; }
        return $"生成完成 · {result.ExpectedAdaptedClips} 条动画\r\n目标项目：{saved.Plan.Options.UnityProject}\r\n"
            + audit + "生成文件已核对；实际播放尚未验证。\r\n\r\n预览场景：\r\n" + (result.UnityPreviewScene ?? "本次未启用预览场景")
            + "\r\n\r\n" + string.Join("\r\n", result.Warnings);
    }

    private void ShowPreviewFailure(SavedPlan saved, Exception error)
    {
        trackAudit.LoadAudit(saved);
        retryConverted.Enabled = false;
        adaptationDetails.Text = "未完成：" + error.Message;
        try
        {
            var summary = UnityFailureSummary.Read(saved);
            if (summary == null) return;
            adaptationDetails.AppendText("\r\n\r\n" + summary.Describe());
            retryConverted.Enabled = summary.ConvertedKeys.Count > 0 && summary.Problems.Count > 0;
        }
        catch (Exception reportError) { adaptationDetails.AppendText("\r\n详细报告暂不可读：" + reportError.Message); }
    }

    private async Task RetryConvertedAsync()
    {
        try
        {
            if (inventoryTask == null || objectInventory == null) throw new InvalidDataException("请先载入动作。");
            var summary = UnityFailureSummary.Read(inventoryTask) ?? throw new InvalidDataException("尚无当前包的生成报告。");
            if (summary.ConvertedKeys.Count == 0 || summary.Problems.Count == 0) throw new InvalidDataException("当前没有需要单独重试的已转换项。");
            var selection = JsonFiles.Read<ObjectExportSelection>(Path.Combine(Path.GetDirectoryName(inventoryTask.FilePath)!, "objects", summary.RunId, "selection.json"));
            if (selection.Adaptation == null || summary.ConvertedKeys.Any(key => !selection.ClipKeys.Contains(key)
                || objectInventory.Objects.All(row => row.Key != key || row.Kind != "AnimationClip")))
                throw new InvalidDataException("报告中的动画与本次选择或对象列表不符。");
            int model = Enumerable.Range(1, modelChoice.Items.Count - 1).FirstOrDefault(i => (modelChoice.Items[i] as ObjectChoice)?.Row?.Key == selection.ModelKey);
            int avatar = Enumerable.Range(1, avatarChoice.Items.Count - 1).FirstOrDefault(i => (avatarChoice.Items[i] as ObjectChoice)?.Row?.Key == selection.AvatarKey);
            if (model == 0 || avatar == 0) throw new InvalidDataException("原包模型或骨架不在当前对象列表中。");
            modelChoice.SelectedIndex = model; avatarChoice.SelectedIndex = avatar;
            var options = selection.Adaptation;
            sourceRigChoice.SelectedIndex = 0;
            if (options.SourceRigKey.Length > 0)
            {
                int source = Enumerable.Range(1, sourceRigChoice.Items.Count - 1).FirstOrDefault(i => (sourceRigChoice.Items[i] as ObjectChoice)?.Row?.Key == options.SourceRigKey);
                if (source == 0) throw new InvalidDataException("原动作骨架不在当前快照中，不能更改原规则重试。");
                sourceRigChoice.SelectedIndex = source;
            }
            modelImportScale.Value = options.ModelImportScale; animationPositionScale.Value = options.PositionScale;
            normalizeRoot.Text = options.NormalizeRoot; excludedDescendants.Lines = options.ExcludedDescendants.ToArray(); excludedNodes.Lines = options.ExcludedNodes.ToArray();
            dropUnmatched.Checked = options.DropUnmatched; dropSceneTranslation.Checked = options.DropSceneTranslation;
            forceLoop.Checked = options.ForceLoop; createPreviewScene.Checked = true;
            checkedClips.Clear(); checkedClips.UnionWith(summary.ConvertedKeys); PopulateObjects();
            await RunWorkflowAsync("preview");
        }
        catch (Exception error) { status.Text = "无法重试：" + error.Message; adaptationDetails.Text = error.Message; }
    }

    private void UpdateAdaptationDefaults()
    {
        UpdateAdaptationHint();
        var model = (modelChoice.SelectedItem as ObjectChoice)?.Row;
        modelImportScale.Value = inventoryTask?.Plan.CharacterId == "chen" && model?.Name == "chr_0005_chen_uimodel"
            && model.PathId == "8239783119078153412"
            && inventoryTask.Plan.GameAssemblySha256 == "C24495E51B406F03B03890C4788EE618AE022C991405BE5D5B8B787CB775AE89" ? 100 : 1;
    }

    private void UpdateAdaptationHint()
    {
        adaptationHint.Text = "生成前请保存并关闭顶部所示项目的 Unity 编辑器。\n自动准备、导入并核对结果；单位与骨骼规则可在高级设置中调整。";
        UpdateContext();
    }

    private AnimationPreparationOptions AdaptationOptions() => new()
    {
        SourceRigKey = (sourceRigChoice.SelectedItem as ObjectChoice)?.Row?.Key ?? "",
        ModelImportScale = modelImportScale.Value, PositionScale = animationPositionScale.Value,
        DropUnmatched = dropUnmatched.Checked, DropSceneTranslation = dropSceneTranslation.Checked, ForceLoop = forceLoop.Checked,
        CreatePreviewScene = createPreviewScene.Checked,
        NormalizeRoot = normalizeRoot.Text.Trim(), ExcludedDescendants = PathsFrom(excludedDescendants), ExcludedNodes = PathsFrom(excludedNodes)
    };

    private async Task ReadAdaptationResultAsync(bool generate = false, bool openPreview = false)
    {
        if (scanCancellation != null || extractionCancellation != null) return;
        if (inventoryTask == null) { status.Text = "请先载入任务。"; return; }
        using var cancellation = new CancellationTokenSource(); extractionCancellation = cancellation;
        runningTask = inventoryTask;
        SetObjectActionsEnabled(false); executeExtraction.Enabled = resumeExtraction.Enabled = scan.Enabled = probe.Enabled = false;
        forceScan.Enabled = false;
        foreach (var action in catalogActions) action.Enabled = false;
        pages.TabPages[3].Enabled = false;
        stopScan.Enabled = true;
        scanProgress.Value = 0;
        if (generate) scanLog.Clear();
        try
        {
            ObjectOperationResult result;
            string? openMessage = null;
            if (openPreview)
            {
                var progress = new Progress<ExtractionProgress>(row =>
                {
                    if (IsDisposed || cancellation.IsCancellationRequested) return;
                    status.Text = row.Message; scanProgress.Value = Math.Clamp(row.Percent, 0, 100);
                });
                var outcome = await new UnityPreviewLauncher().OpenAsync(inventoryTask, progress, cancellation.Token);
                result = outcome.Result; openMessage = outcome.Message;
            }
            else if (generate)
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
            scanProgress.Value = 100;
            adaptationDetails.Text = PreviewResultText(inventoryTask, result) + (openMessage == null ? "" : "\r\n\r\n" + openMessage);
            trackAudit.LoadAudit(inventoryTask);
            status.Text = openMessage ?? result.Message;
            foreach (ListViewItem row in tasks.Items)
                if (((SavedPlan)row.Tag!).Plan.Id == inventoryTask.Plan.Id) row.SubItems[2].Text = ObjectStatus(result.Status);
            ShowTask();
        }
        catch (OperationCanceledException) { status.Text = openPreview ? "已停止等待打开预览。交互式 Unity 编辑器保留，请在 Unity 中查看请求是否已执行。" : generate ? "已取消导入／生成；本次启动的 Unity 已结束，记录和已有文件保留。" : "已取消读取适配结果。"; }
        catch (Exception error) { status.Text = "适配操作未完成：" + error.Message; ShowPreviewFailure(inventoryTask, error); }
        finally
        {
            scanLog.AppendText(status.Text + Environment.NewLine);
            extractionCancellation = null; runningTask = null; SetObjectActionsEnabled(true);
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
                    scanLog.AppendText($"\r\n最近 Unity 操作：{run.Status} · {run.Message}\r\n项目：{run.Project}\r\n日志：{run.LogPath}\r\n复用已有结果：{run.Reused}；已验证播放：{run.PlaybackVerified}\r\n");
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
