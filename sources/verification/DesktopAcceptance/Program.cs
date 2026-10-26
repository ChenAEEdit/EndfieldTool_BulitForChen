using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

internal static class Program
{
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, int delta, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int state);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, ref NativeRect rect);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int left, top, right, bottom; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int bytes);
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int x, y; public uint data, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput { public ushort virtualKey, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct Input { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MouseInput mouse; [FieldOffset(8)] public KeyboardInput keyboard; }
    static readonly List<object> checks = [];
    static readonly List<object> messages = [];
    sealed class InputTrace : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg is 0x201 or 0x202 or 0x203 or 0x204 or 0x205 or 0x206 or 0x215)
                messages.Add(new { message = m.Msg, hwnd = m.HWnd.ToString(), control = Control.FromHandle(m.HWnd)?.Text, w = m.WParam.ToString(), l = m.LParam.ToString() });
            return false;
        }
    }
    static string reports = "";
    static Form form = null!;
    static Type type = null!;
    static object? Get(string name) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form);
    static T Field<T>(string name) => (T)Get(name)!;
    static IEnumerable<Control> Children(Control root) { foreach (Control c in root.Controls) { yield return c; foreach (var item in Children(c)) yield return item; } }
    static ButtonBase Button(string name) => Children(form).OfType<ButtonBase>().Single(c => c.Text == name && c.Visible);
    [STAThread] static int Main(string[] args)
    {
        string package = Path.GetFullPath(args[0]), root = Path.GetFullPath(args[1]);
        string mode = args.Length > 2 ? args[2] : "recovery";
        reports = Path.Combine(root, "desktop", mode); Directory.CreateDirectory(reports);
        AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", package + Path.DirectorySeparatorChar);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        Application.AddMessageFilter(new InputTrace());
        var state = JsonFiles.Read<JsonElement>(Path.Combine(root, "target-state.json"));
        string planPath = Environment.GetEnvironmentVariable("ENDFIELD_DESKTOP_PLAN") ?? state.GetProperty("first").GetString()!;
        var saved = new SavedPlan(planPath, JsonFiles.Read<ExtractionPlan>(planPath));
        var partial = UnityFailureSummary.Read(saved);
        var prepared = ObjectWorkflow.ReadPreparation(saved)!;
        type = typeof(WorkspaceStore).Assembly.GetType("EndfieldChenTool.Framework.WorkspaceForm")!;
        form = (Form)Activator.CreateInstance(type, nonPublic: true)!;
        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(40, 40);
        form.ClientSize = new Size(1320, 900);
        int exit = 1;
        form.Shown += async (_, _) =>
        {
            try
            {
                bool realDpi = mode is "real150" or "real200";
                int expectedNativeDpi = mode == "real150" ? 144 : mode == "real200" ? 192 : 96;
                if (realDpi) Require(GetDpiForWindow(form.Handle) == expectedNativeDpi && form.DeviceDpi == expectedNativeDpi, "Actual OS display DPI matches requested scale");
                if (mode == "workflow") await SearchByInput();
                // Fixture setup only. Navigation, search, selection and recovery below
                // are actual OS mouse/keyboard input; no handler or PerformClick calls.
                Field<Dictionary<string, TextBox>>("paths")["workspace"].Text = saved.Plan.Options.WorkspaceDirectory;
                type.GetMethod("RefreshTasks", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null);
                await Click(Field<Dictionary<int, Button>>("sectionButtons")[2]);
                var tasks = Field<ListView>("tasks");
                var item = tasks.Items.Cast<ListViewItem>().Single(row => ((SavedPlan)row.Tag!).Plan.Id == saved.Plan.Id);
                await Click(tasks, new Point(item.Bounds.Left + 40, item.Bounds.Top + item.Bounds.Height / 2));
                await Click(Button("打开角色与动作"));
                Require(Field<TabControl>("pages").SelectedIndex == 5 && Field<Label>("projectContext").Text.Contains(UnityTaskTarget.Resolve(saved).Plan.Options.UnityProject), "Actual task selection and action-page navigation");
                if (mode == "workflow") { await WorkflowByInput(saved); exit = 0; return; }
                if (mode is "target-save" or "target-read")
                {
                    string requested = state.GetProperty(mode == "target-save" ? "projectA" : "projectB").GetString()!;
                    if (mode == "target-read") Require(Field<Label>("projectContext").Text.Contains(state.GetProperty("projectA").GetString()!), "New UI process reloads previously saved target");
                    await Click(Field<Button>("changeTarget"));
                    Field<TextBox>("targetProject").Text = requested;
                    await Click(Button("保存到当前任务"));
                    Require(UnityTaskTarget.Resolve(saved).Plan.Options.UnityProject == requested && Field<Label>("projectContext").Text.Contains(requested), "Actual save button persists selected target and updates header");
                    JsonFiles.Write(Path.Combine(reports, "target-ui.json"), new { passed = true, mode, requested, actualMouseInput = true, processId = Environment.ProcessId, checks });
                    exit = 0; return;
                }
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                ObjectOperationResult recovered;
                if (mode == "recovery")
                {
                Require(Field<Button>("retryConverted").Enabled && Field<TextBox>("adaptationDetails").Text.Contains("1 条已转换"), "Partial failure shown with recovery button");
                Capture("before-recovery.png");
                await Click(Field<Button>("retryConverted"));
                await Wait(() => Get("extractionCancellation") != null, deadline.Token);
                await Wait(() => Get("extractionCancellation") == null, deadline.Token);
                recovered = ObjectWorkflow.ReadPreparation(saved)!;
                Require(recovered.RunId != prepared.RunId && recovered.Adapted && recovered.ExpectedAdaptedClips == 1, "Actual recovery click generates a separate successful package");
                var selection = JsonFiles.Read<ObjectExportSelection>(Path.Combine(Path.GetDirectoryName(planPath)!, "objects", recovered.RunId, "selection.json"));
                var originalSelection = JsonFiles.Read<ObjectExportSelection>(Path.Combine(Path.GetDirectoryName(planPath)!, "objects", prepared.RunId, "selection.json"));
                Require(selection.ClipKeys.SequenceEqual(partial!.ConvertedKeys) && selection.ModelKey == originalSelection.ModelKey && selection.AvatarKey == originalSelection.AvatarKey
                    && selection.Adaptation!.DropUnmatched == originalSelection.Adaptation!.DropUnmatched && !selection.Adaptation.DropUnmatched, "Recovery preserves exact selection and does not ignore unknown tracks");
                var oldFiles = JsonFiles.Read<Dictionary<string, string>>(Path.Combine(root, "partial-output-before-recovery.json"));
                Require(oldFiles.All(pair => File.Exists(pair.Key) && Hash(pair.Key) == pair.Value), "Failed package outputs preserved byte-for-byte");
                JsonFiles.Write(Path.Combine(reports, "recovery-verification.json"), new { passed = true, actualMouseInput = true, checks, recovered });
                }
                else recovered = ObjectWorkflow.ReadPreparation(saved)!;
                await Click(Field<Dictionary<int, Button>>("sectionButtons")[4]);
                await Click(Field<Dictionary<int, Button>>("sectionButtons")[0]);
                Require(Field<TabControl>("pages").SelectedIndex == 5, "Workflow returns to the user's previous step");
                int requestedDpi = mode == "dpi150" ? 144 : mode == "dpi200" ? 192 : form.DeviceDpi;
                if (mode.StartsWith("dpi"))
                {
                    var working = Screen.FromControl(form).WorkingArea;
                    var rect = new NativeRect { left = working.Left, top = working.Top, right = working.Right, bottom = working.Bottom };
                    SendMessage(form.Handle, 0x02E0, new IntPtr((requestedDpi << 16) | requestedDpi), ref rect);
                    await Task.Delay(600);
                    checks.Add(new { name = "dpi-notification", requestedDpi, handledDpi = form.DeviceDpi, nativeWindowDpi = GetDpiForWindow(form.Handle), minimumSize = form.MinimumSize.ToString(), bounds = form.Bounds.ToString(), workingArea = working.ToString(), simulatedNotification = true });
                    Capture("dpi-layout.png");
                    Require(form.DeviceDpi == requestedDpi && working.Contains(form.Bounds), "DPI notification scales controls and keeps window inside display");
                }
                else if (!realDpi) form.Size = new Size(1050, 800);
                await Task.Delay(500); Capture("minimum-window.png");
                await Click(Button("高级：单位、骨骼规则与单步执行"));
                Capture("minimum-advanced.png");
                await Click(Button("核对已有结果"));
                await Wait(() => Get("extractionCancellation") == null, deadline.Token);
                Require(!Field<Label>("status").Text.StartsWith("适配操作未完成"), "Small-window advanced action reachable by real scrolling and click");
                checks.Add(new { name = "layout", realDeviceDpi = form.DeviceDpi, nativeWindowDpi = GetDpiForWindow(form.Handle), size = form.Size.ToString(), workingArea = Screen.FromControl(form).WorkingArea.ToString(), highDpiScalesVerified = realDpi });
                JsonFiles.Write(Path.Combine(reports, "desktop-verification.json"), new { passed = true, actualMouseInput = true, checks, recovered, status = Field<Label>("status").Text, highDpiVerified = realDpi, unityGameMouseControlsVerified = false });
                exit = 0;
            }
            catch (Exception error)
            {
                Capture("failure.png");
                var chain = new List<object>();
                for (Control? current = Children(form).FirstOrDefault(c => c.Text == "核对已有结果"); current != null; current = current.Parent)
                    chain.Add(new { type = current.GetType().Name, bounds = current.Bounds.ToString(), client = current.ClientSize.ToString(), screen = current.RectangleToScreen(current.ClientRectangle).ToString(),
                        dock = current.Dock.ToString(), padding = current.Padding.ToString(), display = current.DisplayRectangle.ToString(), scroll = current is ScrollableControl sc ? sc.AutoScrollPosition.ToString() : "" });
                JsonFiles.Write(Path.Combine(reports, "desktop-verification.json"), new { passed = false, actualMouseInput = true, checks, messages, chain, error = error.ToString(), status = Field<Label>("status").Text });
                Console.Error.WriteLine(error);
            }
            finally { form.Close(); }
        };
        Application.Run(form); return exit;
    }
    static async Task Wait(Func<bool> predicate, CancellationToken token) { while (!predicate()) await Task.Delay(100, token); }
    static async Task TypeInto(TextBox box, string value)
    {
        await Click(box);
        Input Key(ushort key, uint flags = 0) => new() { type = 1, keyboard = new KeyboardInput { virtualKey = key, flags = flags } };
        Input[] select = [Key(17), Key(65), Key(65, 2), Key(17, 2)];
        Require(SendInput((uint)select.Length, select, Marshal.SizeOf<Input>()) == select.Length, "Native keyboard select-all input submitted");
        await Task.Delay(150);
        var typing = value.Length == 0 ? new[] { Key(8), Key(8, 2) } : value.SelectMany(c => new[] {
            new Input { type = 1, keyboard = new KeyboardInput { scan = c, flags = 4 } },
            new Input { type = 1, keyboard = new KeyboardInput { scan = c, flags = 6 } } }).ToArray();
        Require(SendInput((uint)typing.Length, typing, Marshal.SizeOf<Input>()) == typing.Length, "Native Unicode keyboard input submitted");
        await Task.Delay(900);
        Require(box.Text == value, "Actual keyboard text reaches search field: " + value);
    }
    static async Task SearchByInput()
    {
        var search = Field<TextBox>("search");
        await TypeInto(search, "chen");
        var characters = Field<ListView>("characters");
        Require(characters.Items.Count > 0 && characters.Items.Cast<ListViewItem>().All(i => ((CharacterEntry)i.Tag!).Id.Contains("chen", StringComparison.OrdinalIgnoreCase)), "Character search filters real catalog rows");
        await TypeInto(search, "acceptance_no_character_93817"); Require(characters.Items.Count == 0, "No-result character search is empty");
        await TypeInto(search, "");
        await Click(characters, new Point(35, characters.Items[0].Bounds.Top + characters.Items[0].Bounds.Height / 2));
        Require(Field<CheckedListBox>("resources").Items.Count > 0, "Actual character click loads resource choices");
    }
    static async Task WorkflowByInput(SavedPlan saved)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        Process? owned = null;
        try
        {
            await TypeInto(Field<TextBox>("objectSearch"), "ui_relax_loop");
            var clips = Field<CheckedListBox>("clipRows");
            Require(clips.Items.Count == 1, "Object search isolates exactly one real idle clip");
            await Click(clips, new Point(9, clips.GetItemRectangle(0).Top + clips.ItemHeight / 2));
            Require(clips.CheckedItems.Count == 1 && Field<ComboBox>("modelChoice").SelectedIndex > 0 && Field<ComboBox>("avatarChoice").SelectedIndex > 0, "Mouse clip selection retains actual model and linked Avatar");
            await Click(Field<Button>("previewAction"));
            await Wait(() => Get("extractionCancellation") != null && Field<ProgressBar>("scanProgress").Value >= 30, deadline.Token);
            await Click(Field<Button>("stopScan"));
            await Wait(() => Get("extractionCancellation") == null, deadline.Token);
            Require(Field<Label>("status").Text.Contains("取消"), "Real mouse cancellation stops running object export");
            var interrupted = ObjectWorkflow.ReadResult(saved)!;
            Require(interrupted.Status == "cancelled", "Cancelled operation persisted on disk");
            await Click(Field<Button>("previewAction"));
            await Wait(() => Get("extractionCancellation") != null, deadline.Token);
            await Wait(() => Get("extractionCancellation") == null, deadline.Token);
            var generated = ObjectWorkflow.ReadPreparation(saved)!;
            Require(generated.Adapted && generated.ExpectedAdaptedClips == 1 && generated.RunId != interrupted.RunId, "Mouse retry generates a separate complete native preview");
            JsonFiles.Write(Path.Combine(reports, "workflow-generated.json"), generated);
            var target = UnityTaskTarget.Resolve(saved).Plan.Options;
            string validation = Path.Combine(target.UnityProject, "Assets/EndfieldCharacterValidation/Editor");
            File.Copy(Path.Combine(Directory.GetCurrentDirectory(), "sources/verification/UnityLaunchVerification.cs"), Path.Combine(validation, "UnityLaunchVerification.cs"), true);
            string request = Path.Combine(reports, "launch-request.json");
            JsonFiles.Write(request, new { scene = generated.UnityAssetRoot + "/Generated/CharacterPreview.unity", directory = reports });
            var before = Process.GetProcessesByName("Unity").Select(p => p.Id).ToHashSet();
            Environment.SetEnvironmentVariable("ENDFIELD_LAUNCH_CHECK", request);
            await Click(Button("打开并播放预览"));
            await Wait(() => Get("extractionCancellation") == null && File.Exists(Path.Combine(reports, "launch-started.json")), deadline.Token);
            owned = Process.GetProcessesByName("Unity").Single(p => !before.Contains(p.Id) && p.MainModule!.FileName.Equals(target.UnityEditor, StringComparison.OrdinalIgnoreCase));
            Require(Field<Label>("status").Text.Contains("播放"), "Mouse open receives native preview acknowledgement");
            owned.Refresh(); ShowWindow(owned.MainWindowHandle, 6); await Task.Delay(500);
            await Click(Button("打开并播放预览"));
            await Wait(() => Get("extractionCancellation") == null && File.Exists(Path.Combine(reports, "launch-verification.json")), deadline.Token);
            var proof = JsonFiles.Read<JsonElement>(Path.Combine(reports, "launch-verification.json"));
            Require(proof.GetProperty("passed").GetBoolean() && proof.GetProperty("actualPlay").GetBoolean() && proof.GetProperty("timeAdvanced").GetBoolean(), "Mouse open and reopen reach actual Play with advancing animation time");
            await owned.WaitForExitAsync(deadline.Token); Require(owned.ExitCode == 0, "Owned preview editor exits after verification");
            JsonFiles.Write(Path.Combine(reports, "workflow-verification.json"), new { passed = true, actualMouseInput = true, checks, generated, proof, saved.FilePath });
        }
        finally
        {
            Environment.SetEnvironmentVariable("ENDFIELD_LAUNCH_CHECK", null);
            if (owned != null && !owned.HasExited) { owned.Kill(true); await owned.WaitForExitAsync(); }
            owned?.Dispose();
        }
    }
    static async Task Click(Control control, Point? local = null)
    {
        if (GetForegroundWindow() != form.Handle) { SetForegroundWindow(form.Handle); form.BringToFront(); }
        await Task.Delay(Math.Max(350, SystemInformation.DoubleClickTime + 100));
        if (GetForegroundWindow() != form.Handle)
        {
            var caption = new Point(form.Left + 100, form.Top + Math.Max(10, SystemInformation.CaptionHeight / 2));
            if (GetAncestor(WindowFromPoint(caption), 2) == form.Handle)
            {
                SetCursorPos(caption.X, caption.Y);
                SendInput(1, [new Input { mouse = new MouseInput { flags = 2 } }], Marshal.SizeOf<Input>());
                await Task.Delay(100);
                SendInput(1, [new Input { mouse = new MouseInput { flags = 4 } }], Marshal.SizeOf<Input>());
                await Task.Delay(400);
                checks.Add(new { name = "native-caption-focus", point = caption.ToString(), acquired = GetForegroundWindow() == form.Handle });
            }
        }
        if (GetForegroundWindow() != form.Handle) throw new Exception("Cannot acquire foreground for real mouse verification");
        for (int n = 0; n < 12; n++)
        {
            Point point = control.PointToScreen(local ?? new Point(control.Width / 2, control.Height / 2));
            Rectangle visible = control.RectangleToScreen(control.ClientRectangle);
            for (Control? parent = control.Parent; parent != null; parent = parent.Parent) visible.Intersect(parent.RectangleToScreen(parent.ClientRectangle));
            visible.Intersect(Screen.FromControl(form).WorkingArea);
            if (control.Enabled && control.Visible && visible.Contains(point))
            {
                bool received = false;
                EventHandler handler = (_, _) => received = true;
                control.Click += handler;
                try
                {
                    bool moved = SetCursorPos(point.X, point.Y); await Task.Delay(100);
                    GetCursorPos(out var actual);
                    var target = WindowFromPoint(actual);
                    string events = "";
                    MouseEventHandler downHandler = (_, e) => events += "down:" + e.Button + ";";
                    MouseEventHandler upHandler = (_, e) => events += "up:" + e.Button + ";";
                    control.MouseDown += downHandler; control.MouseUp += upHandler;
                    var inputResult = await Task.Run(() => {
                        uint d = SendInput(1, [new Input { mouse = new MouseInput { flags = 2 } }], Marshal.SizeOf<Input>());
                        Thread.Sleep(120);
                        uint u = SendInput(1, [new Input { mouse = new MouseInput { flags = 4 } }], Marshal.SizeOf<Input>());
                        return (d, u);
                    });
                    uint down = inputResult.d, up = inputResult.u;
                    await Task.Delay(300);
                    checks.Add(new { name = "mouse-click", control = control.Text.Length > 80 ? control.Text[..80] : control.Text,
                        point = point.ToString(), moved, actual = actual.ToString(), target = target.ToString(), expected = control.Handle.ToString(), sentDown = down, sentUp = up, received, events });
                    control.MouseDown -= downHandler; control.MouseUp -= upHandler;
                    if (control is ButtonBase && !received) throw new Exception("Actual mouse click not delivered: " + control.Text);
                    return;
                }
                finally { control.Click -= handler; }
            }
            var parents = new List<ScrollableControl>();
            for (Control? parent = control.Parent; parent != null; parent = parent.Parent)
                if (parent is ScrollableControl sc && sc.AutoScroll && sc.VerticalScroll.Visible && !sc.RectangleToScreen(sc.ClientRectangle).Contains(point)) parents.Add(sc);
            var scroll = parents.LastOrDefault();
            if (scroll == null) break;
            var bar = scroll.VerticalScroll;
            int arrow = SystemInformation.VerticalScrollBarArrowHeight;
            int track = scroll.Height - 2 * arrow;
            int thumb = Math.Max(12, track * bar.LargeChange / Math.Max(1, bar.Maximum + 1));
            int range = Math.Max(1, bar.Maximum - bar.LargeChange + 1);
            int startY = arrow + (track - thumb) * bar.Value / range + thumb / 2;
            Point from = scroll.PointToScreen(new Point(scroll.Width - 8, startY));
            int centerY = scroll.PointToScreen(new Point(0, scroll.ClientSize.Height / 2)).Y;
            int desiredValue = Math.Clamp(bar.Value + point.Y - centerY, 0, range);
            int targetY = arrow + thumb / 2 + (track - thumb) * desiredValue / range;
            Point to = scroll.PointToScreen(new Point(scroll.Width - 8, targetY));
            SetCursorPos(from.X, from.Y); await Task.Delay(150);
            SendInput(1, [new Input { mouse = new MouseInput { flags = 2 } }], Marshal.SizeOf<Input>()); await Task.Delay(120);
            SetCursorPos(to.X, to.Y); await Task.Delay(160);
            SendInput(1, [new Input { mouse = new MouseInput { flags = 4 } }], Marshal.SizeOf<Input>()); await Task.Delay(250);
            checks.Add(new { name = "scrollbar-drag", before = startY, after = bar.Value, from = from.ToString(), to = to.ToString() });
        }
        throw new Exception("Control not reachable by mouse and scrolling: " + control.Text);
    }
    static void Require(bool pass, string text) { if (!pass) throw new Exception(text); checks.Add(new { name = "assertion", passed = true, text }); Console.WriteLine("PASS " + text); }
    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    static void Capture(string name)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(form.Location, Point.Empty, bitmap.Size);
        bitmap.Save(Path.Combine(reports, name));
    }
}
