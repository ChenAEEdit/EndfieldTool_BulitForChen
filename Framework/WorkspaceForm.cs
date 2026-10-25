using System.Diagnostics;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

namespace EndfieldChenTool.Framework;

internal sealed class WorkspaceForm : Form
{
    private static readonly Color Ink = Color.FromArgb(32, 45, 62);
    private static readonly Color Accent = Color.FromArgb(33, 100, 116);
    private readonly TabControl pages = new() { Dock = DockStyle.Fill };
    private readonly TextBox search = new() { Dock = DockStyle.Fill, PlaceholderText = "搜索角色名称、代码或资源路径" };
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
    private ResourceCatalog? catalog;
    private CharacterEntry? selectedCharacter;

    public WorkspaceForm()
    {
        Text = "终末地角色工作台 · 框架预览 0.2.0";
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.FromArgb(244, 247, 249);
        ForeColor = Ink;
        ClientSize = new Size(1160, 850);
        MinimumSize = new Size(1050, 800);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = Grid(1, 3);
        layout.Padding = new Padding(18);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label { Text = "终末地角色工作台", AutoSize = true,
            Font = new Font(Font.FontFamily, 23, FontStyle.Bold), Location = new Point(0, 0) });
        header.Controls.Add(new Label { Text = "搜索资源 → 选择内容 → 制定提取计划 → 查看任务", AutoSize = true,
            ForeColor = Color.DimGray, Location = new Point(3, 48) });
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(pages, 0, 1);
        layout.Controls.Add(status, 0, 2);
        Controls.Add(layout);

        var settings = LoadSettings();
        BuildCatalogPage();
        BuildPlanPage();
        BuildTasksPage();
        BuildSettingsPage(settings);
        search.TextChanged += (_, _) => RefreshCharacters();
        characters.SelectedIndexChanged += (_, _) => SelectCharacter();
        resources.SelectedIndexChanged += (_, _) => ShowResource();
        resources.ItemCheck += (_, _) =>
        {
            if (IsHandleCreated) BeginInvoke(new Action(UpdatePlan));
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
        RunAction(LoadSupportedCatalog);
    }

