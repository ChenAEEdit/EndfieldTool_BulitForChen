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
    private readonly TextBox adaptationDetails = ReadOnlyText();

    private void BuildAnimationPage()
    {
        var page = Page("动画适配");
        var layout = Grid(1, 5);
        foreach (var height in new[] { 69, 276, 85, 85 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(Note("先在对象与导出中选择目标模型、Avatar 和动画。生成独立 Unity 适配包后，在现有项目内执行原生生成菜单；读取结果后才标记已适配。共享及附加动作应分别设置，播放效果仍需检查。"), 0, 0);
        var settings = Grid(2, 5);
        settings.ColumnStyles.Add(new(SizeType.Absolute, 225)); settings.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var height in new[] { 37, 37, 38, 80, 84 }) settings.RowStyles.Add(new(SizeType.Absolute, height));
        settings.Controls.Add(Note("模型导入倍率"), 0, 0); settings.Controls.Add(modelImportScale, 1, 0);
        settings.Controls.Add(Note("动画位置倍率"), 0, 1); settings.Controls.Add(animationPositionScale, 1, 1);
        settings.Controls.Add(Note("首帧位移归零的节点"), 0, 2); settings.Controls.Add(normalizeRoot, 1, 2);
        settings.Controls.Add(Note("排除后代（每行一个父路径）"), 0, 3); settings.Controls.Add(excludedDescendants, 1, 3);
        settings.Controls.Add(Note("排除节点（每行一个路径）"), 0, 4); settings.Controls.Add(excludedNodes, 1, 4);
        layout.Controls.Add(settings, 0, 1);
        var flags = Flow(); flags.Controls.AddRange([dropUnmatched, dropSceneTranslation, forceLoop]); layout.Controls.Add(flags, 0, 2);
        var actions = Flow();
        var prepare = new Button { Text = "生成 Unity 适配包", Width = 185, Height = 35 };
        prepare.Click += async (_, _) => await RunWorkflowAsync("prepare");
        var read = new Button { Text = "读取 Unity 适配结果", Width = 190, Height = 35 };
        read.Click += async (_, _) => await ReadAdaptationResultAsync();
        actions.Controls.Add(ActionButton("返回对象选择", () => pages.SelectedIndex = 5)); actions.Controls.Add(prepare); actions.Controls.Add(read);
        actions.Controls.Add(ActionButton("打开适配包", () => RunAction(() =>
        {
            string? path = inventoryTask == null ? null : ObjectWorkflow.ReadPreparation(inventoryTask)?.UnityPackageDirectory;
            if (path == null || !Directory.Exists(path)) throw new InvalidDataException("请先生成适配包。");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        })));
        actions.Controls.Add(ActionButton("查看使用说明", () => RunAction(() => OpenLocal(Path.Combine(AppContext.BaseDirectory, "docs", "动画适配说明.md")))));
        foreach (Control action in actions.Controls) objectActions.Add(action);
        layout.Controls.Add(actions, 0, 3); layout.Controls.Add(adaptationDetails, 0, 4); page.Controls.Add(layout);
        adaptationDetails.Text = "默认模型与动画倍率均为 1；只有当前已收录且版本、模型标识匹配的陈千语配置预填模型倍率 100。\r\n其他角色请明确确认单位。当前只生成 Generic 直接骨骼动画，不进行 Humanoid 重定向、IK 或游戏修正骨骼求解。\r\n生成包位于任务的 exports/unity-package，源文件单独保留；读取 Unity 结果不会启动 Unity。";
    }

    private void UpdateAdaptationDefaults()
    {
        var model = (modelChoice.SelectedItem as ObjectChoice)?.Row;
        modelImportScale.Value = inventoryTask?.Plan.CharacterId == "chen" && model?.Name == "chr_0005_chen_uimodel"
            && model.PathId == "8239783119078153412"
            && inventoryTask.Plan.GameAssemblySha256 == "C24495E51B406F03B03890C4788EE618AE022C991405BE5D5B8B787CB775AE89" ? 100 : 1;
    }

    private AnimationPreparationOptions AdaptationOptions() => new()
    {
        ModelImportScale = modelImportScale.Value, PositionScale = animationPositionScale.Value,
        DropUnmatched = dropUnmatched.Checked, DropSceneTranslation = dropSceneTranslation.Checked, ForceLoop = forceLoop.Checked,
        NormalizeRoot = normalizeRoot.Text.Trim(), ExcludedDescendants = PathsFrom(excludedDescendants), ExcludedNodes = PathsFrom(excludedNodes)
    };

    private async Task ReadAdaptationResultAsync()
    {
        if (scanCancellation != null || extractionCancellation != null) return;
        if (inventoryTask == null) { status.Text = "请先载入任务。"; return; }
        using var cancellation = new CancellationTokenSource(); extractionCancellation = cancellation;
        SetObjectActionsEnabled(false); executeExtraction.Enabled = resumeExtraction.Enabled = scan.Enabled = probe.Enabled = false;
        stopScan.Enabled = true;
        try
        {
            var result = await UnityAdaptationResults.ImportAsync(inventoryTask, cancellation.Token);
            adaptationDetails.Text = DescribeObjectResult(result); status.Text = result.Message;
            foreach (ListViewItem row in tasks.Items)
                if (((SavedPlan)row.Tag!).Plan.Id == inventoryTask.Plan.Id) row.SubItems[2].Text = ObjectStatus(result.Status);
            ShowTask();
        }
        catch (OperationCanceledException) { status.Text = "已取消读取适配结果。"; }
        catch (Exception error) { status.Text = "适配结果未确认：" + error.Message; adaptationDetails.Text = error.Message; }
        finally
        {
            extractionCancellation = null; SetObjectActionsEnabled(true);
            executeExtraction.Enabled = resumeExtraction.Enabled = scan.Enabled = probe.Enabled = true; stopScan.Enabled = false;
            if (closeAfterScan) BeginInvoke(new Action(Close));
        }
    }

    private static List<string> PathsFrom(TextBox box) => box.Lines.Select(line => line.Trim().Replace('\\', '/')).Where(line => line.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    private static NumericUpDown ScaleInput() => new() { Minimum = .0001m, Maximum = 10000, DecimalPlaces = 4, Value = 1, Dock = DockStyle.Fill };
}
