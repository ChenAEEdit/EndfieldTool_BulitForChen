using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EndfieldChenTool;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var form = new MainForm();
        if (args.Length == 2 && args[0] == "--smoke-test")
        {
            var python = MainForm.FindPython(form.BlenderPath);
            File.WriteAllText(args[1], JsonSerializer.Serialize(new
            {
                passed = File.Exists(Path.Combine(AppContext.BaseDirectory, "pipeline.py")) && File.Exists(python),
                python, controls = form.Controls.Count, title = form.Text
            }, new JsonSerializerOptions { WriteIndented = true }));
            return File.Exists(python) ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--capture")
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.ShowInTaskbar = false;
            form.Shown += (_, _) => form.BeginInvoke(() =>
            {
                using var image = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                form.Close();
            });
        }
        if (args.Length == 3 && args[0] == "--verify-run")
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.ShowInTaskbar = false;
            form.Shown += async (_, _) =>
            {
                bool passed = await form.VerifyRunAsync(args[1]);
                File.WriteAllText(args[2], JsonSerializer.Serialize(new { passed, status = form.StatusText }, new JsonSerializerOptions { WriteIndented = true }));
                using var image = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(Path.ChangeExtension(args[2], ".png"), System.Drawing.Imaging.ImageFormat.Png);
                form.Close();
            };
        }
        Application.Run(form);
        return 0;
    }
}

internal sealed class UserSettings
{
    public string Game { get; set; } = @"F:\AAA_ZMD\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data";
    public string Workspace { get; set; } = @"D:\AAAChatGPT\EndfieldChenWorkspace";
    public string Project { get; set; } = @"D:\Unity 3D\Projects\Codex_ZMD";
    public string Unity { get; set; } = @"D:\Unity 3D\Editor\6000.0.84f1\Editor\Unity.exe";
    public string Blender { get; set; } = @"D:\Blender\5.0.1\blender.exe";
    public bool ImportUnity { get; set; } = true;
    public bool VerifyPlay { get; set; } = true;
    public bool Weights { get; set; } = true;
    public bool UiWalk { get; set; } = true;
    public bool AclWalk { get; set; } = true;
}

