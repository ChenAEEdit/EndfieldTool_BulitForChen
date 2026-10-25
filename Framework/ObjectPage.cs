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

    private void BuildObjectsPage()
    {
        var page = Page("对象与导出");
        var layout = Grid(1, 7);
        foreach (var height in new[] { 59, 45, 38, 65 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 40));
        layout.RowStyles.Add(new(SizeType.Absolute, 112));
        layout.Controls.Add(objectInfo, 0, 0);
        var actions = Flow();
        foreach (var button in new[] {
            ActionButton("选择任务 →", () => pages.SelectedIndex = 2),
            ActionButton("载入已有对象列表", () => RunAction(LoadSavedObjects)), exportObjects,
            ActionButton("打开导出目录", () => RunAction(() =>
            {
                var result = CurrentObjectResult();
                if (!Directory.Exists(result.OutputDirectory)) throw new InvalidDataException("该次任务尚未生成导出目录。");
                Process.Start(new ProcessStartInfo(result.OutputDirectory) { UseShellExecute = true });
            })),
            ActionButton("打开对象报告", () => RunAction(() =>
            {
                if (inventoryTask == null) throw new InvalidDataException("请先载入对象列表。");
                OpenLocal(Path.Combine(Path.GetDirectoryName(inventoryTask.FilePath)!, "objects", "result.json"));
            })) })
        {
            actions.Controls.Add(button);
            objectActions.Add(button);
        }
        layout.Controls.Add(actions, 0, 1);
        layout.Controls.Add(objectSearch, 0, 2);
        var selectors = Grid(2, 2);
        selectors.ColumnStyles.Add(new(SizeType.Percent, 50));
        selectors.ColumnStyles.Add(new(SizeType.Percent, 50));
        selectors.RowStyles.Add(new(SizeType.Absolute, 28));
        selectors.RowStyles.Add(new(SizeType.Percent, 100));
        selectors.Controls.Add(Note("模型根节点 → FBX（请选择一个）"), 0, 0);
        selectors.Controls.Add(Note("Avatar（自动选择仅依据实际 Animator 引用）"), 1, 0);
        selectors.Controls.Add(modelChoice, 0, 1);
        selectors.Controls.Add(avatarChoice, 1, 1);
        layout.Controls.Add(selectors, 0, 3);
        var lists = Grid(2, 1);
        lists.ColumnStyles.Add(new(SizeType.Percent, 58));
        lists.ColumnStyles.Add(new(SizeType.Percent, 42));
        objectRows.Margin = new Padding(0, 0, 10, 0);
        lists.Controls.Add(objectRows, 0, 0);
        lists.Controls.Add(clipRows, 1, 0);
        layout.Controls.Add(lists, 0, 4);
        var clipActions = Flow();
        clipActions.Controls.Add(ActionButton("勾选当前动画列表", () => SetClipChecks(true)));
        clipActions.Controls.Add(ActionButton("清空全部动画勾选", () => { checkedClips.Clear(); PopulateObjects(); }));
        layout.Controls.Add(clipActions, 0, 5);
        layout.Controls.Add(objectDetails, 0, 6);
        page.Controls.Add(layout);
        modelChoice.Items.Add(new ObjectChoice(null, "不导出模型")); modelChoice.SelectedIndex = 0;
        avatarChoice.Items.Add(new ObjectChoice(null, "不指定 Avatar")); avatarChoice.SelectedIndex = 0;
        inspectObjects.Click += async (_, _) => await RunObjectOperationAsync(false);
        exportObjects.Click += async (_, _) => await RunObjectOperationAsync(true);
        objectSearch.TextChanged += (_, _) => PopulateObjects();
        modelChoice.SelectedIndexChanged += (_, _) =>
        {
            var linked = (modelChoice.SelectedItem as ObjectChoice)?.Row?.LinkedAvatarKey;
            avatarChoice.SelectedIndex = 0;
            if (linked != null)
                for (int i = 1; i < avatarChoice.Items.Count; i++)
                    if ((avatarChoice.Items[i] as ObjectChoice)?.Row?.Key == linked) avatarChoice.SelectedIndex = i;
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
        objectDetails.Text = DescribeObjectResult(result);
    }

    private void LoadObjects(SavedPlan saved, string path)
    {
        var loaded = ObjectWorkflow.ReadInventory(saved, path);
        inventoryTask = saved; objectInventory = loaded; objectInventoryPath = path;
        checkedClips.Clear();
        modelChoice.Items.Clear(); avatarChoice.Items.Clear();
        modelChoice.Items.Add(new ObjectChoice(null, "不导出模型"));
        avatarChoice.Items.Add(new ObjectChoice(null, "不指定 Avatar"));
        foreach (var row in loaded.Objects.Where(row => row.IsModelCandidate)) modelChoice.Items.Add(new ObjectChoice(row));
        foreach (var row in loaded.Objects.Where(row => row.Kind == "Avatar")) avatarChoice.Items.Add(new ObjectChoice(row));
        modelChoice.SelectedIndex = avatarChoice.SelectedIndex = 0;
        objectSearch.Clear(); PopulateObjects();
        pages.SelectedIndex = 5;
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
            foreach (var row in rows.Where(row => row.Kind == "AnimationClip").Take(2000))
                clipRows.Items.Add(new ObjectChoice(row), checkedClips.Contains(row.Key));
        }
        finally { loadingClips = false; objectRows.EndUpdate(); clipRows.EndUpdate(); }
        UpdateObjectInfo();
    }

    private void UpdateObjectInfo()
    {
        if (objectInventory == null || inventoryTask == null) return;
        objectInfo.Text = $"当前：{inventoryTask.Plan.CharacterName} · 任务 {inventoryTask.Plan.Id.ToString("N")[..8]} · {objectInventory.Objects.Count} 个对象 · 已勾选 {checkedClips.Count} 条动画（含隐藏项）。\n每个列表最多显示 2000 项，可筛选；导出为 FBX／原始 .anim，未做 Unity 适配及播放验证。";
    }

    private void SetClipChecks(bool value)
    {
        for (int i = 0; i < clipRows.Items.Count; i++) clipRows.SetItemChecked(i, value);
    }

    private ObjectOperationResult CurrentObjectResult() => inventoryTask == null
        ? throw new InvalidDataException("请先载入任务的对象列表。")
        : ObjectWorkflow.ReadResult(inventoryTask) ?? throw new InvalidDataException("缺少对象操作结果。");

    private async Task RunObjectOperationAsync(bool export)
    {
        if (scanCancellation != null || extractionCancellation != null) return;
        SavedPlan saved;
        ObjectExportSelection? selection = null;
        try
        {
            saved = export ? inventoryTask ?? throw new InvalidDataException("请先解析或载入对象列表。") : SelectedTask();
            if (export)
            {
                selection = new((modelChoice.SelectedItem as ObjectChoice)?.Row?.Key,
                    (avatarChoice.SelectedItem as ObjectChoice)?.Row?.Key, checkedClips.Order().ToList());
                if (selection.ModelKey == null && selection.ClipKeys.Count == 0) throw new InvalidDataException("请选择模型根节点或勾选动画。");
            }
            else if (BundleExtractionBackend.ReadResult(saved)?.Status != "bundles_extracted")
                throw new InvalidDataException("请先完成该任务的 Bundle 提取。");
        }
        catch (Exception error) { status.Text = "无法开始：" + error.Message; return; }
        using var cancellation = new CancellationTokenSource();
        extractionCancellation = cancellation;
        SetObjectActionsEnabled(false);
        scan.Enabled = probe.Enabled = forceScan.Enabled = executeExtraction.Enabled = resumeExtraction.Enabled = false;
        foreach (var action in catalogActions) action.Enabled = false;
        pages.TabPages[3].Enabled = false; stopScan.Enabled = true;
        scanProgress.Value = 0; scanLog.Clear(); pages.SelectedIndex = 4;
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
            var result = await objectWorkflow.ExecuteAsync(saved, export ? "export" : "inspect", objectInventoryPath, selection, progress, cancellation.Token);
            if (!export) LoadObjects(saved, result.InventoryPath!);
            objectDetails.Text = DescribeObjectResult(result);
            status.Text = result.Status == "partial" ? "部分导出失败，请查看对象报告。" : result.Message;
            pages.SelectedIndex = 5;
        }
        catch (OperationCanceledException) { status.Text = "对象操作已取消；本次记录保留，重新执行会建立新的运行目录。"; }
        catch (Exception error) { status.Text = "对象操作未完成：" + error.Message; }
        finally
        {
            scanLog.AppendText(status.Text + Environment.NewLine);
            extractionCancellation = null; SetObjectActionsEnabled(true);
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
        + "\r\n" + string.Join("\r\n", result.Warnings.Select(message => "提示：" + message))
        + "\r\n" + string.Join("\r\n", result.Failures.Select(message => "失败：" + message)) + "\r\n";

    private static string ObjectStatus(string status) => status switch
    {
        "inspected" => "对象已解析 · 待选择",
        "exported_unverified" => "已导出 · 未适配／验证",
        "partial" => "部分导出失败",
        "running" => "对象处理中／中断待重试",
        "cancelled" => "对象操作已取消",
        "failed" => "对象操作失败",
        _ => "未知对象状态"
    };

    private sealed record ObjectChoice(BundleObjectRow? Row, string? Label = null)
    {
        public override string ToString() => Row == null ? Label ?? "无" : $"{Row.Name} [{Row.PathId}] · {Row.SourceAsset} · {Path.GetFileName(Row.SourceBundle)}";
    }
}
