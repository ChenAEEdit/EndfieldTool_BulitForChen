using System.Reflection;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string package = Path.GetFullPath(args[0]), planPath = Path.GetFullPath(args[1]), reports = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(reports);
        AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", package + Path.DirectorySeparatorChar);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        var saved = new SavedPlan(planPath, JsonFiles.Read<ExtractionPlan>(planPath));
        var type = typeof(WorkspaceStore).Assembly.GetType("EndfieldChenTool.Framework.WorkspaceForm")!;
        using var form = (Form)Activator.CreateInstance(type, nonPublic: true)!;
        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.ShowInTaskbar = false;
        bool passed = false;
        object? Get(string name) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form);
        object? Call(string name, params object[] values) => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, values);
        form.Shown += (_, _) => form.BeginInvoke((Action)(() =>
        {
            try
            {
                var paths = (Dictionary<string, TextBox>)Get("paths")!;
                paths["workspace"].Text = saved.Plan.Options.WorkspaceDirectory;
                Call("RefreshTasks");
                Call("NavigateTo", 2); Application.DoEvents();
                var tasks = (ListView)Get("tasks")!;
                tasks.Items[0].Selected = true;
                Call("LoadSavedObjects"); Call("NavigateTo", 5);
                form.PerformLayout(); Application.DoEvents();
                var context = (Label)Get("projectContext")!;
                var sections = (Dictionary<int, Button>)Get("sectionButtons")!;
                var clips = (CheckedListBox)Get("clipRows")!;
                bool playButton = Descendants(form).OfType<Button>().Any(button => button.Text == "打开并播放预览" && button.Enabled && button.Visible);
                string[] expected = ["customized_state_walk_end", "customized_state_walk_loop", "customized_state_walk_start", "ui_overview_loop", "ui_relax_loop"];
                bool expectedPresent = expected.All(name => clips.Items.Cast<object>().Any(item => item.ToString()!.Contains(name)));
                passed = sections.Count == 4 && expectedPresent && playButton && context.Text.Contains(saved.Plan.Options.UnityProject);
                using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(reports, "workbench.png"));
                JsonFiles.Write(Path.Combine(reports, "ui-verification.json"), new { passed, title = form.Text, navigationEntries = sections.Count,
                    clips = clips.Items.Count, expectedPresent, playButton, currentProject = context.Text, mouseInteractionTested = false });
            }
            catch (Exception error) { JsonFiles.Write(Path.Combine(reports, "ui-verification.json"), new { passed = false, error = error.ToString() }); }
            finally { form.Close(); }
        }));
        Application.Run(form);
        return passed ? 0 : 1;
    }
    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (var item in Descendants(child)) yield return item; }
    }
}
