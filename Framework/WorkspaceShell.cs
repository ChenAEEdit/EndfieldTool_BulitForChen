using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

namespace EndfieldChenTool.Framework;

internal sealed partial class WorkspaceForm
{
    private readonly Label taskContext = Note("");
    private readonly Label projectContext = Note("");
    private readonly FlowLayoutPanel workflowSteps = Flow();
    private readonly Dictionary<int, Button> sectionButtons = [];
    private readonly Button changeTarget = new() { Text = "更换当前任务项目", AutoSize = true, Height = 32 };
    private readonly TextBox targetProject = new() { Dock = DockStyle.Fill };
    private readonly TextBox targetEditor = new() { Dock = DockStyle.Fill };
    private TableLayoutPanel rootLayout = null!;
    private SavedPlan? editingTargetTask;
    private SavedPlan? runningTask;
    private int lastWorkflowPage;

    private SavedPlan? ContextTask => runningTask ?? (pages.SelectedIndex is 0 or 1 ? null
        : pages.SelectedIndex == 2 ? tasks.SelectedItems.Count == 0 ? null : SelectedTask() : inventoryTask);

    private Control BuildContextHeader()
    {
        var header = Grid(2, 3);
        header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.ColumnStyles.Add(new(SizeType.Absolute, 180));
        header.RowStyles.Add(new(SizeType.Absolute, 39));
        header.RowStyles.Add(new(SizeType.Absolute, 30));
        header.RowStyles.Add(new(SizeType.Absolute, 36));
        var title = new Label { Text = "终末地角色工作台", Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 21, FontStyle.Bold) };
        header.Controls.Add(title, 0, 0); header.SetColumnSpan(title, 2);
        header.Controls.Add(taskContext, 0, 1); header.SetColumnSpan(taskContext, 2);
        header.Controls.Add(projectContext, 0, 2);
        header.Controls.Add(changeTarget, 1, 2);
        changeTarget.Click += (_, _) => RunAction(() =>
        {
            editingTargetTask = ContextTask ?? throw new InvalidDataException("请先在任务与结果中选择任务，或载入角色与动作。");
            SavedPlan effective;
            try { effective = UnityTaskTarget.Resolve(editingTargetTask); }
            catch (Exception error) { effective = editingTargetTask; status.Text = "目标记录暂不可读，可在此重新保存：" + error.Message; }
            targetProject.Text = effective.Plan.Options.UnityProject;
            targetEditor.Text = effective.Plan.Options.UnityEditor;
            rootLayout.RowStyles[1].Height = 160;
        });
        return header;
    }

    private Control BuildTargetEditor()
    {
        var panel = Grid(3, 3);
        panel.Padding = new Padding(10); panel.BackColor = Color.FromArgb(232, 242, 245);
        panel.ColumnStyles.Add(new(SizeType.Absolute, 120));
        panel.ColumnStyles.Add(new(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new(SizeType.Absolute, 100));
        panel.RowStyles.Add(new(SizeType.Absolute, 38)); panel.RowStyles.Add(new(SizeType.Absolute, 38));
        panel.RowStyles.Add(new(SizeType.Percent, 100));
        panel.Controls.Add(Note("当前任务项目"), 0, 0); panel.Controls.Add(targetProject, 1, 0);
        panel.Controls.Add(Note("Unity 编辑器"), 0, 1); panel.Controls.Add(targetEditor, 1, 1);
        var folder = ActionButton("浏览…", () =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择已有的 Unity 项目", SelectedPath = targetProject.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) targetProject.Text = dialog.SelectedPath;
        }); folder.Width = 90; panel.Controls.Add(folder, 2, 0);
        var editor = ActionButton("浏览…", () =>
        {
            using var dialog = new OpenFileDialog { Filter = "Unity 编辑器|Unity.exe", FileName = targetEditor.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) targetEditor.Text = dialog.FileName;
        }); editor.Width = 90; panel.Controls.Add(editor, 2, 1);
        var actions = Flow();
        actions.Controls.Add(ActionButton("保存到当前任务", () => RunAction(() =>
        {
            if (scanCancellation != null || extractionCancellation != null) throw new InvalidDataException("请完成或取消任务后再更换项目。");
            var task = editingTargetTask ?? throw new InvalidDataException("请先选择要更换项目的任务。");
            UnityTaskTarget.Save(task, targetProject.Text.Trim(), targetEditor.Text.Trim());
            rootLayout.RowStyles[1].Height = 0; editingTargetTask = null;
            RefreshTasks();
            foreach (ListViewItem row in tasks.Items)
                if (((SavedPlan)row.Tag!).Plan.Id == task.Plan.Id) row.Selected = true;
            if (inventoryTask?.Plan.Id == task.Plan.Id)
                adaptationDetails.Text = "当前任务目标项目已更换。请在新项目生成或核对预览；旧项目中的文件仍保留。";
            SetObjectActionsEnabled(true); UpdateAdaptationHint(); ShowTask();
            status.Text = "已更换当前任务的目标项目。提取文件保留；请在新项目生成并核对预览。";
        })));
        actions.Controls.Add(ActionButton("填入默认项目", () =>
        {
            targetProject.Text = paths["project"].Text.Trim(); targetEditor.Text = paths["unity"].Text.Trim();
        }));
        actions.Controls.Add(ActionButton("取消更换", () => { rootLayout.RowStyles[1].Height = 0; editingTargetTask = null; }));
        panel.Controls.Add(actions, 0, 2); panel.SetColumnSpan(actions, 3);
        return panel;
    }

    private Control BuildNavigation()
    {
        // Keep the workflow views in one window. Only explicit navigation changes pages.
        pages.Appearance = TabAppearance.FlatButtons; pages.ItemSize = new Size(0, 1); pages.SizeMode = TabSizeMode.Fixed;
        var body = Grid(2, 1);
        body.ColumnStyles.Add(new(SizeType.Absolute, 155)); body.ColumnStyles.Add(new(SizeType.Percent, 100));
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(0, 8, 8, 0) };
        foreach (var entry in new[] { (0, "角色与动作"), (2, "任务与结果"), (3, "设置"), (4, "帮助与日志") })
        {
            var button = ActionButton(entry.Item2, () => NavigateTo(entry.Item1 == 0 ? lastWorkflowPage : entry.Item1));
            button.Width = 142; button.Height = 46; button.Margin = new Padding(0, 0, 0, 10);
            sectionButtons.Add(entry.Item1, button); navigation.Controls.Add(button);
        }
        var content = Grid(1, 2); content.RowStyles.Add(new(SizeType.Absolute, 45)); content.RowStyles.Add(new(SizeType.Percent, 100));
        foreach (var entry in new[] { (0, "1 选择角色与资源"), (1, "2 准备角色资源"), (5, "3 选择动作与预览") })
            workflowSteps.Controls.Add(ActionButton(entry.Item2, () => NavigateTo(entry.Item1)));
        content.Controls.Add(workflowSteps, 0, 0); content.Controls.Add(pages, 0, 1);
        body.Controls.Add(navigation, 0, 0); body.Controls.Add(content, 1, 0);
        pages.SelectedIndexChanged += (_, _) =>
        {
            editingTargetTask = null; rootLayout.RowStyles[1].Height = 0;
            workflowSteps.Visible = pages.SelectedIndex is 0 or 1 or 5;
            if (workflowSteps.Visible) lastWorkflowPage = pages.SelectedIndex;
            content.RowStyles[0].Height = workflowSteps.Visible ? 45 : 0;
            int section = pages.SelectedIndex is 0 or 1 or 5 ? 0 : pages.SelectedIndex;
            foreach (var entry in sectionButtons)
            {
                entry.Value.BackColor = entry.Key == section ? Accent : Color.White;
                entry.Value.ForeColor = entry.Key == section ? Color.White : Accent;
            }
            UpdateContext();
        };
        return body;
    }

    private void NavigateTo(int page)
    {
        pages.SelectedIndex = page;
        UpdateContext();
    }

    private void UpdateContext()
    {
        if (paths.Count < 5) return;
        var task = ContextTask;
        try
        {
            var effective = task == null ? null : UnityTaskTarget.Resolve(task);
            taskContext.Text = task == null ? "新任务 · " + (selectedCharacter?.DisplayName ?? "请选择角色")
                : $"当前任务 · {task.Plan.CharacterName} · {task.Plan.Id.ToString("N")[..8]}"
                    + (inventoryTask?.Plan.Id == task.Plan.Id ? $" · 已选 {checkedClips.Count} 条动作" : "");
            projectContext.Text = (task == null ? "新任务默认项目：" : "当前任务目标项目：")
                + (effective?.Plan.Options.UnityProject ?? paths["project"].Text.Trim());
            changeTarget.Enabled = task != null && scanCancellation == null && extractionCancellation == null;
        }
        catch (Exception error) { projectContext.Text = "目标项目读取失败：" + error.Message; changeTarget.Enabled = task != null; }
    }

    private Control Collapsible(string title, Control content, int height, bool limitHeight = true)
    {
        var container = Grid(1, 2);
        container.AutoSize = true; container.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        container.RowStyles.Add(new(SizeType.Absolute, 37)); container.RowStyles.Add(new(SizeType.Absolute, 0));
        container.Height = 37; container.Dock = DockStyle.Top;
        var toggle = new CheckBox { Text = title, Dock = DockStyle.Fill, Appearance = Appearance.Button,
            TextAlign = ContentAlignment.MiddleLeft, FlatStyle = FlatStyle.Flat };
        content.Visible = false;
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = limitHeight };
        content.Dock = DockStyle.Top; content.Height = height; content.MinimumSize = new Size(0, height);
        viewport.Controls.Add(content);
        toggle.CheckedChanged += (_, _) =>
        {
            content.Visible = toggle.Checked;
            int contentHeight = content.MinimumSize.Height;
            int expanded = limitHeight ? Math.Min(contentHeight, (int)Math.Round(255 * DeviceDpi / 96d)) : contentHeight;
            container.RowStyles[1].Height = toggle.Checked ? expanded : 0;
            container.Height = (int)Math.Ceiling(container.RowStyles[0].Height) + (toggle.Checked ? expanded : 0);
        };
        container.Controls.Add(toggle, 0, 0); container.Controls.Add(viewport, 0, 1);
        return container;
    }

    private void ShowInlineLog(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("尚无此日志或说明文件。", path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 200000) stream.Seek(-200000, SeekOrigin.End);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        scanLog.Text = path + "\r\n（较长日志仅显示末尾内容）\r\n\r\n" + reader.ReadToEnd();
        NavigateTo(4);
    }

    private Control ScrollableWorkflow(TableLayoutPanel layout, Control advanced)
    {
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        layout.Dock = DockStyle.Top;
        void ResizeContent()
        {
            int minimum = (int)Math.Ceiling(layout.RowStyles.Cast<RowStyle>()
                .Where(row => row.SizeType == SizeType.Absolute).Sum(row => row.Height))
                + advanced.Height + Font.Height * 7;
            layout.Height = Math.Max(viewport.ClientSize.Height, minimum);
            viewport.AutoScrollMinSize = new Size(0, minimum);
        }
        viewport.Controls.Add(layout);
        viewport.SizeChanged += (_, _) => ResizeContent();
        advanced.SizeChanged += (_, _) => ResizeContent();
        ResizeContent();
        return viewport;
    }
}