internal sealed class MainForm : Form
{
    private static readonly Color Blue = Color.FromArgb(35, 86, 178);
    private static readonly Color Ink = Color.FromArgb(34, 48, 67);
    private readonly TextBox game = new(), workspace = new(), project = new(), unity = new(), blender = new();
    private readonly CheckBox import = new() { Text = "导入 Unity 并生成预览场景", AutoSize = true };
    private readonly CheckBox verify = new() { Text = "检查实际 Play 模式", AutoSize = true };
    private readonly CheckBox weights = new() { Text = "检查骨骼权重（Blender）", AutoSize = true };
    private readonly CheckBox force = new() { Text = "重新导出，忽略已有缓存", AutoSize = true };
    private readonly CheckBox uiWalk = new() { Text = "UI 行走 · female_walk", AutoSize = true };
    private readonly CheckBox aclWalk = new() { Text = "ACL 行走 · customized walk", AutoSize = true };
    private readonly Button check = MakeButton("检查环境"), start = MakeButton("开始提取与导入"), cancel = MakeButton("取消");
    private readonly Button output = MakeButton("打开输出目录"), report = MakeButton("查看结果说明"), openUnity = MakeButton("打开 Unity 预览");
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Height = 8, Style = ProgressBarStyle.Continuous };
    private readonly Label status = new() { Text = "准备就绪 · 首次运行会提取资源；再次运行会校验并复用已有结果。", AutoSize = true };
    private readonly Label elapsed = new() { Text = "", AutoSize = true, ForeColor = Color.DimGray };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(246, 248, 251), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Microsoft YaHei UI", 9) };
    private readonly List<Control> inputs = [];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly Stopwatch watch = new();
    private Process? active;
    private bool cancelling, completionReceived;
    private string? latestError;
    private readonly string settingsPath = Environment.GetEnvironmentVariable("ENDFIELD_CHEN_SETTINGS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndfieldChenTool", "settings.json");
    public string BlenderPath => blender.Text.Trim();
    public string StatusText => status.Text;

    public async Task<bool> VerifyRunAsync(string configuration)
    {
        var s = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(configuration))!;
        game.Text = s.Game; workspace.Text = s.Workspace; project.Text = s.Project; unity.Text = s.Unity; blender.Text = s.Blender;
        import.Checked = s.ImportUnity; verify.Checked = s.VerifyPlay; weights.Checked = s.Weights;
        uiWalk.Checked = s.UiWalk; aclWalk.Checked = s.AclWalk;
        force.Checked = false;
        await ExecuteAsync(false);
        return completionReceived && !cancelling && latestError == null && progress.Value == 100;
    }

    public MainForm()
    {
        Text = "终末地角色工具 · 陈千语  |  v0.1.3";
        Font = new Font("Microsoft YaHei UI", 9.5f);
        ForeColor = Ink;
        BackColor = Color.FromArgb(242, 245, 250);
        ClientSize = new Size(1050, 850);
        MinimumSize = new Size(970, 830);
        StartPosition = FormStartPosition.CenterScreen;

        var header = new Panel { Dock = DockStyle.Top, Height = 106, BackColor = Color.FromArgb(25, 47, 81) };
        header.Controls.Add(new Label { Text = "终末地角色工具", Font = new Font("Microsoft YaHei UI", 21, FontStyle.Bold),
            AutoSize = true, ForeColor = Color.White, Location = new Point(24, 15) });
        header.Controls.Add(new Label { Text = "陈千语  /  模型提取 · 动画修复 · Unity 预览", AutoSize = true,
            ForeColor = Color.FromArgb(200, 217, 239), Location = new Point(27, 65) });
        var badge = new Label { Text = "本地处理  ·  两条已验证动画", AutoSize = true,
            ForeColor = Color.FromArgb(195, 215, 240), Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(770, 27) };
        header.Controls.Add(badge);
        header.Resize += (_, _) => badge.Left = header.Width - badge.Width - 26;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 17, 22, 16), ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 213));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 63));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 5, BackColor = Color.White, Padding = new Padding(12, 5, 12, 5) };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        AddPath(paths, 0, "游戏目录", game, false, false);
        AddPath(paths, 1, "输出工作区", workspace, false, true);
        AddPath(paths, 2, "Unity 项目", project, false, false);
        AddPath(paths, 3, "Unity 编辑器", unity, true, false);
        AddPath(paths, 4, "Blender", blender, true, false);
        layout.Controls.Add(paths, 0, 0);

        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10, 5, 0, 0) };
        for (int i = 0; i < 3; i++) options.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333f));
        var clips = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        clips.Controls.Add(new Label { Text = "选择动画", Width = 102, Padding = new Padding(0, 3, 0, 0) });
        clips.Controls.Add(uiWalk); clips.Controls.Add(aclWalk);
        uiWalk.Margin = new Padding(0, 4, 26, 0); aclWalk.Margin = new Padding(0, 4, 0, 0);
        var flags = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        flags.Controls.Add(import); flags.Controls.Add(verify); flags.Controls.Add(weights);
        foreach (Control control in flags.Controls) control.Margin = new Padding(0, 4, 24, 0);
        var cache = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        cache.Controls.Add(force);
        cache.Controls.Add(new Label { Text = "已验证游戏：20260821_c24495e5  ·  Unity：6000.0.84f1", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(20, 3, 0, 0) });
        options.Controls.Add(clips, 0, 0); options.Controls.Add(flags, 0, 1); options.Controls.Add(cache, 0, 2);
        inputs.AddRange([uiWalk, aclWalk, import, verify, weights, force]);
        layout.Controls.Add(options, 0, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        check.Width = 122; start.Width = 202; cancel.Width = 90;
        start.BackColor = Blue; start.ForeColor = Color.White;
        start.FlatAppearance.BorderSize = 0;
        cancel.Enabled = false;
        actions.Controls.AddRange([check, start, cancel]);
        layout.Controls.Add(actions, 0, 2);
        var state = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        status.Dock = DockStyle.Fill;
        status.AutoEllipsis = true;
        status.AutoSize = false;
        status.Padding = new Padding(0, 4, 0, 0);
        state.Controls.Add(status, 0, 0); state.Controls.Add(elapsed, 1, 0);
        state.Controls.Add(progress, 0, 1); state.SetColumnSpan(progress, 2);
        layout.Controls.Add(state, 0, 3);
        layout.Controls.Add(new Label { Text = "运行日志", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 3, 0, 0) }, 0, 4);
        layout.Controls.Add(log, 0, 5);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
        var copyLog = MakeButton("复制日志");
        footer.Controls.AddRange([output, report, openUnity, copyLog]);
        layout.Controls.Add(footer, 0, 6);
        layout.Controls.Add(new Label { Text = "生成目录：Assets/EndfieldChenTool。手工编辑场景请另存；再次导入会重新生成工具场景。", AutoSize = true, ForeColor = Color.DimGray }, 0, 7);
        Controls.Add(layout); Controls.Add(header);
        LoadSettings();
        UpdateImportControls();
        import.CheckedChanged += (_, _) => UpdateImportControls();
        check.Click += async (_, _) => await ExecuteAsync(true);
        start.Click += async (_, _) => await ExecuteAsync(false);
        cancel.Click += (_, _) => CancelRun();
        output.Click += (_, _) => OpenPath(workspace.Text.Trim());
        report.Click += (_, _) => OpenPath(Path.Combine(workspace.Text.Trim(), "结果说明.md"));
        openUnity.Click += (_, _) => OpenUnityPreview();
        copyLog.Click += (_, _) => { if (log.TextLength > 0) Clipboard.SetText(log.Text); };
        timer.Tick += (_, _) => elapsed.Text = watch.Elapsed.ToString(@"mm\:ss");
        FormClosing += (_, e) =>
        {
            if (active is { HasExited: false })
            {
                if (MessageBox.Show(this, "任务仍在运行。关闭窗口会停止当前任务，已完成的提取可在下次复用。", "停止任务并退出？", MessageBoxButtons.OKCancel) != DialogResult.OK)
                { e.Cancel = true; return; }
                CancelRun();
            }
        };
        Append("选择游戏目录和独立的输出工作区。第一次可先点“检查环境”。");
    }

    private static Button MakeButton(string text) => new() { Text = text, Height = 34, Width = 145, FlatStyle = FlatStyle.Flat,
        BackColor = Color.White, ForeColor = Ink, Margin = new Padding(0, 3, 10, 0), Cursor = Cursors.Hand };

    private void AddPath(TableLayoutPanel panel, int row, string title, TextBox box, bool file, bool create)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        panel.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        box.Dock = DockStyle.Fill; box.Margin = new Padding(0, 6, 10, 5);
        panel.Controls.Add(box, 1, row);
        var browse = MakeButton("浏览…"); browse.Dock = DockStyle.Fill; browse.Margin = new Padding(0, 4, 0, 4);
        browse.Click += (_, _) =>
        {
            if (file)
            {
                using var dialog = new OpenFileDialog { Filter = "程序 (*.exe)|*.exe", CheckFileExists = true, FileName = box.Text };
                if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName;
            }
            else
            {
                using var dialog = new FolderBrowserDialog { Description = create ? "选择空目录或本工具已创建的工作区" : "选择" + title, ShowNewFolderButton = create };
                if (Directory.Exists(box.Text)) dialog.SelectedPath = box.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
            }
        };
        panel.Controls.Add(browse, 2, row);
        inputs.Add(box); inputs.Add(browse);
        if (box == project || box == unity) browse.Tag = "unity";
    }

    private void UpdateImportControls()
    {
        if (active is { HasExited: false }) return;
        project.Enabled = unity.Enabled = verify.Enabled = import.Checked;
        foreach (var control in inputs.Where(c => Equals(c.Tag, "unity"))) control.Enabled = import.Checked;
        start.Text = import.Checked ? "开始提取与导入" : "开始提取";
    }

    private UserSettings CurrentSettings() => new()
    {
        Game = game.Text.Trim(), Workspace = workspace.Text.Trim(), Project = project.Text.Trim(), Unity = unity.Text.Trim(), Blender = blender.Text.Trim(),
        ImportUnity = import.Checked, VerifyPlay = verify.Checked, Weights = weights.Checked, UiWalk = uiWalk.Checked, AclWalk = aclWalk.Checked
    };

    private void LoadSettings()
    {
        UserSettings settings = new();
        try { if (File.Exists(settingsPath)) settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(settingsPath)) ?? settings; }
        catch { }
        game.Text = settings.Game; workspace.Text = settings.Workspace; project.Text = settings.Project;
        unity.Text = settings.Unity; blender.Text = settings.Blender;
        import.Checked = settings.ImportUnity; verify.Checked = settings.VerifyPlay; weights.Checked = settings.Weights;
        uiWalk.Checked = settings.UiWalk; aclWalk.Checked = settings.AclWalk;
    }

    public static string FindPython(string blenderPath)
    {
        var directory = Path.GetDirectoryName(blenderPath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            foreach (var folder in Directory.EnumerateDirectories(directory).OrderByDescending(p => p))
            {
                var candidate = Path.Combine(folder, "python", "bin", "python.exe");
                if (File.Exists(candidate)) return candidate;
            }
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (folder.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            try { var candidate = Path.Combine(folder.Trim('"'), "python.exe"); if (File.Exists(candidate)) return candidate; }
            catch { }
        }
        return "";
    }

    private async Task ExecuteAsync(bool checkOnly)
    {
        if (active is { HasExited: false }) return;
        var settings = CurrentSettings();
        var python = FindPython(settings.Blender);
        if (!File.Exists(python))
        {
            MessageBox.Show(this, "找不到 Python。请选择 Blender 安装目录中的 blender.exe，工具会使用它自带的 Python。也可将 Python 3.10 或以上加入 PATH。", "需要 Python");
            return;
        }
        if (!settings.UiWalk && !settings.AclWalk) { MessageBox.Show(this, "请至少选择一条动画。"); return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            completionReceived = cancelling = false; latestError = null;
            progress.Value = 0;
            log.Clear();
            Append("Python：" + python);
            foreach (var control in inputs) control.Enabled = false;
            check.Enabled = start.Enabled = openUnity.Enabled = false; cancel.Enabled = true;
            watch.Restart(); timer.Start();
            var info = new ProcessStartInfo(python)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = AppContext.BaseDirectory
            };
            info.Environment["PYTHONUTF8"] = "1";
            info.Environment["PYTHONIOENCODING"] = "utf-8";
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "pipeline.py"));
            void Arg(string name, string value) { info.ArgumentList.Add(name); info.ArgumentList.Add(value); }
            Arg("--game", settings.Game); Arg("--workspace", settings.Workspace);
            Arg("--blender", settings.Blender);
            Arg("--animations", string.Join(',', new[] { settings.UiWalk ? "ui" : null, settings.AclWalk ? "acl" : null }.Where(x => x != null)));
            if (settings.ImportUnity) { Arg("--unity-project", settings.Project); Arg("--unity", settings.Unity); }
            if (settings.Weights) info.ArgumentList.Add("--weights");
            if (settings.VerifyPlay && settings.ImportUnity) info.ArgumentList.Add("--verify-play");
            if (force.Checked) info.ArgumentList.Add("--force");
            if (checkOnly) info.ArgumentList.Add("--check-only");
            using var process = new Process { StartInfo = info };
            active = process;
            process.OutputDataReceived += (_, e) => { if (e.Data != null) BeginInvoke(() => HandleLine(e.Data)); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) BeginInvoke(() => Append(e.Data)); };
            if (!process.Start()) throw new Exception("无法启动处理程序。");
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit();
            await Task.Delay(80);
            if (cancelling)
            {
                status.Text = "已取消 · 下次运行会校验并复用已完成的步骤。";
                status.ForeColor = Color.DarkOrange;
                Append("任务已取消。正在生成的步骤需要重新运行。");
            }
            else if (process.ExitCode == 0 && completionReceived)
            {
                status.Text = checkOnly ? "环境检查通过，可以开始提取。" : "处理完成 · 可查看结果说明或打开 Unity 预览。";
                status.ForeColor = Color.FromArgb(28, 121, 76);
                progress.Value = 100;
            }
            else
            {
                status.Text = "未完成 · " + (latestError?.Split('\n')[0] ?? $"处理程序退出码 {process.ExitCode}，请查看日志。");
                status.ForeColor = Color.Firebrick;
            }
        }
        catch (Exception error)
        {
            status.Text = "未完成 · " + error.Message;
            status.ForeColor = Color.Firebrick;
            Append(error.ToString());
        }
        finally
        {
            timer.Stop(); watch.Stop(); active = null;
            foreach (var control in inputs) control.Enabled = true;
            check.Enabled = start.Enabled = openUnity.Enabled = true; cancel.Enabled = false;
            UpdateImportControls();
        }
    }

    private void HandleLine(string line)
    {
        if (!line.StartsWith("@event ", StringComparison.Ordinal)) { Append(line); return; }
        try
        {
            using var document = JsonDocument.Parse(line[7..]);
            var item = document.RootElement;
            string message = item.GetProperty("message").GetString() ?? "";
            Append(message);
            status.Text = message.Split('\n')[0]; status.ForeColor = Ink;
            if (item.TryGetProperty("progress", out var p)) progress.Value = Math.Clamp(p.GetInt32(), 0, 100);
            var type = item.GetProperty("type").GetString();
            if (type == "error") { latestError = message; status.ForeColor = Color.Firebrick; }
            if (type == "completed") completionReceived = true;
        }
        catch (JsonException) { Append(line); }
    }

    private void Append(string text)
    {
        if (log.TextLength > 240000) log.Text = log.Text[^120000..];
        log.AppendText(DateTime.Now.ToString("HH:mm:ss ") + text + Environment.NewLine);
    }

    private void CancelRun()
    {
        cancelling = true;
        try { if (active is { HasExited: false }) active.Kill(entireProcessTree: true); }
        catch (Exception error) { Append("停止任务失败：" + error.Message); }
    }

    private void OpenPath(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) { MessageBox.Show(this, "该结果尚未生成，请先完成提取。", "还没有结果"); return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "打开失败"); }
    }

    private void OpenUnityPreview()
    {
        try
        {
            var path = Path.Combine(workspace.Text.Trim(), "last_run.json");
            if (!File.Exists(path)) { MessageBox.Show(this, "请先完成 Unity 导入。"); return; }
            using var result = JsonDocument.Parse(File.ReadAllText(path));
            if (!result.RootElement.TryGetProperty("unity", out var imported) || imported.GetProperty("status").GetString() != "completed")
            { MessageBox.Show(this, "该工作区尚未完成 Unity 导入，请勾选导入选项后运行。"); return; }
            var projectPath = imported.GetProperty("project").GetString()!;
            var scenes = imported.GetProperty("scenes");
            string mode = scenes.TryGetProperty("ui", out _) ? "ui" : "acl";
            string method = mode == "ui" ? "OpenUiSceneInteractive" : "OpenAclSceneInteractive";
            string lockPath = Path.Combine(projectPath, "Temp", "UnityLockfile");
            if (File.Exists(lockPath))
            {
                try { using var stream = File.Open(lockPath, FileMode.Open, FileAccess.Read, FileShare.None); }
                catch (IOException)
                {
                    MessageBox.Show(this, "项目已在 Unity 打开。请使用菜单 Endfield Chen Tool 打开对应的行走预览，或打开 Assets/EndfieldChenTool 中的场景。", "使用已打开的项目");
                    return;
                }
            }
            var info = new ProcessStartInfo(unity.Text.Trim()) { UseShellExecute = false };
            info.ArgumentList.Add("-projectPath"); info.ArgumentList.Add(projectPath);
            info.ArgumentList.Add("-executeMethod"); info.ArgumentList.Add("EndfieldChenTool.Generated.SceneBuilder." + method);
            Process.Start(info);
            Append("已请求 Unity 打开预览。如果项目已在编辑器打开，请打开 Assets/EndfieldChenTool 中对应的场景。");
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "打开 Unity 失败"); }
    }
}
