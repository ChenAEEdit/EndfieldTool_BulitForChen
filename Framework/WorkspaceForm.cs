using System.Diagnostics;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

namespace EndfieldChenTool.Framework;

internal sealed partial class WorkspaceForm : Form
{
    private static readonly Color Ink = Color.FromArgb(32, 45, 62);
    private static readonly Color Accent = Color.FromArgb(33, 100, 116);
    private readonly TabControl pages = new() { Dock = DockStyle.Fill };
    private readonly TextBox search = new() { Dock = DockStyle.Fill, PlaceholderText = "搜索角色名称、代码或资源路径" };
    private readonly TextBox resourceSearch = new() { Dock = DockStyle.Fill, PlaceholderText = "筛选资源名称或路径，例如 idle、walk；隐藏的勾选仍计入计划" };
    private readonly HashSet<string> checkedResourceIds = new(StringComparer.Ordinal);
    private readonly ListView characters = List("角色", "代码");
    private readonly CheckedListBox resources = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly TextBox resourceDetails = ReadOnlyText();
    private readonly TextBox planDetails = ReadOnlyText();
    private readonly TextBox taskDetails = ReadOnlyText();
    private readonly ListView tasks = List("角色", "建立时间", "状态");
    private readonly Label catalogInfo = Note("尚未加载资源索引。");
    private readonly Label status = Note("准备就绪。");
    private readonly Dictionary<string, TextBox> paths = [];
    private readonly CheckBox importUnity = new() { Text = "生成 Unity 预览", AutoSize = true };
    private readonly CheckBox checkWeights = new() { Text = "检查骨骼权重", AutoSize = true };
    private readonly CheckBox verifyPlayback = new() { Text = "检查实际播放", AutoSize = true };
    private readonly WorkspaceStore store = new();
    private readonly ITaskPlanner planner = new TaskPlanner();
    private readonly IGameResourceIndexer indexer = new GameResourceIndexer();
    private readonly IExtractionBackend extractor = new BundleExtractionBackend();
    private readonly Button executeExtraction = new() { Text = "准备角色与动作", Width = 185, Height = 35 };
    private readonly Button resumeExtraction = new() { Text = "继续准备资源", Width = 160, Height = 35 };
    private readonly Button scan = new() { Text = "扫描游戏资源", Width = 145, Height = 35 };
    private readonly Button probe = new() { Text = "检查索引兼容性", Width = 135, Height = 35 };
    private readonly Button stopScan = new() { Text = "取消扫描", Width = 95, Height = 35, Enabled = false };
    private readonly CheckBox forceScan = new() { Text = "重新扫描", AutoSize = true };
    private readonly ProgressBar scanProgress = new() { Dock = DockStyle.Fill, Maximum = 100 };
    private readonly TextBox scanLog = ReadOnlyText();
    private readonly List<Control> catalogActions = [];
    private readonly System.Windows.Forms.Timer searchTimer = new() { Interval = 250 };
    private CancellationTokenSource? scanCancellation;
    private CancellationTokenSource? extractionCancellation;
    private string? latestIndexReport;
    private bool updatePlanQueued, closeAfterScan, loadingResources;
    private ResourceCatalog? catalog;
    private CharacterEntry? selectedCharacter;