    private void BuildCatalogPage()
    {
        var page = Page("角色与资源");
        var layout = Grid(1, 4);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 51));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Note("框架阶段：当前只读取已支持的角色配置或导入的索引。游戏全量扫描与通用提取尚未接入。"), 0, 0);
        var tools = Flow();
        tools.Controls.Add(ActionButton("加载已支持角色", () => RunAction(LoadSupportedCatalog)));
        tools.Controls.Add(ActionButton("导入资源索引…", () => RunAction(ImportCatalog)));
        tools.Controls.Add(ActionButton("另存资源索引…", () => RunAction(ExportCatalog)));
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
        var right = Grid(1, 3);
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 161));
        var choices = Flow();
        choices.Controls.Add(ActionButton("全选资源", () => SetChecks(true)));
        choices.Controls.Add(ActionButton("取消勾选", () => SetChecks(false)));
        choices.Controls.Add(ActionButton("查看提取计划 →", () => { UpdatePlan(); pages.SelectedIndex = 1; }));
        right.Controls.Add(choices, 0, 0);
        right.Controls.Add(resources, 0, 1);
        right.Controls.Add(resourceDetails, 0, 2);
        body.Controls.Add(right, 1, 0);
        layout.Controls.Add(body, 0, 3);
        page.Controls.Add(layout);
    }

    private void BuildPlanPage()
    {
        var page = Page("提取计划");
        var layout = Grid(1, 3);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        layout.Controls.Add(Note("选择资源后生成独立的任务计划。保存计划只记录选择和依赖，不执行解包、不启动检查，也不写入 Unity 项目。"), 0, 0);
        layout.Controls.Add(planDetails, 0, 1);
        var actions = Flow();
        actions.Controls.Add(ActionButton("保存提取计划", () => RunAction(SavePlan)));
        actions.Controls.Add(new Button { Text = "执行提取（待接入）", Enabled = false, Width = 180, Height = 35 });
        actions.Controls.Add(ActionButton("调整环境与选项 →", () => pages.SelectedIndex = 3));
        layout.Controls.Add(actions, 0, 2);
        page.Controls.Add(layout);
    }

    private void BuildTasksPage()
    {
        var page = Page("任务记录");
        var layout = Grid(1, 4);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 51));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.Controls.Add(Note("这里显示当前工作区保存的计划。框架尚无执行结果，计划不能作为提取或验证成功的证据。"), 0, 0);
        var actions = Flow();
        actions.Controls.Add(ActionButton("读取工作区任务", () => RunAction(RefreshTasks)));
        actions.Controls.Add(ActionButton("打开所选计划文件", () => RunAction(() =>
        {
            if (tasks.SelectedItems.Count == 0) throw new InvalidDataException("请先选择一个任务。");
            OpenLocal(((SavedPlan)tasks.SelectedItems[0].Tag!).FilePath);
        })));
        layout.Controls.Add(actions, 0, 1);
        layout.Controls.Add(tasks, 0, 2);
        layout.Controls.Add(taskDetails, 0, 3);
        page.Controls.Add(layout);
    }

    private void BuildSettingsPage(WorkspaceOptions options)
    {
        var page = Page("环境与已有工具");
        var layout = Grid(1, 5);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 255));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Note("填写计划使用的环境。路径填写不代表已通过环境检查；当前游戏版本将在后续执行阶段核对。"), 0, 0);
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
        importUnity.Checked = options.ImportUnity;
        checkWeights.Checked = options.CheckWeights;
        verifyPlayback.Checked = options.ImportUnity && options.VerifyPlayback;
        flags.Controls.AddRange([importUnity, checkWeights, verifyPlayback]);
        foreach (Control control in flags.Controls) control.Margin = new Padding(4, 10, 25, 0);
        layout.Controls.Add(flags, 0, 2);
        var actions = Flow();
        actions.Controls.Add(ActionButton("保存环境设置", () => RunAction(() =>
        {
            JsonFiles.Write(WorkspaceStore.SettingsPath, CurrentOptions());
            status.Text = "环境设置已保存；尚未运行环境检查。";
        })));
        var legacy = ActionButton("打开陈千语提取窗口", () =>
        {
            using var form = new MainForm();
            form.ShowDialog(this);
        });
        legacy.Width = 220;
        legacy.Enabled = File.Exists(Path.Combine(AppContext.BaseDirectory, "pipeline.py"));
        actions.Controls.Add(legacy);
        layout.Controls.Add(actions, 0, 3);
        layout.Controls.Add(Note("陈千语提取窗口沿用原有独立设置与两条行走流程，不会接收工作台的任务计划。\n\n六条专属待机仍从现有 Unity 项目的 Assets/ChenIdle/Chen_Idle.unity 或 Endfield Chen Tool 菜单打开。\n\n工作台设置与原工具分开保存；生成任务时选择空目录或本工作台已创建的目录。"), 0, 4);
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
        catalogInfo.Text = $"版本 {catalog.GameBuild}\n当前 {characters.Items.Count} / {catalog.Characters.Count} 个角色 · 配置／索引数据";
        if (characters.Items.Count > 0) characters.Items[0].Selected = true;
        UpdatePlan();
    }

    private void SelectCharacter()
    {
        selectedCharacter = characters.SelectedItems.Count == 0 ? null : (CharacterEntry)characters.SelectedItems[0].Tag!;
        resources.Items.Clear();
        resourceDetails.Clear();
        if (selectedCharacter != null)
            foreach (var resource in selectedCharacter.Resources) resources.Items.Add(new ResourceChoice(resource), true);
        if (resources.Items.Count > 0) resources.SelectedIndex = 0;
        UpdatePlan();
    }

    private void ShowResource()
    {
        if (resources.SelectedItem is not ResourceChoice choice) { resourceDetails.Clear(); return; }
        var resource = choice.Entry;
        resourceDetails.Text = $"{resource.Name}\r\n类型：{KindText(resource.Kind)}    播放方式：{resource.Rig}\r\n"
            + $"配置路径：{resource.SourcePath}\r\n依赖 Bundle：{resource.BundleIds.Count} 个\r\n"
            + "状态：已收录；未在本次工作台执行解码、适配或验证。\r\n"
            + $"来源：{catalog?.SourceDescription}";
    }

    private List<string> SelectedIds() => resources.CheckedItems.Cast<ResourceChoice>().Select(item => item.Entry.Id).ToList();

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
        try { planDetails.Text = Describe(MakePlan()); }
        catch (InvalidDataException error) { planDetails.Text = error.Message; }
    }

    private void SavePlan()
    {
        var saved = store.Save(MakePlan());
        RefreshTasks();
        pages.SelectedIndex = 2;
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
            row.SubItems.Add(saved.Plan.Status == "planned" ? "仅计划 · 未执行" : "外部记录 · 未核对");
            tasks.Items.Add(row);
        }
        status.Text = $"读取了 {entries.Count} 条任务计划。";
    }

    private void ShowTask()
    {
        taskDetails.Text = tasks.SelectedItems.Count == 0 ? "" :
            Describe(((SavedPlan)tasks.SelectedItems[0].Tag!).Plan) + "\r\n计划文件：" + ((SavedPlan)tasks.SelectedItems[0].Tag!).FilePath;
    }

    private static string Describe(ExtractionPlan plan) =>
        $"角色：{plan.CharacterName} ({plan.CharacterId})\r\n游戏版本：{plan.GameBuild}\r\n"
        + $"状态：仅计划，未执行\r\n输出工作区：{plan.Options.WorkspaceDirectory}\r\n"
        + $"选中 {plan.RequestedResourceIds.Count} 项；包含模型／Avatar 依赖后 {plan.Resources.Count} 项；Bundle 去重后 {plan.Bundles.Count} 个。\r\n\r\n资源：\r\n"
        + string.Join("\r\n", plan.Resources.Select(resource => $"  [{KindText(resource.Kind)}] {resource.Name}"))
        + "\r\n\r\n执行阶段：\r\n" + string.Join("\r\n", plan.Stages.Select(stage => $"  {stage.Name}：{StateText(stage.State)}"))
        + "\r\n\r\n通用执行器待接入；保存后不会启动游戏解析器、Blender 或 Unity。";

    private void RunAction(Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            status.Text = "未完成：" + error.Message;
            MessageBox.Show(this, error.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
    private static string StateText(StageState state) => state switch
    { StageState.Pending => "待执行", StageState.NotRequested => "未请求", StageState.Completed => "已完成", StageState.Failed => "失败", _ => "不支持" };
    private static Label Note(string text) => new() { Text = text, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0), AutoEllipsis = true };
    private static TextBox ReadOnlyText() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(247, 249, 251), BorderStyle = BorderStyle.FixedSingle };
    private static TableLayoutPanel Grid(int columns, int rows) => new() { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = Padding.Empty };
    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
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
        public override string ToString() => $"[{KindText(Entry.Kind)}] {Entry.Name} · 已收录";
    }
}
