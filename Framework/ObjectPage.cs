using System.Diagnostics;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

namespace EndfieldChenTool.Framework;

internal sealed partial class WorkspaceForm
{
    private readonly ObjectWorkflow objectWorkflow = new();
    private readonly Button inspectObjects = new() { Text = "解析 Bundle 对象", Width = 165, Height = 35 };
    private readonly Button exportObjects = new() { Text = "导出所选对象", Width = 150, Height = 35 };
    private readonly Label objectInfo = Note("先在任务记录中选择已完成 Bundle 提取的任务，再解析对象。");
    private readonly TextBox objectSearch = new() { Dock = DockStyle.Fill, PlaceholderText = "筛选实际对象和动画：名称、类型、来源或 PathID；隐藏的动画勾选仍保留" };
    private readonly ListView objectRows = List("类型", "名称", "PathID", "来源资产", "来源 Bundle");
    private readonly CheckedListBox clipRows = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true };
    private readonly ComboBox modelChoice = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 900 };
    private readonly ComboBox avatarChoice = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 900 };
    private readonly TextBox objectDetails = ReadOnlyText();
    private readonly HashSet<string> checkedClips = new(StringComparer.Ordinal);
    private readonly List<Control> objectActions = [];
    private SavedPlan? inventoryTask;
    private ObjectInventory? objectInventory;
    private string? objectInventoryPath;
    private bool loadingClips;
    private List<string> associatedRelatedModelKeys = [];

    private readonly Panel previewPane = new() { Dock = DockStyle.Fill };
    private readonly ComboBox clipCategory = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };

    private void BuildObjectsPage()
    {
        var page = Page("选择动作与预览");
        var body = Grid(2, 1);
        body.ColumnStyles.Add(new(SizeType.Percent, 52)); body.ColumnStyles.Add(new(SizeType.Percent, 48));
        var layout = Grid(1, 7); layout.Padding = new Padding(0, 0, 12, 0);
        foreach (var height in new[] { 49, 45, 100, 72 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 43));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(objectInfo, 0, 0);
        var actions = Flow();
        actions.Controls.Add(ActionButton("载入所选任务", () => RunAction(LoadSavedObjects)));
        actions.Controls.Add(ActionButton("自动关联专属待机", () => RunAction(SelectAssociatedIdleSet)));
        actions.Controls.Add(exportObjects); objectActions.Add(exportObjects);
        layout.Controls.Add(actions, 0, 1);
        var selectors = Grid(2, 2);
        selectors.ColumnStyles.Add(new(SizeType.Absolute, 78)); selectors.ColumnStyles.Add(new(SizeType.Percent, 100));
        selectors.RowStyles.Add(new(SizeType.Percent, 50)); selectors.RowStyles.Add(new(SizeType.Percent, 50));
        selectors.Controls.Add(Note("角色本体"), 0, 0); selectors.Controls.Add(modelChoice, 1, 0);
        selectors.Controls.Add(Note("骨架"), 0, 1); selectors.Controls.Add(avatarChoice, 1, 1);
        layout.Controls.Add(selectors, 0, 2);
        var filters = Grid(1, 2); filters.RowStyles.Add(new(SizeType.Percent, 50)); filters.RowStyles.Add(new(SizeType.Percent, 50));
        clipCategory.Items.AddRange(["当前角色名称相关（未检查兼容）", "待机／休息", "行走／移动", "全部动作"]);
        clipCategory.SelectedIndex = 0;
        filters.Controls.Add(clipCategory, 0, 0); filters.Controls.Add(objectSearch, 0, 1);
        layout.Controls.Add(filters, 0, 3);
        layout.Controls.Add(clipRows, 0, 4);
        var clipActions = Flow();
        clipActions.Controls.Add(ActionButton("勾选当前列表", () => SetClipChecks(true)));
        clipActions.Controls.Add(ActionButton("清空全部勾选", () => { checkedClips.Clear(); PopulateObjects(); }));
        layout.Controls.Add(clipActions, 0, 5);
        var metadata = Grid(1, 3); metadata.RowStyles.Add(new(SizeType.Absolute, 40)); metadata.RowStyles.Add(new(SizeType.Percent, 58)); metadata.RowStyles.Add(new(SizeType.Percent, 42));
        var investigate = new Button { Text = "调查关联道具", Width = 155, Height = 35 };
        investigate.Click += async (_, _) => await RunWorkflowAsync("inspect-props"); objectActions.Add(investigate);
        metadata.Controls.Add(investigate, 0, 0); metadata.Controls.Add(objectRows, 0, 1); metadata.Controls.Add(objectDetails, 0, 2);
        var advancedObjects = Collapsible("高级：原始对象与来源详情", metadata, 220);
        layout.Controls.Add(advancedObjects, 0, 6);
        body.Controls.Add(ScrollableWorkflow(layout, advancedObjects), 0, 0);
        body.Controls.Add(previewPane, 1, 0); page.Controls.Add(body);
        modelChoice.Items.Add(new ObjectChoice(null, "不导出模型")); modelChoice.SelectedIndex = 0;
        avatarChoice.Items.Add(new ObjectChoice(null, "不指定 Avatar")); avatarChoice.SelectedIndex = 0;
        inspectObjects.Click += async (_, _) => await RunObjectOperationAsync(false);
        exportObjects.Click += async (_, _) => await RunObjectOperationAsync(true);
        objectSearch.TextChanged += (_, _) => PopulateObjects();
        clipCategory.SelectedIndexChanged += (_, _) => PopulateObjects();
        modelChoice.SelectedIndexChanged += (_, _) =>
        {
            associatedRelatedModelKeys = [];
            var linked = (modelChoice.SelectedItem as ObjectChoice)?.Row?.LinkedAvatarKey;
            avatarChoice.SelectedIndex = 0;
            if (linked != null)
                for (int i = 1; i < avatarChoice.Items.Count; i++)
                    if ((avatarChoice.Items[i] as ObjectChoice)?.Row?.Key == linked) avatarChoice.SelectedIndex = i;
            UpdateAdaptationDefaults();
        };
        objectRows.SelectedIndexChanged += (_, _) =>
        {
            if (objectRows.SelectedItems.Count == 0) return;
            var row = (BundleObjectRow)objectRows.SelectedItems[0].Tag!;
            objectDetails.Text = $"{row.Kind} · {row.Name} · PathID {row.PathId}\r\n来源：{row.SourceBundle}\r\n资产：{row.SourceAsset}\r\n层级节点 {row.HierarchyNodes}；渲染器 {row.RendererCount}；骨骼引用合计 {row.BoneReferenceCount}（非去重骨骼数）；Animator 引用 Avatar：{row.LinkedAvatarKey ?? "无／未解析"}";
        };
        clipRows.ItemCheck += (_, e) =>
        {
            if (loadingClips) return;
            var key = ((ObjectChoice)clipRows.Items[e.Index]).Row!.Key;
            if (e.NewValue == CheckState.Checked) checkedClips.Add(key); else checkedClips.Remove(key);
            BeginInvoke(new Action(UpdateObjectInfo));
        };
        objectActions.Add(inspectObjects);
    }

    private void SetObjectActionsEnabled(bool enabled)
    {
        foreach (var control in objectActions) control.Enabled = enabled;
        // Preserve selections while an operation runs, but prevent edits during execution.
        pages.TabPages[5].Enabled = enabled;
        if (!enabled) { rootLayout.RowStyles[1].Height = 0; editingTargetTask = null; }
        retryConverted.Enabled = false;
        if (enabled && inventoryTask != null)
        {
            try
            {
                var summary = UnityFailureSummary.Read(inventoryTask);
                retryConverted.Enabled = summary?.ConvertedKeys.Count > 0 && summary.Problems.Count > 0;
            }
            catch (Exception) { /* A missing or unreadable report must not enable recovery. */ }
        }
        UpdateContext();
    }

    private void LoadSavedObjects()
    {
        if (scanCancellation != null || extractionCancellation != null) return;
        var saved = SelectedTask();
        var result = ObjectWorkflow.ReadResult(saved);
        if (result?.InventoryPath == null) throw new InvalidDataException("此任务尚无对象列表，请先解析 Bundle 对象。");
        var bundles = BundleExtractionBackend.ReadResult(saved);
        if (bundles?.Status != "bundles_extracted" || bundles.RunId != result.BundleRunId)
            throw new InvalidDataException("Bundle 提取记录已变化，请重新解析对象。");
        LoadObjects(saved, result.InventoryPath);
        trackAudit.LoadAudit(saved);
        objectDetails.Text = DescribeObjectResult(result);
        retryConverted.Enabled = false;
        try
        {
            var summary = UnityFailureSummary.Read(saved);
            adaptationDetails.Text = summary == null ? "选择动作后点击“生成 Unity 预览”。" : summary.Describe();
            retryConverted.Enabled = summary?.ConvertedKeys.Count > 0 && summary.Problems.Count > 0;
        }
        catch (Exception error) { adaptationDetails.Text = "历史生成结果暂不可读：" + error.Message; }
    }

    private void LoadObjects(SavedPlan saved, string path)
    {
        var loaded = ObjectWorkflow.ReadInventory(saved, path);
        inventoryTask = saved; objectInventory = loaded; objectInventoryPath = path;
        checkedClips.Clear();
        modelChoice.Items.Clear(); avatarChoice.Items.Clear();
        sourceRigChoice.Items.Clear(); sourceRigChoice.Items.Add(new ObjectChoice(null, "直接绑定（专属动画）；跨角色请选择有证据的动作原骨架")); sourceRigChoice.SelectedIndex = 0;
        modelChoice.Items.Add(new ObjectChoice(null, "不导出模型"));
        avatarChoice.Items.Add(new ObjectChoice(null, "不指定 Avatar"));
        foreach (var row in loaded.Objects.Where(row => row.IsModelCandidate)) modelChoice.Items.Add(new ObjectChoice(row));
        foreach (var row in loaded.Objects.Where(row => row.Kind == "Avatar")) avatarChoice.Items.Add(new ObjectChoice(row));
        foreach (var row in loaded.Objects.Where(row => row.IsModelCandidate && row.HierarchyNodes >= 15 && row.RigEvidencePath != null)) sourceRigChoice.Items.Add(new ObjectChoice(row));
        modelChoice.SelectedIndex = avatarChoice.SelectedIndex = 0;
        var preferred = loaded.Objects.Where(row => row.IsModelCandidate
            && (row.PathId == saved.Plan.RootPathId || row.Name == saved.Plan.RootName)).ToList();
        if (preferred.Count == 0) preferred = loaded.Objects.Where(row => row.IsModelCandidate).ToList();
        if (preferred.Count == 1)
            for (int i = 1; i < modelChoice.Items.Count; i++)
                if ((modelChoice.Items[i] as ObjectChoice)?.Row?.Key == preferred[0].Key) modelChoice.SelectedIndex = i;
        objectSearch.Clear(); PopulateObjects();
        UpdateContext();
    }

    private void PopulateObjects()
    {
        if (objectInventory == null) return;
        var query = objectSearch.Text.Trim();
        var rows = objectInventory.Objects.Where(row => string.Join(" ", row.Kind, row.Name, row.PathId, row.SourceAsset, row.SourceBundle)
            .Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        objectRows.BeginUpdate(); clipRows.BeginUpdate(); loadingClips = true;
        try
        {
            objectRows.Items.Clear(); clipRows.Items.Clear();
            foreach (var row in rows.Take(2000))
            {
                var item = new ListViewItem(row.Kind) { Tag = row };
                item.SubItems.AddRange([row.Name, row.PathId, row.SourceAsset, Path.GetFileName(row.SourceBundle)]);
                objectRows.Items.Add(item);
            }
            var clips = rows.Where(row => row.Kind == "AnimationClip");
            bool Related(BundleObjectRow row) => row.Name.Contains("_" + inventoryTask!.Plan.CharacterId + "_", StringComparison.OrdinalIgnoreCase);
            clips = clipCategory.SelectedIndex switch
            {
                0 => objectInventory.Objects.Any(row => row.Kind == "AnimationClip" && Related(row)) ? clips.Where(Related) : clips,
                1 => clips.Where(row => new[] { "idle", "loop", "relax", "stand" }.Any(term => row.Name.Contains(term, StringComparison.OrdinalIgnoreCase))),
                2 => clips.Where(row => new[] { "walk", "run", "move" }.Any(term => row.Name.Contains(term, StringComparison.OrdinalIgnoreCase))),
                _ => clips
            };
            foreach (var row in clips.Take(2000))
                clipRows.Items.Add(new ObjectChoice(row), checkedClips.Contains(row.Key));
        }
        finally { loadingClips = false; objectRows.EndUpdate(); clipRows.EndUpdate(); }
        UpdateObjectInfo();
    }

    private void UpdateObjectInfo()
    {
        if (objectInventory == null || inventoryTask == null) return;
        int visibleSelected = clipRows.Items.Cast<ObjectChoice>().Count(choice => checkedClips.Contains(choice.Row!.Key));
        objectInfo.Text = $"已选 {checkedClips.Count} 条动作，其中 {checkedClips.Count - visibleSelected} 条不在当前筛选内。\n名称筛选不代表骨架兼容；没有名称关联项时显示全部。";
        UpdateContext();
    }

    private void SetClipChecks(bool value)
    {
        for (int i = 0; i < clipRows.Items.Count; i++) clipRows.SetItemChecked(i, value);
    }

    private void SelectAssociatedIdleSet()
    {
        if (objectInventory == null || inventoryTask == null) throw new InvalidDataException("请先载入角色对象列表。");
        var resolved = CharacterAssociationResolver.Resolve(objectInventory, inventoryTask.Plan.CharacterId, includeSpecialIdle: true);
        int modelIndex = Enumerable.Range(1, modelChoice.Items.Count - 1)
            .FirstOrDefault(index => (modelChoice.Items[index] as ObjectChoice)?.Row?.Key == resolved.Model.Key);
        int avatarIndex = Enumerable.Range(1, avatarChoice.Items.Count - 1)
            .FirstOrDefault(index => (avatarChoice.Items[index] as ObjectChoice)?.Row?.Key == resolved.Avatar.Key);
        if (modelIndex == 0 || avatarIndex == 0) throw new InvalidDataException("关联结果不在当前模型或 Avatar 选择列表中，请重新解析对象。");

        modelChoice.SelectedIndex = modelIndex;
        avatarChoice.SelectedIndex = avatarIndex;
        associatedRelatedModelKeys = resolved.RelatedModels.Select(row => row.Key).ToList();
        checkedClips.Clear();
        checkedClips.UnionWith(resolved.Clips.Select(row => row.Key));
        PopulateObjects();
        objectDetails.Text = "自动关联依据：唯一角色本体 → 实际 Animator Avatar/Controller 引用 → Controller 引用的待机动画。导出包含本体子层级中的关联模型，并核对初始显隐。"
            + $"\r\n控制器：{resolved.ControllerKey}；引用动画：{resolved.ReferencedClipCount}；已选择待机：{string.Join("、", resolved.Clips.Select(row => row.Name))}。"
            + $"\r\n层级内关联模型候选：{associatedRelatedModelKeys.Count}；独立道具需要额外挂点及行为证据。"
            + "\r\n源骨架归属未由控制器关联证明；共享走路仍需单独选择有证据的来源。";
    }

    private ObjectOperationResult CurrentObjectResult() => inventoryTask == null
        ? throw new InvalidDataException("请先载入任务的对象列表。")
        : ObjectWorkflow.ReadResult(inventoryTask) ?? throw new InvalidDataException("缺少对象操作结果。");

    private Task RunObjectOperationAsync(bool export) => RunWorkflowAsync(export ? "export" : "inspect");

    private async Task RunWorkflowAsync(string mode)
    {
        bool investigate = mode == "inspect-props", preview = mode == "preview", export = mode is not ("inspect" or "inspect-props"), prepare = mode is "prepare" or "preview";
        if (scanCancellation != null || extractionCancellation != null) return;
        SavedPlan saved;
        ObjectExportSelection? selection = null;
        var preserved = investigate ? new {
            Model = (modelChoice.SelectedItem as ObjectChoice)?.Row, Avatar = (avatarChoice.SelectedItem as ObjectChoice)?.Row,
            SourceRig = (sourceRigChoice.SelectedItem as ObjectChoice)?.Row,
            Clips = objectInventory?.Objects.Where(row => checkedClips.Contains(row.Key)).ToArray() ?? [],
            Related = objectInventory?.Objects.Where(row => associatedRelatedModelKeys.Contains(row.Key)).ToArray() ?? [],
            Rules = AdaptationOptions()
        } : null;
        try
        {
            saved = export || investigate ? inventoryTask ?? throw new InvalidDataException("请先解析或载入对象列表。") : SelectedTask();
            if (investigate && objectInventoryPath == null) throw new InvalidDataException("请先载入可核对的对象快照。");
            if (export)
            {
                selection = new((modelChoice.SelectedItem as ObjectChoice)?.Row?.Key,
                    (avatarChoice.SelectedItem as ObjectChoice)?.Row?.Key, checkedClips.Order().ToList(), prepare ? AdaptationOptions() : null)
                    { RelatedModelKeys = associatedRelatedModelKeys.ToList() };
                if (selection.ModelKey == null && selection.ClipKeys.Count == 0) throw new InvalidDataException("请选择模型根节点或勾选动画。");
                if (prepare && (selection.ModelKey == null || selection.AvatarKey == null || selection.ClipKeys.Count == 0))
                    throw new InvalidDataException("生成适配包需要选择模型、Avatar 和至少一条动画。");
                if (preview)
                {
                    var target = UnityTaskTarget.Resolve(saved);
                    UnityAdaptationRunner.ValidateProject(target.Plan.Options.UnityProject, target.Plan.Options.UnityEditor);
                    UnityAdaptationRunner.RequireClosedProject(target.Plan.Options.UnityProject);
                }
            }
            else if (BundleExtractionBackend.ReadResult(saved)?.Status != "bundles_extracted")
                throw new InvalidDataException("请先完成该任务的 Bundle 提取。");
        }
        catch (Exception error) { status.Text = "无法开始：" + error.Message; if (prepare) adaptationDetails.Text = status.Text; return; }
        using var cancellation = new CancellationTokenSource();
        extractionCancellation = cancellation;
        runningTask = saved;
        SetObjectActionsEnabled(false);
        scan.Enabled = probe.Enabled = forceScan.Enabled = executeExtraction.Enabled = resumeExtraction.Enabled = false;
        foreach (var action in catalogActions) action.Enabled = false;
        pages.TabPages[3].Enabled = false; stopScan.Enabled = true;
        scanProgress.Value = 0; scanLog.Clear();
        latestIndexReport = Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "objects", "result.json");
        status.Text = export ? "正在导出所选对象…" : "正在解析 Bundle 中的实际对象…";
        try
        {
            var progress = new Progress<ExtractionProgress>(row =>
            {
                if (IsDisposed || cancellation.IsCancellationRequested || !ReferenceEquals(extractionCancellation, cancellation)) return;
                status.Text = row.Message; scanProgress.Value = Math.Clamp(row.Percent, 0, 100);
                if (scanLog.TextLength > 150000) scanLog.Text = scanLog.Text[^75000..];
                scanLog.AppendText(DateTime.Now.ToString("HH:mm:ss ") + row.Message + Environment.NewLine);
            });
            var result = investigate
                ? await objectWorkflow.ExpandModelReferencesAsync(saved, objectInventoryPath!, progress, cancellation.Token)
                : await objectWorkflow.ExecuteAsync(saved, preview ? "prepare" : mode, objectInventoryPath, selection, progress, cancellation.Token);
            scanProgress.Value = 100;
            if (!export)
            {
                LoadObjects(saved, result.InventoryPath!);
                if (preserved != null)
                {
                    void Restore(ComboBox combo, BundleObjectRow? old)
                    {
                        if (old == null) { combo.SelectedIndex = 0; return; }
                        var row = ObjectWorkflow.MatchSnapshotObject(objectInventory!, old);
                        int index = Enumerable.Range(1, combo.Items.Count - 1).Single(i => (combo.Items[i] as ObjectChoice)?.Row?.Key == row.Key);
                        combo.SelectedIndex = index;
                    }
                    Restore(modelChoice, preserved.Model); Restore(avatarChoice, preserved.Avatar); Restore(sourceRigChoice, preserved.SourceRig);
                    checkedClips.UnionWith(preserved.Clips.Select(row => ObjectWorkflow.MatchSnapshotObject(objectInventory!, row).Key));
                    associatedRelatedModelKeys = preserved.Related.Select(row => ObjectWorkflow.MatchSnapshotObject(objectInventory!, row).Key).ToList();
                    var rules = preserved.Rules;
                    modelImportScale.Value = rules.ModelImportScale; animationPositionScale.Value = rules.PositionScale; inferUnits.Checked = rules.InferUnits;
                    dropUnmatched.Checked = rules.DropUnmatched; dropSceneTranslation.Checked = rules.DropSceneTranslation;
                    forceLoop.Checked = rules.ForceLoop; createPreviewScene.Checked = rules.CreatePreviewScene;
                    normalizeRoot.Text = rules.NormalizeRoot; excludedDescendants.Lines = rules.ExcludedDescendants.ToArray(); excludedNodes.Lines = rules.ExcludedNodes.ToArray();
                    PopulateObjects();
                }
            }
            objectDetails.Text = DescribeObjectResult(result);
            status.Text = result.Status == "partial" ? "部分导出失败，请查看对象报告。" : result.Message;
            if (prepare) adaptationDetails.Text = DescribeObjectResult(result) + "\r\n适配包：" + result.UnityPackageDirectory;
            if (preview)
            {
                var outcome = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, cancellation.Token);
                result = outcome.Result;
                adaptationDetails.Text = PreviewResultText(saved, result);
                trackAudit.LoadAudit(saved);
                status.Text = result.Message;
            }
        }
        catch (OperationCanceledException) { status.Text = "对象操作已取消；本次记录保留，重新执行会建立新的运行目录。"; }
        catch (Exception error)
        {
            status.Text = "操作未完成：" + error.Message;
            if (prepare) ShowPreviewFailure(saved, error);
        }
        finally
        {
            scanLog.AppendText(status.Text + Environment.NewLine);
            extractionCancellation = null; runningTask = null; SetObjectActionsEnabled(true);
            scan.Enabled = probe.Enabled = forceScan.Enabled = executeExtraction.Enabled = resumeExtraction.Enabled = true;
            foreach (var action in catalogActions) action.Enabled = true;
            pages.TabPages[3].Enabled = true; stopScan.Enabled = false;
            var message = status.Text;
            try
            {
                RefreshTasks();
                foreach (ListViewItem row in tasks.Items)
                    if (((SavedPlan)row.Tag!).Plan.Id == saved.Plan.Id) row.Selected = true;
                ShowTask(); status.Text = message;
            }
            catch (Exception error) { status.Text = message + "；任务记录读取失败：" + error.Message; }
            if (closeAfterScan) BeginInvoke(new Action(Close));
        }
    }

    private static string DescribeObjectResult(ObjectOperationResult? result) => result == null ? "\r\n尚无对象解析或导出记录。\r\n" :
        $"\r\n对象操作：{ObjectStatus(result.Status)} · {result.Message}\r\n来源 Bundle 运行：{result.BundleRunId}\r\n输出文件：{result.Files.Count}；已适配：{result.Adapted}；已验证播放：{result.PlaybackVerified}\r\n"
        + string.Join("\r\n", result.Files.Take(40).Select(row => $"  [{row.Kind}] {row.Path}"))
        + (result.UnityPreviewScene == null ? "" : "\r\n预览场景：" + result.UnityPreviewScene + "\r\n")
        + "\r\n" + string.Join("\r\n", result.Warnings.Select(message => "提示：" + message))
        + "\r\n" + string.Join("\r\n", result.Failures.Select(message => "失败：" + message)) + "\r\n";

    private static string ObjectStatus(string status) => status switch
    {
        "inspected" => "对象已解析 · 待选择",
        "exported_unverified" => "已导出 · 未适配／验证",
        "unity_prepared" => "适配包已准备 · 待 Unity 生成",
        "adapted_unverified" => "已适配 · 未验证播放",
        "partial" => "部分导出失败",
        "running" => "对象处理中／中断待重试",
        "cancelled" => "对象操作已取消",
        "failed" => "对象操作失败",
        _ => "未知对象状态"
    };

    private sealed record ObjectChoice(BundleObjectRow? Row, string? Label = null)
    {
        public override string ToString() => Row == null ? Label ?? "无" : $"{Row.Name} [{Row.PathId}]";
    }
}