    public WorkspaceForm()
    {
        Text = "终末地角色工作台 · 源骨架与轨道诊断 0.12.0";
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        BackColor = Color.FromArgb(244, 247, 249);
        ForeColor = Ink;
        ClientSize = new Size(1320, 900);
        MinimumSize = new Size(1050, 800);
        StartPosition = FormStartPosition.CenterScreen;
        Shown += (_, _) => FitToDisplay();
        DpiChanged += (_, _) => BeginInvoke(new Action(FitToDisplay));

        var layout = rootLayout = Grid(1, 4);
        layout.Padding = new Padding(18);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 115));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 63));
        layout.Controls.Add(BuildContextHeader(), 0, 0);
        layout.Controls.Add(BuildTargetEditor(), 0, 1);
        var footer = Grid(2, 2);
        footer.ColumnStyles.Add(new(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new(SizeType.Absolute, 112));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(stopScan, 1, 0);
        footer.Controls.Add(scanProgress, 0, 1);
        footer.SetColumnSpan(scanProgress, 2);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);

        var settings = LoadSettings();
        BuildCatalogPage();
        BuildPlanPage();
        BuildTasksPage();
        BuildSettingsPage(settings);
        BuildScanPage();
        BuildObjectsPage();
        BuildAnimationPage();
        layout.Controls.Add(BuildNavigation(), 0, 2);
        search.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RefreshCharacters(); };
        characters.SelectedIndexChanged += (_, _) => SelectCharacter();
        resources.SelectedIndexChanged += (_, _) => ShowResource();
        resourceSearch.TextChanged += (_, _) => PopulateResources();
        resources.ItemCheck += (_, e) =>
        {
            if (loadingResources) return;
            var entry = ((ResourceChoice)resources.Items[e.Index]).Entry;
            if (e.NewValue == CheckState.Checked) checkedResourceIds.Add(entry.Id);
            else checkedResourceIds.Remove(entry.Id);
            if (IsHandleCreated && !updatePlanQueued)
            {
                updatePlanQueued = true;
                BeginInvoke(new Action(() => { updatePlanQueued = false; UpdatePlan(); }));
            }
        };
        tasks.SelectedIndexChanged += (_, _) => ShowTask();
        foreach (var box in paths.Values) box.TextChanged += (_, _) => UpdatePlan();
        checkWeights.CheckedChanged += (_, _) => UpdatePlan();
        verifyPlayback.CheckedChanged += (_, _) => UpdatePlan();
        importUnity.CheckedChanged += (_, _) =>
        {
            verifyPlayback.Enabled = importUnity.Checked;
            if (!importUnity.Checked) verifyPlayback.Checked = false;
            UpdatePlan();
        };
        verifyPlayback.Enabled = importUnity.Checked;
        scan.Click += async (_, _) => await ScanGameAsync();
        probe.Click += async (_, _) => await ScanGameAsync(probeOnly: true);
        executeExtraction.Click += async (_, _) => await ExecutePlanAsync(resume: false);
        resumeExtraction.Click += async (_, _) => await ExecutePlanAsync(resume: true);
        stopScan.Text = "取消任务";
        stopScan.Click += (_, _) => { scanCancellation?.Cancel(); extractionCancellation?.Cancel(); };
        FormClosing += (_, e) =>
        {
            if (scanCancellation == null && extractionCancellation == null) return;
            e.Cancel = true;
            closeAfterScan = true;
            scanCancellation?.Cancel();
            extractionCancellation?.Cancel();
            status.Text = "正在停止任务并关闭，完整输出保留。";
        };
        FormClosed += (_, _) => searchTimer.Dispose();
        RunAction(LoadSupportedCatalog);
        RunAction(RefreshTasks);
        sectionButtons[0].BackColor = Accent; sectionButtons[0].ForeColor = Color.White;
        UpdateContext();
    }

    private void FitToDisplay()
    {
        var area = Screen.FromControl(this).WorkingArea;
        float scale = DeviceDpi / 96f;
        MinimumSize = new Size(Math.Min(area.Width, (int)Math.Ceiling(1050 * scale)),
            Math.Min(area.Height, (int)Math.Ceiling(800 * scale)));
        if (Width > area.Width || Height > area.Height || !area.Contains(Bounds))
        {
            int width = Math.Min(Width, area.Width), height = Math.Min(Height, area.Height);
            Bounds = new Rectangle(Math.Clamp(Left, area.Left, area.Right - width),
                Math.Clamp(Top, area.Top, area.Bottom - height), width, height);
        }
    }

    private void BuildCatalogPage()
    {
        var page = Page("角色与资源");
        var layout = Grid(1, 4);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 51));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Note("更新游戏后可先检查索引兼容性。格式可读的新版本仍标为未验证；扫描名称候选不代表已确认骨架或完成解码。"), 0, 0);
        var tools = Flow();
        foreach (var button in new[] {
            ActionButton("加载已支持角色", () => RunAction(LoadSupportedCatalog)),
            ActionButton("导入索引…", () => RunAction(ImportCatalog)),
            ActionButton("另存索引…", () => RunAction(ExportCatalog)) })
        {
            button.Width = 135;
            tools.Controls.Add(button);
            catalogActions.Add(button);
        }
        tools.Controls.AddRange([probe, scan, forceScan]);
        layout.Controls.Add(tools, 0, 1);
        var searchRow = Grid(2, 1);
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        search.Margin = new Padding(0, 8, 16, 0);
        searchRow.Controls.Add(search, 0, 0);
        searchRow.Controls.Add(catalogInfo, 1, 0);
        layout.Controls.Add(searchRow, 0, 2);

        var body = Grid(2, 1);
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        characters.Margin = new Padding(0, 0, 12, 0);
        body.Controls.Add(characters, 0, 0);
        var right = Grid(1, 4);
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 161));
        var choices = Flow();
        choices.Controls.Add(ActionButton("全选当前列表", () => SetChecks(true)));
        choices.Controls.Add(ActionButton("取消当前勾选", () => SetChecks(false)));
        choices.Controls.Add(ActionButton("准备角色资源 →", () => { UpdatePlan(); NavigateTo(1); }));
        right.Controls.Add(choices, 0, 0);
        right.Controls.Add(resourceSearch, 0, 1);
        right.Controls.Add(resources, 0, 2);
        right.Controls.Add(resourceDetails, 0, 3);
        body.Controls.Add(right, 1, 0);
        layout.Controls.Add(body, 0, 3);
        page.Controls.Add(layout);
    }

    private void BuildScanPage()
    {
        var page = Page("运行日志");
        var layout = Grid(1, 3);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.Controls.Add(Note("这里查看运行日志与使用说明。运行时页面保持原位，底部显示进度；详细日志会自动追加在此处。"), 0, 0);
        layout.Controls.Add(scanLog, 0, 1);
        var actions = Flow();
        actions.Controls.Add(ActionButton("打开当前结果报告", () => RunAction(() =>
        {
            if (latestIndexReport == null) throw new InvalidDataException("当前任务还没有结果报告。");
            ShowInlineLog(latestIndexReport);
        })));
        actions.Controls.Add(ActionButton("使用说明", () => RunAction(() => ShowInlineLog(Path.Combine(AppContext.BaseDirectory, "docs", "统一工作流使用说明.md")))));
        actions.Controls.Add(ActionButton("取消当前任务", () => { scanCancellation?.Cancel(); extractionCancellation?.Cancel(); }));
        layout.Controls.Add(actions, 0, 2);
        page.Controls.Add(layout);
    }

    private async Task ScanGameAsync(bool probeOnly = false)
    {
        if (scanCancellation != null || extractionCancellation != null) return;
        var options = CurrentOptions();
        var force = forceScan.Checked;
        using var cancellation = new CancellationTokenSource();
        scanCancellation = cancellation;
        scan.Enabled = probe.Enabled = forceScan.Enabled = false;
        executeExtraction.Enabled = resumeExtraction.Enabled = false;
        SetObjectActionsEnabled(false);
        stopScan.Enabled = true;
        foreach (var action in catalogActions) action.Enabled = false;
        pages.TabPages[3].Enabled = false;
        scanProgress.Value = 0;
        scanLog.Clear();
        latestIndexReport = null;
        try
        {
            var progress = new Progress<IndexProgress>(row =>
            {
                if (IsDisposed || !ReferenceEquals(scanCancellation, cancellation) || cancellation.IsCancellationRequested) return;
                status.Text = row.Message;
                scanProgress.Value = row.Percent;
                if (scanLog.TextLength > 150000) scanLog.Text = scanLog.Text[^75000..];
                scanLog.AppendText(DateTime.Now.ToString("HH:mm:ss ") + row.Message + Environment.NewLine);
            });
            var result = await indexer.BuildAsync(options, force, progress, cancellation.Token, probeOnly);
            latestIndexReport = result.ReportPath;
            if (result.Catalog != null)
            {
                catalog = result.Catalog;
                search.Clear();
                RefreshCharacters();
                status.Text = $"索引已完成：{catalog.Characters.Count} 个角色名称候选；{CompatibilityText(result.CompatibilityStatus)}。";
            }
            else status.Text = "索引兼容性检查：" + CompatibilityText(result.CompatibilityStatus);
            scanLog.AppendText($"\r\n兼容状态：{CompatibilityText(result.CompatibilityStatus)}\r\n索引：{result.CatalogPath ?? "本次只检查格式，未扫描名称"}\r\n报告：{result.ReportPath}\r\n日志：{result.LogPath}\r\n{result.Catalog?.CoverageNote}\r\n");
        }
        catch (OperationCanceledException)
        {
            status.Text = "扫描已取消；保留已完成缓存，再次扫描可继续。";
            scanLog.AppendText(status.Text + Environment.NewLine);
        }
        catch (GameIndexFailureException error)
        {
            latestIndexReport = error.ReportPath;
            status.Text = "索引格式／读取检查未通过：" + error.Message;
            scanLog.AppendText(status.Text + Environment.NewLine);
        }
        catch (Exception error)
        {
            status.Text = "索引未完成：" + error.Message;
            scanLog.AppendText(status.Text + Environment.NewLine);
        }
        finally
        {
            scanCancellation = null;
            scan.Enabled = probe.Enabled = forceScan.Enabled = true;
            executeExtraction.Enabled = resumeExtraction.Enabled = true;
            SetObjectActionsEnabled(true);
            stopScan.Enabled = false;
            foreach (var action in catalogActions) action.Enabled = true;
            pages.TabPages[3].Enabled = true;
            if (closeAfterScan) BeginInvoke(new Action(Close));
        }
    }

    private void BuildPlanPage()
    {
        var page = Page("提取计划");
        var layout = Grid(1, 3);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        layout.Controls.Add(Note("点击“准备角色与动作”自动提取并解析资源。完成后点击顶部第 3 步选择动作；需要保留选择但稍后执行时，可仅保存计划。"), 0, 0);
        layout.Controls.Add(planDetails, 0, 1);
        var actions = Flow();
        actions.Controls.Add(ActionButton("保存提取计划", () => RunAction(SavePlan)));
        actions.Controls.Add(executeExtraction);
        actions.Controls.Add(ActionButton("设置默认项目", () => NavigateTo(3)));
        actions.Controls.Add(ActionButton("载入所选任务的动作", () => RunAction(() => { LoadSavedObjects(); NavigateTo(5); })));
        layout.Controls.Add(actions, 0, 2);
        page.Controls.Add(layout);
    }

    private void BuildTasksPage()
    {
        var page = Page("任务记录");
        var layout = Grid(1, 4);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 51));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Note("选择历史任务后，点击“打开角色与动作”继续。顶部显示当前任务的目标项目；详细记录默认收起。"), 0, 0);
        var actions = Flow();
        actions.Controls.Add(ActionButton("读取工作区任务", () => RunAction(RefreshTasks)));
        actions.Controls.Add(ActionButton("打开角色与动作", () => RunAction(() => { LoadSavedObjects(); NavigateTo(5); })));
        actions.Controls.Add(resumeExtraction);
        var detailedActions = Flow();
        detailedActions.Controls.Add(ActionButton("打开所选计划文件", () => RunAction(() =>
        {
            if (tasks.SelectedItems.Count == 0) throw new InvalidDataException("请先选择一个任务。");
            OpenLocal(((SavedPlan)tasks.SelectedItems[0].Tag!).FilePath);
        })));
        detailedActions.Controls.Add(ActionButton("打开提取目录", () => RunAction(() =>
        {
            var folder = Path.Combine(Path.GetDirectoryName(SelectedTask().FilePath)!, "bundles");
            if (!Directory.Exists(folder)) throw new InvalidDataException("该任务尚未生成 Bundle 目录。");
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        })));
        detailedActions.Controls.Add(ActionButton("查看提取结果", () => RunAction(() =>
            ShowInlineLog(Path.Combine(Path.GetDirectoryName(SelectedTask().FilePath)!, "result.json")))));
        detailedActions.Controls.Add(inspectObjects);
        layout.Controls.Add(actions, 0, 1);
        layout.Controls.Add(tasks, 0, 2);
        var details = Grid(1, 2);
        details.RowStyles.Add(new(SizeType.Absolute, 85)); details.RowStyles.Add(new(SizeType.Percent, 100));
        details.Controls.Add(detailedActions, 0, 0); details.Controls.Add(taskDetails, 0, 1);
        layout.Controls.Add(Collapsible("高级：任务详情、文件与单独解析", details, 330), 0, 3);
        page.Controls.Add(layout);
    }

    private void BuildSettingsPage(WorkspaceOptions options)
    {
        var page = Page("设置");
        var layout = Grid(1, 5);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 255));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Note("这些是新任务的默认设置。已有任务请使用窗口顶部的“更换当前任务项目”，当前任务目标始终显示在顶部。"), 0, 0);
        var table = Grid(3, 5);
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        AddPath(table, 0, "游戏目录", "game", options.GameDirectory, false);
        AddPath(table, 1, "独立输出工作区", "workspace", options.WorkspaceDirectory, false);
        AddPath(table, 2, "Unity 项目", "project", options.UnityProject, false);
        AddPath(table, 3, "Unity 编辑器", "unity", options.UnityEditor, true);
        AddPath(table, 4, "Blender", "blender", options.Blender, true);
        layout.Controls.Add(table, 0, 1);
        var flags = Flow();
        importUnity.Text = "预览场景请从动画适配页生成";
        checkWeights.Text = "权重检查（待接入）";
        verifyPlayback.Text = "播放检查（待接入）";
        importUnity.Checked = checkWeights.Checked = verifyPlayback.Checked = false;
        importUnity.Enabled = checkWeights.Enabled = verifyPlayback.Enabled = false;
        flags.Controls.AddRange([importUnity, checkWeights, verifyPlayback]);
        foreach (Control control in flags.Controls) control.Margin = new Padding(4, 10, 25, 0);
        layout.Controls.Add(flags, 0, 2);
        var actions = Flow();
        actions.Controls.Add(ActionButton("保存环境设置", () => RunAction(() =>
        {
            JsonFiles.Write(WorkspaceStore.SettingsPath, CurrentOptions());
            UpdateContext();
            status.Text = "新任务默认设置已保存。已有任务的项目不变，可在顶部单独更换。";
        })));
        layout.Controls.Add(actions, 0, 3);
        layout.Controls.Add(Note("主要流程：角色与动作 → 选择资源并准备 → 选择模型和动作 → 生成 Unity 预览。\n\n准备包、导入 Unity、读取结果现在由一个主按钮完成；单独执行入口收在高级选项中。\n\n历史陈专用流程保留为命令行兼容入口 --legacy，主界面不再打开另一个操作窗口。\n\nJSON 保存环境设置、提取计划和运行结果；更换当前任务的 Unity 项目使用独立目标记录，不修改原计划摘要。"), 0, 4);
        page.Controls.Add(layout);
    }

    private void AddPath(TableLayoutPanel table, int row, string title, string key, string value, bool executable)
    {
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        table.Controls.Add(Note(title), 0, row);
        var box = new TextBox { Text = value, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 10, 0) };
        paths[key] = box;
        table.Controls.Add(box, 1, row);
        var browse = ActionButton("浏览…", () =>
        {
            if (executable)
            {
                using var dialog = new OpenFileDialog { Filter = "应用程序 (*.exe)|*.exe", FileName = box.Text };
                if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName;
            }
            else
            {
                using var dialog = new FolderBrowserDialog { Description = title, SelectedPath = box.Text };
                if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
            }
        });
        browse.Width = 95;
        table.Controls.Add(browse, 2, row);
    }

    private WorkspaceOptions CurrentOptions() => new()
    {
        GameDirectory = paths["game"].Text.Trim(), WorkspaceDirectory = paths["workspace"].Text.Trim(),
        UnityProject = paths["project"].Text.Trim(), UnityEditor = paths["unity"].Text.Trim(), Blender = paths["blender"].Text.Trim(),
        ImportUnity = importUnity.Checked, CheckWeights = checkWeights.Checked, VerifyPlayback = verifyPlayback.Checked
    };

    private WorkspaceOptions LoadSettings()
    {
        try { if (File.Exists(WorkspaceStore.SettingsPath)) return JsonFiles.Read<WorkspaceOptions>(WorkspaceStore.SettingsPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { status.Text = "设置未能读取，暂用默认路径：" + error.Message; }
        return new WorkspaceOptions();
    }

    private void LoadSupportedCatalog()
    {
        catalog = new ProfileCatalogProvider().Load(Path.Combine(AppContext.BaseDirectory, "profiles", "chen.json"));
        RefreshCharacters();
        status.Text = "已加载陈千语配置中的模型、Avatar 和两条行走；尚未扫描游戏或执行提取。";
    }

    private void ImportCatalog()
    {
        using var dialog = new OpenFileDialog { Filter = "角色资源索引 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var loaded = new JsonCatalogProvider().Load(dialog.FileName);
        catalog = loaded with { SourceDescription = "导入索引：" + dialog.FileName };
        RefreshCharacters();
        status.Text = "已读取索引元数据；未核对当前游戏，也未解码或验证资源。";
    }

    private void ExportCatalog()
    {
        if (catalog == null) throw new InvalidDataException("请先加载角色配置或导入索引。");
        using var dialog = new SaveFileDialog { Filter = "角色资源索引 (*.json)|*.json", FileName = "character-catalog.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        JsonFiles.Write(dialog.FileName, catalog);
        status.Text = "资源索引已保存：" + dialog.FileName;
    }

    private void RefreshCharacters()
    {
        selectedCharacter = null;
        checkedResourceIds.Clear();
        resources.Items.Clear();
        characters.Items.Clear();
        resourceDetails.Clear();
        if (catalog == null) return;
        foreach (var character in CatalogService.Search(catalog, search.Text))
        {
            var item = new ListViewItem(character.DisplayName) { Tag = character };
            item.SubItems.Add(character.Id);
            characters.Items.Add(item);
        }
        catalogInfo.Text = $"版本 {catalog.GameBuild}\n当前 {characters.Items.Count} / {catalog.Characters.Count} 个角色 · 配置／名称候选";
        if (characters.Items.Count > 0) characters.Items[0].Selected = true;
        UpdatePlan();
    }

    private void SelectCharacter()
    {
        selectedCharacter = characters.SelectedItems.Count == 0 ? null : (CharacterEntry)characters.SelectedItems[0].Tag!;
        checkedResourceIds.Clear();
        if (selectedCharacter != null)
            foreach (var resource in selectedCharacter.Resources.Where(resource => resource.Association == AssociationKind.Configured))
                checkedResourceIds.Add(resource.Id);
        PopulateResources();
        UpdatePlan();
    }

    private void PopulateResources()
    {
        loadingResources = true;
        resources.Items.Clear();
        resourceDetails.Clear();
        resources.BeginUpdate();
        if (selectedCharacter != null)
            foreach (var resource in selectedCharacter.Resources.Where(resource =>
                resource.Name.Contains(resourceSearch.Text, StringComparison.OrdinalIgnoreCase)
                || resource.SourcePath.Contains(resourceSearch.Text, StringComparison.OrdinalIgnoreCase)))
                resources.Items.Add(new ResourceChoice(resource), checkedResourceIds.Contains(resource.Id));
        resources.EndUpdate();
        loadingResources = false;
        if (resources.Items.Count > 0) resources.SelectedIndex = 0;
    }

    private void ShowResource()
    {
        if (resources.SelectedItem is not ResourceChoice choice) { resourceDetails.Clear(); return; }
        var resource = choice.Entry;
        resourceDetails.Text = $"{resource.Name}\r\n类型：{KindText(resource.Kind)}    播放方式：{resource.Rig}\r\n"
            + $"配置路径：{resource.SourcePath}\r\n依赖 Bundle：{resource.BundleIds.Count} 个\r\n"
            + $"关联：{(resource.Association == AssociationKind.NameCandidate ? "名称候选，尚未确认对象和骨架" : "已支持的配置")}；未在本次工作台解码、适配或验证。\r\n"
            + $"依据：{resource.Evidence}\r\n来源：{catalog?.SourceDescription}\r\n{catalog?.CoverageNote}";
        resourceDetails.AppendText($"\r\n兼容性：{catalog?.CompatibilityNote}");
    }

    private List<string> SelectedIds() => checkedResourceIds.ToList();

    private void SetChecks(bool value)
    {
        for (int index = 0; index < resources.Items.Count; index++) resources.SetItemChecked(index, value);
        UpdatePlan();
    }

    private ExtractionPlan MakePlan()
    {
        if (catalog == null || selectedCharacter == null) throw new InvalidDataException("请先在“角色与资源”中选择角色。");
        return planner.Create(catalog, selectedCharacter, SelectedIds(), CurrentOptions());
    }

    private void UpdatePlan()
    {
        if (paths.Count < 5) return;
        UpdateContext();
        try
        {
            var plan = MakePlan();
            planDetails.Text = $"角色：{plan.CharacterName}\r\n新任务目标项目：{plan.Options.UnityProject}\r\n输出工作区：{plan.Options.WorkspaceDirectory}\r\n\r\n"
                + $"已选 {plan.RequestedResourceIds.Count} 项资源，工具将同时准备模型、骨架及已收录的依赖。\r\n\r\n"
                + string.Join("\r\n", plan.Resources.Take(40).Select(resource => $"  [{KindText(resource.Kind)}] {resource.Name}"))
                + (plan.Resources.Count > 40 ? "\r\n其余资源记录于任务计划。" : "")
                + "\r\n\r\n准备完成后，在第 3 步勾选需要的动作，再生成 Unity 预览。";
        }
        catch (InvalidDataException error) { planDetails.Text = error.Message; }
    }

    private void SavePlan()
    {
        var saved = store.Save(MakePlan());
        RefreshTasks();
        foreach (ListViewItem row in tasks.Items)
            if (((SavedPlan)row.Tag!).Plan.Id == saved.Plan.Id) row.Selected = true;
        status.Text = "计划已保存，尚未执行提取：" + saved.FilePath;
    }

    private void RefreshTasks()
    {
        var entries = store.List(CurrentOptions().WorkspaceDirectory);
        tasks.Items.Clear();
        taskDetails.Clear();
        foreach (var saved in entries)
        {
            var row = new ListViewItem(saved.Plan.CharacterName) { Tag = saved };
            row.SubItems.Add(saved.Plan.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            try
            {
                var bundle = BundleExtractionBackend.ReadResult(saved);
                var objects = ObjectWorkflow.ReadResult(saved);
                var prepared = ObjectWorkflow.ReadPreparation(saved);
                if (prepared?.BundleRunId == bundle?.RunId && prepared?.Status is ("unity_prepared" or "adapted_unverified")) objects = prepared;
                row.SubItems.Add(bundle?.Status == "bundles_extracted" && objects != null && objects.BundleRunId == bundle.RunId
                    ? ObjectStatus(objects.Status) : ResultStatus(bundle?.Status));
                var generation = UnityAdaptationRunner.ReadLatest(saved);
                if (generation != null && generation.SourceRunId == prepared?.RunId && prepared.BundleRunId == bundle?.RunId
                    && generation.Status != "adapted_unverified") row.SubItems[2].Text = UnityGenerationStatus(generation.Status);
                if (generation != null && !Path.GetFullPath(generation.Project).Equals(
                    Path.GetFullPath(UnityTaskTarget.Resolve(saved).Plan.Options.UnityProject), StringComparison.OrdinalIgnoreCase))
                    row.SubItems[2].Text = "目标项目已更换 · 待生成";
            }
            catch (Exception)
            {
                if (row.SubItems.Count > 2) row.SubItems[2].Text = "结果记录无法读取";
                else row.SubItems.Add("结果记录无法读取");
            }
            tasks.Items.Add(row);
        }
        status.Text = $"读取了 {entries.Count} 条任务计划。";
    }

    private void ShowTask()
    {
        if (editingTargetTask != null && pages.SelectedIndex == 2
            && (tasks.SelectedItems.Count == 0 || SelectedTask().Plan.Id != editingTargetTask.Plan.Id))
        {
            editingTargetTask = null; rootLayout.RowStyles[1].Height = 0;
        }
        UpdateContext();
        if (tasks.SelectedItems.Count == 0) { taskDetails.Clear(); return; }
        var saved = SelectedTask();
        try
        {
            var result = BundleExtractionBackend.ReadResult(saved);
            var objects = ObjectWorkflow.ReadResult(saved);
            var prepared = ObjectWorkflow.ReadPreparation(saved);
            var generation = UnityAdaptationRunner.ReadLatest(saved);
            taskDetails.Text = (result == null ? "尚无执行结果。\r\n" : DescribeResult(result))
                + DescribeObjectResult(objects)
                + (prepared != null && prepared.RunId != objects?.RunId ? "\r\n独立适配包记录：" + DescribeObjectResult(prepared) : "")
                + (generation == null ? "" : "\r\n最近 Unity 导入／生成（来源包 " + generation.SourceRunId + "）：\r\n"
                    + UnityGenerationStatus(generation.Status) + " · " + generation.Message + "\r\n项目：" + generation.Project
                    + "\r\n日志：" + generation.LogPath + "\r\n复用已有结果：" + generation.Reused + "；已验证播放：" + generation.PlaybackVerified + "\r\n")
                + "\r\n当前任务目标项目：" + UnityTaskTarget.Resolve(saved).Plan.Options.UnityProject
                + "\r\n计划文件：" + saved.FilePath + "\r\n\r\n" + Describe(saved.Plan);
        }
        catch (Exception error) { taskDetails.Text = "结果记录无法读取：" + error.Message; }
    }

    private static string UnityGenerationStatus(string value) => value switch
    {
        "running" => "Unity 处理中／中断待检查",
        "failed" => "Unity 导入／生成失败 · 适配包保留",
        "cancelled" => "Unity 导入／生成已取消",
        "adapted_unverified" => "Unity 已生成 · 未验证播放",
        _ => "Unity 操作状态未知"
    };

    private SavedPlan SelectedTask() => tasks.SelectedItems.Count == 0
        ? throw new InvalidDataException("请先选择一个任务。") : (SavedPlan)tasks.SelectedItems[0].Tag!;

    private async Task ExecutePlanAsync(bool resume)
    {
        if (scanCancellation != null || extractionCancellation != null) return;
        SavedPlan saved;
        try
        {
            saved = resume ? SelectedTask() : store.Save(MakePlan());
            RefreshTasks();
            foreach (ListViewItem row in tasks.Items)
                if (((SavedPlan)row.Tag!).Plan.Id == saved.Plan.Id) row.Selected = true;
        }
        catch (Exception error) { status.Text = "无法开始提取：" + error.Message; return; }
        using var cancellation = new CancellationTokenSource();
        extractionCancellation = cancellation;
        runningTask = saved;
        executeExtraction.Enabled = resumeExtraction.Enabled = false;
        SetObjectActionsEnabled(false);
        scan.Enabled = probe.Enabled = forceScan.Enabled = false;
        stopScan.Enabled = true;
        foreach (var action in catalogActions) action.Enabled = false;
        pages.TabPages[3].Enabled = false;
        scanProgress.Value = 0;
        scanLog.Clear();
        latestIndexReport = Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "result.json");
        taskDetails.Text = "正在核对并提取 Bundle；模型和动画导出尚未执行。";
        status.Text = "正在启动 Bundle 提取与校验…";
        foreach (ListViewItem row in tasks.Items)
            if (((SavedPlan)row.Tag!).Plan.Id == saved.Plan.Id) row.SubItems[2].Text = "准备提取";
        try
        {
            var progress = new Progress<ExtractionProgress>(row =>
            {
                if (IsDisposed || !ReferenceEquals(extractionCancellation, cancellation) || cancellation.IsCancellationRequested) return;
                status.Text = row.Message;
                scanProgress.Value = row.Percent;
                if (scanLog.TextLength > 150000) scanLog.Text = scanLog.Text[^75000..];
                scanLog.AppendText(DateTime.Now.ToString("HH:mm:ss ") + row.Message + Environment.NewLine);
            });
            var result = await extractor.ExecuteAsync(saved, progress, cancellation.Token);
            status.Text = "资源提取完成，正在解析角色和动作…";
            var objects = await objectWorkflow.ExecuteAsync(saved, "inspect", null, null, progress, cancellation.Token);
            LoadObjects(saved, objects.InventoryPath!);
            latestIndexReport = Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "objects", "result.json");
            status.Text = "角色资源已准备完成。点击顶部“3 选择动作与预览”继续。";
        }
        catch (OperationCanceledException) { status.Text = "提取已取消；选择该任务后可继续，完整文件会重新校验后复用。"; }
        catch (Exception error) { status.Text = "提取未完成：" + error.Message; }
        finally
        {
            scanLog.AppendText(status.Text + Environment.NewLine);
            extractionCancellation = null;
            runningTask = null;
            executeExtraction.Enabled = resumeExtraction.Enabled = true;
            SetObjectActionsEnabled(true);
            scan.Enabled = probe.Enabled = forceScan.Enabled = true;
            stopScan.Enabled = false;
            foreach (var action in catalogActions) action.Enabled = true;
            pages.TabPages[3].Enabled = true;
            var finalMessage = status.Text;
            try
            {
                RefreshTasks();
                foreach (ListViewItem row in tasks.Items)
                    if (((SavedPlan)row.Tag!).Plan.Id == saved.Plan.Id) row.Selected = true;
                ShowTask();
                status.Text = finalMessage;
            }
            catch (Exception error) { status.Text = finalMessage + "；读取任务记录失败：" + error.Message; }
            if (closeAfterScan) BeginInvoke(new Action(Close));
        }
    }

    private static string ResultStatus(string? state) => state switch
    {
        "bundles_extracted" => "Bundle 已提取 · 待解析",
        "running" => "处理中／中断待重试",
        "cancelled" => "已取消 · 可继续",
        "failed" => "未完成 · 可重试",
        null => "仅计划 · 未执行",
        _ => "未知结果状态"
    };

    private static string DescribeResult(BundleExtractionResult result) =>
        $"执行状态：{ResultStatus(result.Status)}\r\n{result.Message}\r\n"
        + $"文件：{result.Files.Count}/{result.TotalBundles}；新提取 {result.ExtractedFiles}，复用 {result.ReusedFiles}\r\n"
        + $"目录：{result.OutputDirectory}\r\n此记录仅表示 Bundle 提取；对象解析／导出状态见下方对象结果。权重、Unity 及播放检查未执行。\r\n"
        + string.Join("\r\n", result.Files.Take(200).Select(file => $"  [{(file.Cached ? "已校验复用" : "已提取校验")}] {file.Path}"))
        + (result.Files.Count > 200 ? "\r\n其余文件见完整结果 JSON。\r\n" : "\r\n");

    private static string Describe(ExtractionPlan plan) =>
        $"角色：{plan.CharacterName} ({plan.CharacterId})\r\n游戏版本：{plan.GameBuild}\r\n"
        + $"兼容性：{plan.CompatibilityNote}\r\n"
        + $"计划配置（实际执行状态见结果记录）\r\n输出工作区：{plan.Options.WorkspaceDirectory}\r\n"
        + $"选中 {plan.RequestedResourceIds.Count} 项；包含已收录模型／Avatar 后 {plan.Resources.Count} 项；Bundle 依赖展开后 {plan.Bundles.Count} 个。\r\n\r\n资源：\r\n"
        + string.Join("\r\n", plan.Resources.Select(resource => $"  [{KindText(resource.Kind)}] {resource.Name}"))
        + "\r\n\r\n执行阶段：\r\n" + string.Join("\r\n", plan.Stages.Select(stage => $"  {stage.Name}：{StateText(stage.State)}"))
        + "\r\n\r\nBundle 完成后解析对象，选择导出 FBX／原始动画；动画适配页可生成独立包并回读 Unity 原生结果。实际播放验证尚未自动接入。";

    private void RunAction(Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            status.Text = "未完成：" + error.Message;
            scanLog.AppendText(status.Text + Environment.NewLine);
        }
    }

    private static void OpenLocal(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("计划文件不存在。", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private TabPage Page(string title)
    {
        var page = new TabPage(title) { Padding = new Padding(16), BackColor = Color.White };
        pages.TabPages.Add(page);
        return page;
    }

    private static string KindText(ResourceKind kind) => kind switch
    { ResourceKind.Model => "模型", ResourceKind.Avatar => "骨架", ResourceKind.Animation => "动作", _ => "未知" };
    private static string CompatibilityText(string status) => status switch
    {
        "KnownReference" => "匹配历史参考版本，索引格式可读",
        "FormatCompatibleUnverified" => "索引格式可读，新版本尚未验证",
        "Unsupported" => "暂无适用格式适配器",
        "Unreadable" => "数据或环境无法读取",
        _ => "尚未检查当前安装版本"
    };
    private static string StateText(StageState state) => state switch
    { StageState.Pending => "待执行", StageState.NotRequested => "未请求", StageState.Completed => "已完成", StageState.Failed => "失败", _ => "不支持" };
    private static Label Note(string text) => new() { Text = text, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0), AutoEllipsis = true };
    private static TextBox ReadOnlyText() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(247, 249, 251), BorderStyle = BorderStyle.FixedSingle };
    private static TableLayoutPanel Grid(int columns, int rows)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = Padding.Empty };
        // Implicit AutoSize cells can grow beyond the parent at higher DPI.
        // Single-axis containers fill their allocated area; inner workflows
        // provide the scrolling needed for content larger than that area.
        if (columns == 1) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (rows == 1) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return grid;
    }
    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true, Margin = Padding.Empty };
    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Text = text, Width = 163, Height = 35, FlatStyle = FlatStyle.Flat,
            BackColor = Color.White, ForeColor = Accent, Margin = new Padding(0, 3, 10, 3), Cursor = Cursors.Hand };
        button.Click += (_, _) => action();
        return button;
    }
    private static ListView List(params string[] columns)
    {
        var view = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            MultiSelect = false, HideSelection = false };
        foreach (var column in columns) view.Columns.Add(column, column == "建立时间" ? 210 : 145);
        return view;
    }
    private sealed record ResourceChoice(ResourceEntry Entry)
    {
        public override string ToString() => $"[{KindText(Entry.Kind)}] {Entry.Name} · {(Entry.Association == AssociationKind.NameCandidate ? "名称候选" : "已收录")}";
    }
}
