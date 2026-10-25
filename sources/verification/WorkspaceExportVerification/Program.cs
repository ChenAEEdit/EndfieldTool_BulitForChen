using System.Reflection;
using System.Text.Json;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

internal static class Program
{
    private static readonly string[] ClipNames = ["A_actor_chen_ui_overview_loop", "A_actor_chen_ui_relax_loop",
        "A_actor_chen_uiteam_idle_01", "A_actor_chen_uiteam_idle_02", "A_actor_chen_uiteam_idle_03",
        "A_actor_chen_uiteam_idle_04", "female_walk", "A_actor_chen_customized_state_walk_loop"];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("Usage: <tool directory> <plan.json> <report directory>"); return 2; }
        var package = Path.GetFullPath(args[0]);
        var saved = new SavedPlan(Path.GetFullPath(args[1]), JsonFiles.Read<ExtractionPlan>(args[1]));
        var reportDirectory = Path.GetFullPath(args[2]); Directory.CreateDirectory(reportDirectory);
        // Run the real Form and services against the delivered package, in this isolated process.
        AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", package + Path.DirectorySeparatorChar);
        ApplicationConfiguration.Initialize();
        var type = typeof(ObjectWorkflow).Assembly.GetType("EndfieldChenTool.Framework.WorkspaceForm", throwOnError: true)!;
        using var form = (Form)Activator.CreateInstance(type)!;
        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new(-20000, -20000);
        var checks = new List<string>();
        ObjectOperationResult? inspected = null, full = null, animationsOnly = null, rejectedAvatar = null;
        string? failure = null;
        var packageVersion = JsonFiles.Read<JsonElement>(Path.Combine(package, "framework-package.json"))
            .GetProperty("version").GetString()!.Split('-')[0];
        int code = 1;
        T Field<T>(string name) => (T)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        object? Call(string name, params object[] values) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, values);
        async Task Run(bool export) => await (Task)Call("RunObjectOperationAsync", export)!;
        BundleObjectRow? Row(object? choice) => choice?.GetType().GetProperty("Row")?.GetValue(choice) as BundleObjectRow;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidDataException(description + " FAILED; UI status: " + Field<Label>("status").Text);
            checks.Add(description); Console.WriteLine("PASS " + description);
        }
        void ChooseClips(params string[] names)
        {
            var list = Field<CheckedListBox>("clipRows");
            for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, names.Contains(Row(list.Items[i])?.Name));
            Check(Field<HashSet<string>>("checkedClips").Count == names.Length, "Selected " + names.Length + " exact AnimationClip objects");
        }
        form.Shown += async (_, _) =>
        {
            try
            {
                Check(form.Text.Contains(packageVersion) && Field<TabControl>("pages").TabPages.Count == 6, "Delivered workbench Form initializes all six pages at version " + packageVersion);
                Field<Dictionary<string, TextBox>>("paths")["workspace"].Text = saved.Plan.Options.WorkspaceDirectory;
                Field<TabControl>("pages").SelectedIndex = 2;
                Call("RefreshTasks");
                var tasks = Field<ListView>("tasks");
                var taskRow = tasks.Items.Cast<ListViewItem>().Single(item => ((SavedPlan)item.Tag!).Plan.Id == saved.Plan.Id);
                taskRow.Selected = true;
                Check(tasks.SelectedItems.Count == 1, "Selected the saved extraction task in the Form");
                await Run(false);
                inspected = ObjectWorkflow.ReadResult(saved);
                Check(inspected?.Status == "inspected", "Real Form inspection action and C# result checks complete");
                var inventory = ObjectWorkflow.ReadInventory(saved, inspected!.InventoryPath!);
                var model = inventory.Objects.Single(row => row.IsModelCandidate && row.PathId == saved.Plan.RootPathId && row.Name == saved.Plan.RootName);
                var models = Field<ComboBox>("modelChoice");
                models.SelectedIndex = Enumerable.Range(0, models.Items.Count).Single(index => Row(models.Items[index])?.Key == model.Key);
                var avatars = Field<ComboBox>("avatarChoice");
                Check(Row(avatars.SelectedItem)?.Key == model.LinkedAvatarKey, "Avatar automatically follows the actual Animator reference");
                ChooseClips(ClipNames);
                var search = Field<TextBox>("objectSearch");
                search.Text = "A_actor_chen_uiteam_idle_01";
                Check(Field<CheckedListBox>("clipRows").Items.Count == 1 && Field<HashSet<string>>("checkedClips").Count == 8,
                    "Filtering hides clips without dropping checked selections");
                search.Clear();
                Check(Field<CheckedListBox>("clipRows").CheckedItems.Count == 8, "Clearing filter restores all eight checks");
                var wrongAvatar = Enumerable.Range(1, avatars.Items.Count - 1).FirstOrDefault(index => Row(avatars.Items[index])?.Key != model.LinkedAvatarKey);
                if (wrongAvatar > 0)
                {
                    avatars.SelectedIndex = wrongAvatar;
                    await Run(true);
                    rejectedAvatar = ObjectWorkflow.ReadResult(saved);
                    Check(rejectedAvatar?.Status == "failed" && rejectedAvatar.Files.Count == 0 && rejectedAvatar.Message.Contains("Avatar"),
                        "Mismatched Avatar is rejected before exporting files");
                    avatars.SelectedIndex = Enumerable.Range(1, avatars.Items.Count - 1).Single(index => Row(avatars.Items[index])?.Key == model.LinkedAvatarKey);
                }
                await Run(true);
                full = ObjectWorkflow.ReadResult(saved);
                Check(full?.Status == "exported_unverified" && full.Failures.Count == 0, "Model and eight animations export through the actual C# Form action");
                Check(full!.Files.Count(row => row.Kind == "FBX") == 1 && full.Files.Count(row => row.Kind == "AnimationSource") == 8,
                    "Full run contains one FBX and eight animation files");
                Check(Field<ProgressBar>("scanProgress").Value == 100, "Completed export displays 100 percent progress");
                Check(!full.Adapted && !full.PlaybackVerified, "Raw export does not claim adaptation or playback verification");
                using (var capture = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(capture, new Rectangle(Point.Empty, capture.Size));
                    capture.Save(Path.Combine(reportDirectory, "workbench-full-export.png"));
                }
                Call("LoadSavedObjects");
                Check(Field<ObjectInventory>("objectInventory").RunId == inventory.RunId, "Saved object inventory reloads through the Form");
                models.SelectedIndex = 0;
                avatars.SelectedIndex = Enumerable.Range(1, avatars.Items.Count - 1).Single(index => Row(avatars.Items[index])?.Key == model.LinkedAvatarKey);
                ChooseClips("A_actor_chen_ui_overview_loop", "A_actor_chen_customized_state_walk_loop");
                await Run(true);
                animationsOnly = ObjectWorkflow.ReadResult(saved);
                Check(animationsOnly?.Status == "exported_unverified" && animationsOnly.Files.Count == 2
                    && animationsOnly.Files.All(row => row.Kind == "AnimationSource"), "Animation-only export succeeds without requesting an FBX");
                code = 0;
            }
            catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(failure); }
            finally
            {
                JsonFiles.Write(Path.Combine(reportDirectory, "ui-verification.json"), new
                {
                    schema = "endfield-workspace-export-verification-v1", passed = code == 0,
                    checkedAt = DateTimeOffset.UtcNow, package, plan = saved.FilePath, checks,
                    inspected, rejectedAvatar, full, animationsOnly, failure,
                    unityImportTested = false, playbackTested = false
                });
                form.Close();
            }
        };
        Application.Run(form);
        return code;
    }
}
