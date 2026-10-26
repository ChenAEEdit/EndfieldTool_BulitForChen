using System.Diagnostics;
using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

if (args.Length != 4) { Console.Error.WriteLine("<package> <source-plan> <reports> <target|target-read|scan|extract>"); return 2; }
string package = Path.GetFullPath(args[0]), reports = Path.GetFullPath(args[2]);
Directory.CreateDirectory(reports);
AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", package + Path.DirectorySeparatorChar);
var original = new SavedPlan(Path.GetFullPath(args[1]), JsonFiles.Read<ExtractionPlan>(args[1]));
if (!original.Plan.Options.UnityProject.StartsWith(@"D:\AAAChatGPT\UnityValidation\", StringComparison.OrdinalIgnoreCase))
    throw new Exception("Acceptance requires an independent source project.");
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(45));
int lastPercent = -1;
var progress = new InlineProgress<ExtractionProgress>(p => {
    File.AppendAllText(Path.Combine(reports, args[3] + "-progress.log"), $"{DateTime.Now:HH:mm:ss} {p.Percent}% {p.Message}\n");
    if (p.Percent != lastPercent) { Console.WriteLine($"{DateTime.Now:HH:mm:ss} {p.Percent}% {p.Message}"); lastPercent = p.Percent; }
});
string statePath = Path.Combine(reports, "target-state.json");
try
{
    if (args[3] is "rig-prepare" or "rig-reprepare" or "rig-reinspect")
    {
        string baseline = Path.Combine(Path.GetDirectoryName(reports)!, "remaining_acceptance_20261026");
        var state = JsonFiles.Read<TargetState>(Path.Combine(baseline, "target-state.json"));
        var prior = new SavedPlan(state.First, JsonFiles.Read<ExtractionPlan>(state.First));
        var saved = args[3] != "rig-prepare"
            ? new SavedPlan(File.ReadAllText(Path.Combine(reports, "rig-prepare-plan.txt")).Trim(), JsonFiles.Read<ExtractionPlan>(File.ReadAllText(Path.Combine(reports, "rig-prepare-plan.txt")).Trim()))
            : new WorkspaceStore().Save(prior.Plan with { Id = Guid.NewGuid(), Options = prior.Plan.Options with { WorkspaceDirectory = Path.Combine(reports, "workspace"), UnityProject = state.ProjectB } });
        File.WriteAllText(Path.Combine(reports, "rig-prepare-plan.txt"), saved.FilePath);
        ObjectOperationResult inspected;
        if (args[3] == "rig-reprepare") inspected = ObjectWorkflow.ReadResult(saved)!;
        else if (args[3] == "rig-reinspect") inspected = await new ObjectWorkflow().ExecuteAsync(saved, "inspect", null, null, progress, deadline.Token);
        else
        {
        foreach (var file in BundleExtractionBackend.ReadResult(prior)!.Files)
        {
            string destination = Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "bundles", file.Group, Path.GetFileName(file.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file.Path, destination, false);
        }
        await new BundleExtractionBackend().ExecuteAsync(saved, progress, deadline.Token);
        inspected = await new ObjectWorkflow().ExecuteAsync(saved, "inspect", null, null, progress, deadline.Token);
        }
        var inventory = ObjectWorkflow.ReadInventory(saved, inspected.InventoryPath!);
        var target = inventory.Objects.Single(o => o.IsModelCandidate && o.PathId == prior.Plan.RootPathId);
        var sourceRig = inventory.Objects.Single(o => o.IsModelCandidate && o.Name == "SK_actor_female" && Path.GetFileName(o.SourceBundle) == "7cde33b2f3a520fa070d2454.ab");
        var clip = inventory.Objects.Single(o => o.Kind == "AnimationClip" && o.Name == "female_walk" && Path.GetFileName(o.SourceBundle) == "883c322914dd6cf6f62424d8.ab");
        var prepared = await new ObjectWorkflow().ExecuteAsync(saved, "prepare", inspected.InventoryPath, new ObjectExportSelection(target.Key, target.LinkedAvatarKey, [clip.Key],
            new AnimationPreparationOptions { ModelImportScale = 100, SourceRigKey = sourceRig.Key, DropUnmatched = true }), progress, deadline.Token);
        var generated = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, deadline.Token);
        JsonFiles.Write(Path.Combine(reports, "rig-prepare-generation.json"), new { passed = generated.Result.Adapted, target, sourceRig, clip, prepared, generated, saved.FilePath });
    }
    else if (args[3] == "rig-generate")
    {
        string path = File.ReadAllText(Path.Combine(reports, "rig-prepare-plan.txt")).Trim();
        var saved = new SavedPlan(path, JsonFiles.Read<ExtractionPlan>(path));
        var prepared = ObjectWorkflow.ReadPreparation(saved)!;
        var generated = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, deadline.Token);
        JsonFiles.Write(Path.Combine(reports, "rig-prepare-generation.json"), new { passed = generated.Result.Adapted, prepared, generated, saved.FilePath });
    }
    else if (args[3] == "source-rig")
    {
        var catalog = new JsonCatalogProvider().Load(Path.Combine(reports, "source-rig-catalog.json"));
        var actor = catalog.Characters.Single();
        var plan = new TaskPlanner().Create(catalog, actor, actor.Resources.Select(r => r.Id).ToArray(), original.Plan.Options with { WorkspaceDirectory = Path.Combine(reports, "rig-workspace") });
        var saved = new WorkspaceStore().Save(plan);
        File.WriteAllText(Path.Combine(reports, "source-rig-plan.txt"), saved.FilePath);
        await new BundleExtractionBackend().ExecuteAsync(saved, progress, deadline.Token);
        var result = await new ObjectWorkflow().ExecuteAsync(saved, "inspect", null, null, progress, deadline.Token);
        JsonFiles.Write(Path.Combine(reports, "source-rig-inventory.json"), ObjectWorkflow.ReadInventory(saved, result.InventoryPath!));
    }
    else if (args[3] == "target")
    {
        string projectB = @"D:\AAAChatGPT\UnityValidation\剩余验收 B 20261026";
        if (!Directory.Exists(projectB))
        {
            var start = new ProcessStartInfo(original.Plan.Options.UnityEditor) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var a in new[] { "-batchmode", "-quit", "-createProject", projectB, "-logFile", Path.Combine(reports, "create-project-b.log") }) start.ArgumentList.Add(a);
            using var unity = Process.Start(start)!;
            try { await unity.WaitForExitAsync(deadline.Token); }
            finally { if (!unity.HasExited) { unity.Kill(true); await unity.WaitForExitAsync(); } }
            Require(unity.ExitCode == 0, "Create actual independent project B");
        }
        var store = new WorkspaceStore();
        var first = store.Save(original.Plan with { Id = Guid.NewGuid(), Options = original.Plan.Options with { WorkspaceDirectory = Path.Combine(reports, "目标 保存 workspace") } });
        var second = store.Save(first.Plan with { Id = Guid.NewGuid() });
        string hash1 = Hash(first.FilePath), hash2 = Hash(second.FilePath);
        UnityTaskTarget.Save(first, projectB, first.Plan.Options.UnityEditor);
        UnityTaskTarget.Save(second, original.Plan.Options.UnityProject, second.Plan.Options.UnityEditor);
        Require(UnityTaskTarget.Resolve(first).Plan.Options.UnityProject == projectB, "Target B selected");
        Require(UnityTaskTarget.Resolve(second).Plan.Options.UnityProject == original.Plan.Options.UnityProject, "Independent task A selected");
        using (var busy = new FileStream(Path.Combine(Path.GetDirectoryName(first.FilePath)!, "extraction.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            busy.Lock(0, 1);
            try { Expect<IOException>(() => UnityTaskTarget.Save(first, original.Plan.Options.UnityProject, first.Plan.Options.UnityEditor)); }
            finally { busy.Unlock(0, 1); }
        }
        Expect<InvalidDataException>(() => UnityTaskTarget.Save(first, reports, first.Plan.Options.UnityEditor));
        string record = Path.Combine(Path.GetDirectoryName(first.FilePath)!, "unity-target.json");
        byte[] bytes = File.ReadAllBytes(record);
        try { File.WriteAllText(record, File.ReadAllText(record).Replace("endfield-unity-task-target-v1", "wrong-schema")); Expect<InvalidDataException>(() => UnityTaskTarget.Resolve(first)); }
        finally { File.WriteAllBytes(record, bytes); }
        Require(Hash(first.FilePath) == hash1 && Hash(second.FilePath) == hash2, "Original plan files unchanged");
        JsonFiles.Write(statePath, new TargetState(first.FilePath, second.FilePath, projectB, original.Plan.Options.UnityProject, hash1, hash2));
        var self = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in new[] { package, args[1], reports, "target-read" }) self.ArgumentList.Add(a);
        using var child = Process.Start(self)!; await child.WaitForExitAsync(deadline.Token);
        Require(child.ExitCode == 0, "Fresh process reads persisted task targets");
        JsonFiles.Write(Path.Combine(reports, "target-verification.json"), new { passed = true, separateProcess = true, tasksIndependent = true, planHashesUnchanged = true, concurrentSaveRejected = true, invalidTargetRejected = true, tamperedRecordRejected = true, actualGenerationVerified = false, first.FilePath, projectB });
    }
    else if (args[3] == "target-read")
    {
        var state = JsonFiles.Read<TargetState>(statePath);
        foreach (var row in new[] { (state.First, state.ProjectB, state.Hash1), (state.Second, state.ProjectA, state.Hash2) })
        {
            var loaded = new SavedPlan(row.Item1, JsonFiles.Read<ExtractionPlan>(row.Item1));
            Require(UnityTaskTarget.Resolve(loaded).Plan.Options.UnityProject == row.Item2 && Hash(row.Item1) == row.Item3, "Persisted target after process restart");
        }
        JsonFiles.Write(Path.Combine(reports, "target-reloaded.json"), new { passed = true, processId = Environment.ProcessId });
    }
    else if (args[3] == "scan")
    {
        var options = original.Plan.Options with { WorkspaceDirectory = Path.Combine(reports, "scan-workspace") };
        var result = await new GameResourceIndexer().BuildAsync(options, false, new InlineProgress<IndexProgress>(p => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {p.Percent}% {p.Message}")), deadline.Token);
        JsonFiles.Write(Path.Combine(reports, "scan-result.json"), result);
        Console.WriteLine($"Scan complete: {result.Catalog?.Characters.Count} characters, {result.CatalogPath}");
    }
    else if (args[3] is "actor-movement" or "actors-advanced")
    {
        var results = new List<object>();
        foreach (string actor in (Environment.GetEnvironmentVariable("ENDFIELD_ACCEPTANCE_ACTORS") ?? "avywen,lifeng").Split(','))
        {
            string sourcePlan = File.ReadAllText(Path.Combine(reports, "actors", actor, "plan-path.txt")).Trim();
            var source = new SavedPlan(sourcePlan, JsonFiles.Read<ExtractionPlan>(sourcePlan));
            var sharedBundle = original.Plan.Bundles.Single(b => b.Id == "883c322914dd6cf6f62424d8.ab");
            string sharedName = actor == "avywen" ? "female_walk" : "male_walk";
            var shared = new ResourceEntry { Id = actor + ":acceptance:shared-walk", Name = sharedName, Kind = ResourceKind.Animation,
                Association = AssociationKind.NameCandidate, Evidence = "Explicit shared-motion compatibility test; not an automatic character association", BundleIds = [sharedBundle.Id] };
            var plan = source.Plan with { Id = Guid.NewGuid(), Resources = [..source.Plan.Resources, shared],
                RequestedResourceIds = [..source.Plan.RequestedResourceIds, shared.Id], Bundles = [..source.Plan.Bundles, sharedBundle] };
            var saved = new WorkspaceStore().Save(plan);
            var priorBundles = BundleExtractionBackend.ReadResult(source)!;
            foreach (var file in priorBundles.Files)
            {
                string destination = Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "bundles", file.Group, Path.GetFileName(file.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file.Path, destination, false);
            }
            await new BundleExtractionBackend().ExecuteAsync(saved, progress, deadline.Token);
            var inspected = await new ObjectWorkflow().ExecuteAsync(saved, "inspect", null, null, progress, deadline.Token);
            var inventory = ObjectWorkflow.ReadInventory(saved, inspected.InventoryPath!);
            string modelBundle = actor == "avywen" ? "41423eda1c1aee78b0dce7e7.ab" : "15adc972d0f961a9a7f52d62.ab";
            var model = inventory.Objects.Single(row => row.IsModelCandidate && row.Name.EndsWith(actor + "_uimodel") && Path.GetFileName(row.SourceBundle) == modelBundle);
            var names = new[] { sharedName, "A_actor_" + actor + "_ui_overview_loop", "A_actor_" + actor + "_ui_relax_loop" };
            var clips = names.Select(name => inventory.Objects.Single(row => row.Kind == "AnimationClip" && row.Name == name)).ToList();
            var prepared = await new ObjectWorkflow().ExecuteAsync(saved, "prepare", inspected.InventoryPath,
                new ObjectExportSelection(model.Key, model.LinkedAvatarKey ?? throw new Exception("Selected model has no linked Avatar"), clips.Select(c => c.Key).ToList(),
                    new AnimationPreparationOptions { ModelImportScale = 100, DropUnmatched = args[3] == "actors-advanced" }), progress, deadline.Token);
            File.WriteAllText(Path.Combine(reports, "actors", actor, args[3] + "-plan-path.txt"), saved.FilePath);
            var generated = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, deadline.Token);
            Require(generated.Result.Adapted && generated.Result.ExpectedAdaptedClips == 3, "Shared movement and two idles generated for " + actor);
            File.WriteAllText(Path.Combine(reports, "actors", actor, "movement-plan-path.txt"), saved.FilePath);
            JsonFiles.Write(Path.Combine(reports, "actors", actor, args[3] + "-generation.json"), new { passed = true, advancedOption = args[3] == "actors-advanced", fullTrackFidelity = false, model, clips, prepared, generated, saved.FilePath, playbackVerified = false });
            results.Add(new { actor, saved.FilePath, prepared.UnityAssetRoot });
        }
        JsonFiles.Write(Path.Combine(reports, "actor-movement.json"), new { passed = true, actors = results, playbackVerified = false });
    }
    else if (args[3] is "shared" or "shared-advanced" or "shared-neutral" or "shared-knee")
    {
        var state = JsonFiles.Read<TargetState>(statePath);
        var saved = new SavedPlan(state.First, JsonFiles.Read<ExtractionPlan>(state.First));
        var prior = ObjectWorkflow.ReadResult(saved)!;
        var inventory = ObjectWorkflow.ReadInventory(saved, prior.InventoryPath!);
        var model = inventory.Objects.Single(row => row.IsModelCandidate && row.PathId == saved.Plan.RootPathId);
        bool single = args[3] is "shared-neutral" or "shared-knee";
        var names = single ? new[] { "female_walk" } : new[] { "female_walk", "A_actor_chen_customized_state_walk_loop", "A_actor_chen_ui_relax_loop" };
        var clips = names.Select(name => inventory.Objects.Single(row => row.Kind == "AnimationClip" && row.Name == name)).ToList();
        var prepared = await new ObjectWorkflow().ExecuteAsync(saved, "prepare", prior.InventoryPath,
            new ObjectExportSelection(model.Key, model.LinkedAvatarKey, clips.Select(c => c.Key).ToList(), new AnimationPreparationOptions { ModelImportScale = 100,
                DropUnmatched = args[3] != "shared", ExcludedDescendants = single ? ["Bip001_Head"] : [],
                ExcludedNodes = args[3] == "shared-knee" ? ["corrective_Knee_L_ty_plus", "corrective_Knee_L_ty_minus", "corrective_Knee_R_ty_plus", "corrective_Knee_R_ty_minus"] : [] }), progress, deadline.Token);
        var generated = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, deadline.Token);
        JsonFiles.Write(Path.Combine(reports, args[3] + "-generation.json"), new { passed = generated.Result.Adapted, advancedOption = args[3] != "shared", fullTrackFidelity = false, model, clips, prepared, generated, saved.FilePath, playbackVerified = false });
    }
    else if (args[3] is "regression" or "long-path")
    {
        var state = JsonFiles.Read<TargetState>(statePath);
        var prior = new SavedPlan(state.First, JsonFiles.Read<ExtractionPlan>(state.First));
        var source = ObjectWorkflow.ReadResult(prior)!;
        var inventory = ObjectWorkflow.ReadInventory(prior, source.InventoryPath!);
        var model = inventory.Objects.Single(row => row.IsModelCandidate && row.PathId == prior.Plan.RootPathId);
        var names = args[3] == "regression" ? new[] { "A_actor_chen_customized_state_walk_end", "A_actor_chen_customized_state_walk_loop", "A_actor_chen_customized_state_walk_start", "A_actor_chen_ui_overview_loop", "A_actor_chen_ui_relax_loop" } : new[] { "A_actor_chen_ui_relax_loop" };
        var saved = prior;
        if (args[3] == "long-path")
        {
            string workspace = Path.Combine(reports, "长路径 空格", new string('a', 65), new string('b', 65), "工作区");
            saved = new WorkspaceStore().Save(prior.Plan with { Id = Guid.NewGuid(), Options = prior.Plan.Options with { WorkspaceDirectory = workspace, UnityProject = state.ProjectB } });
            UnityTaskTarget.Save(saved, state.ProjectB, saved.Plan.Options.UnityEditor);
            var previous = BundleExtractionBackend.ReadResult(prior)!;
            foreach (var file in previous.Files)
            {
                string destination = Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "bundles", file.Group, Path.GetFileName(file.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file.Path, destination, false);
            }
            await new BundleExtractionBackend().ExecuteAsync(saved, progress, deadline.Token);
            source = await new ObjectWorkflow().ExecuteAsync(saved, "inspect", null, null, progress, deadline.Token);
            inventory = ObjectWorkflow.ReadInventory(saved, source.InventoryPath!);
            model = inventory.Objects.Single(row => row.IsModelCandidate && row.PathId == prior.Plan.RootPathId);
        }
        var clips = names.Select(name => inventory.Objects.Single(row => row.Kind == "AnimationClip" && row.Name == name)).ToList();
        var prepared = await new ObjectWorkflow().ExecuteAsync(saved, "prepare", source.InventoryPath,
            new ObjectExportSelection(model.Key, model.LinkedAvatarKey, clips.Select(c => c.Key).ToList(), new AnimationPreparationOptions { ModelImportScale = 100 }), progress, deadline.Token);
        var generated = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, deadline.Token);
        Require(generated.Result.Adapted && generated.Result.ExpectedAdaptedClips == names.Length, "Exact requested baseline clips generated");
        JsonFiles.Write(Path.Combine(reports, args[3] + "-generation.json"), new { passed = true, clips, prepared, generated, saved.FilePath, longestOutputPath = prepared.Files.Max(f => f.Path.Length), playbackVerified = false });
    }
    else if (args[3] == "generate-latest")
    {
        var state = JsonFiles.Read<TargetState>(statePath);
        var saved = new SavedPlan(state.First, JsonFiles.Read<ExtractionPlan>(state.First));
        var prepared = ObjectWorkflow.ReadPreparation(saved)!;
        var generated = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, deadline.Token);
        JsonFiles.Write(Path.Combine(reports, "latest-generation.json"), new { passed = generated.Result.Adapted, prepared, generated, saved.FilePath, playbackVerified = false });
    }
    else if (args[3] == "actors")
    {
        var scan = JsonFiles.Read<System.Text.Json.JsonElement>(Path.Combine(reports, "scan-result.json"));
        var catalog = new JsonCatalogProvider().Load(scan.GetProperty("catalogPath").GetString()!);
        var outputs = new List<object>();
        foreach (string actor in new[] { "avywen", "lifeng" })
        {
            var character = catalog.Characters.Single(c => c.Id == actor);
            var options = original.Plan.Options with { WorkspaceDirectory = Path.Combine(reports, "actors", actor, "workspace"), UnityProject = JsonFiles.Read<TargetState>(statePath).ProjectB };
            var selected = character.Resources.Where(r => r.Kind is ResourceKind.Model or ResourceKind.Animation).ToArray();
            var plan = new TaskPlanner().Create(catalog, character, selected.Select(r => r.Id).ToArray(), options);
            var saved = new WorkspaceStore().Save(plan);
            Console.WriteLine($"Actor {actor}: {saved.FilePath}, {saved.Plan.Bundles.Count} planned bundles");
            File.WriteAllText(Path.Combine(reports, "actors", actor, "plan-path.txt"), saved.FilePath);
            var extracted = await new BundleExtractionBackend().ExecuteAsync(saved, progress, deadline.Token);
            var inspected = await new ObjectWorkflow().ExecuteAsync(saved, "inspect", null, null, progress, deadline.Token);
            var inventory = ObjectWorkflow.ReadInventory(saved, inspected.InventoryPath!);
            var models = inventory.Objects.Where(r => r.IsModelCandidate && r.Name.Contains(actor, StringComparison.OrdinalIgnoreCase)).ToList();
            var clips = inventory.Objects.Where(r => r.Kind == "AnimationClip" && r.Name.Contains(actor, StringComparison.OrdinalIgnoreCase)).ToList();
            Require(models.Count > 0 && clips.Count > 0, "Real model and animation objects discovered for " + actor);
            var result = new { saved.FilePath, extracted.RunId, inspected.InventoryPath, models, clips };
            JsonFiles.Write(Path.Combine(reports, "actors", actor, "discovery.json"), result);
            outputs.Add(result);
        }
        JsonFiles.Write(Path.Combine(reports, "actors-discovered.json"), new { passed = true, actors = outputs, playbackVerified = false });
    }
    else if (args[3] == "extract")
    {
        var state = JsonFiles.Read<TargetState>(statePath);
        var saved = new SavedPlan(state.First, JsonFiles.Read<ExtractionPlan>(state.First));
        using var stop = new CancellationTokenSource();
        bool requested = false;
        var trigger = new InlineProgress<ExtractionProgress>(p => {
            progress.Report(p);
            if (!requested && p.Percent > 0 && p.Percent < 95 && p.Message.Contains("Bundle")) { requested = true; stop.CancelAfter(100); }
        });
        bool cancelled = false;
        try { await new BundleExtractionBackend().ExecuteAsync(saved, trigger, stop.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(requested && cancelled, "Cancellation triggered during real extraction");
        var interrupted = BundleExtractionBackend.ReadResult(saved);
        Require(interrupted?.Status == "cancelled", "Cancelled extraction persisted");
        var resumed = await new BundleExtractionBackend().ExecuteAsync(saved, progress, deadline.Token);
        Require(resumed.Status == "bundles_extracted" && resumed.Files.Count == resumed.TotalBundles, "Extraction resumes and validates all outputs");
        foreach (var file in resumed.Files)
        { using var stream = File.OpenRead(file.Path); Require(Convert.ToHexString(MD5.HashData(stream)) == file.Md5.ToUpperInvariant(), "Bundle content MD5"); }
        var inspected = await new ObjectWorkflow().ExecuteAsync(saved, "inspect", null, null, progress, deadline.Token);
        Require(inspected.Status == "inspected", "Object inventory after cancellation and resume");
        JsonFiles.Write(Path.Combine(reports, "extract-cancellation.json"), new { passed = true, interrupted, resumed, inspected, saved.FilePath });
    }
    else if (args[3] == "prefab")
    {
        var state = JsonFiles.Read<TargetState>(statePath);
        var saved = new SavedPlan(state.First, JsonFiles.Read<ExtractionPlan>(state.First));
        var inventoryResult = ObjectWorkflow.ReadResult(saved)!;
        var inventory = ObjectWorkflow.ReadInventory(saved, inventoryResult.InventoryPath!);
        var model = inventory.Objects.Single(row => row.IsModelCandidate && row.PathId == saved.Plan.RootPathId);
        var clip = inventory.Objects.Single(row => row.Kind == "AnimationClip" && row.Name == "A_actor_chen_ui_relax_loop");
        var prepared = await new ObjectWorkflow().ExecuteAsync(saved, "prepare", inventoryResult.InventoryPath,
            new ObjectExportSelection(model.Key, model.LinkedAvatarKey, [clip.Key], new AnimationPreparationOptions { ModelImportScale = 100, CreatePreviewScene = false }), progress, deadline.Token);
        var generated = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, deadline.Token);
        Require(generated.Result.Adapted && generated.Result.UnityPreviewScene == null, "Prefab-only generation accepted with no preview scene");
        var reused = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, deadline.Token);
        Require(reused.Run.Reused, "Prefab-only output reuses strictly verified files");
        JsonFiles.Write(Path.Combine(reports, "prefab-generation.json"), new { passed = true, actualTargetProject = generated.Run.Project, prepared, generated, reused });
    }
    else if (args[3] == "install")
    {
        var state = JsonFiles.Read<TargetState>(statePath);
        string work = Path.Combine(state.ProjectB, "Library/EndfieldCharacterWorkbench");
        foreach (var script in new[] { ("Editor", "CharacterAnimationAdapter.cs", "adapter-install.json"), ("Runtime", "CharacterPreviewControls.cs", "preview-controls-install.json") })
        {
            string source = Path.Combine(package, "templates/workspace", script.Item2);
            var file = new ObjectOutputFile { Path = source, Bytes = new FileInfo(source).Length, Sha256 = Hash(source) };
            typeof(UnityAdaptationRunner).GetMethod("InstallScript", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                [file, "Assets/EndfieldCharacterWorkbench/" + script.Item1 + "/" + script.Item2, script.Item3, state.ProjectB, work, Path.Combine(reports, "install"), deadline.Token]);
        }
    }
    else if (args[3] == "open-guards")
    {
        var state = JsonFiles.Read<TargetState>(statePath);
        var saved = new SavedPlan(state.First, JsonFiles.Read<ExtractionPlan>(state.First));
        string root = Path.Combine(reports, "open-guards", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string script = Path.Combine(state.ProjectB, "Assets/EndfieldCharacterValidation/Editor/UnityHoldVerification.cs");
        File.Copy(Path.Combine(Directory.GetCurrentDirectory(), "sources/verification/UnityHoldVerification.cs"), script, true);
        var start = new ProcessStartInfo(saved.Plan.Options.UnityEditor) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string a in new[] { "-projectPath", state.ProjectB, "-executeMethod", "EndfieldCharacterWorkbench.Verification.UnityHoldVerification.Begin", "-logFile", Path.Combine(root, "unity.log") }) start.ArgumentList.Add(a);
        start.Environment["ENDFIELD_ACCEPTANCE_HOLD"] = root;
        using var owned = Process.Start(start)!;
        try
        {
            using var readyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (!File.Exists(Path.Combine(root, "ready.txt"))) { if (owned.HasExited) throw new Exception("Owned editor exited before hold"); await Task.Delay(500, readyDeadline.Token); }
            string receiptPath = Path.Combine(state.ProjectB, "Library/EndfieldCharacterWorkbench/preview-open-result.json");
            string timeoutMessage = "";
            try { await new UnityPreviewLauncher(false, TimeSpan.FromSeconds(2)).OpenAsync(saved, progress, deadline.Token); throw new Exception("No-response open request was accepted"); }
            catch (TimeoutException error) { timeoutMessage = error.Message; }
            var timedOut = JsonFiles.Read<System.Text.Json.JsonElement>(receiptPath);
            Require(timedOut.GetProperty("status").GetString() == "failed" && !owned.HasExited, "No-response request closes as failed and preserves interactive editor");
            using (var stop = new CancellationTokenSource())
            {
                bool sent = false;
                var trigger = new InlineProgress<ExtractionProgress>(p => { progress.Report(p); if (p.Percent == 55) { sent = true; stop.CancelAfter(750); } });
                try { await new UnityPreviewLauncher(false).OpenAsync(saved, trigger, stop.Token); throw new Exception("Open cancellation was ignored"); }
                catch (OperationCanceledException) { Require(sent, "Cancellation after preview request sent"); }
            }
            var cancelled = JsonFiles.Read<System.Text.Json.JsonElement>(receiptPath);
            Require(cancelled.GetProperty("status").GetString() == "cancelled" && !owned.HasExited, "Cancelled open request preserves actual interactive editor");
            var release = new InlineProgress<ExtractionProgress>(p => { progress.Report(p); if (p.Percent == 55) File.WriteAllText(Path.Combine(root, "release.txt"), "release"); });
            var opened = await new UnityPreviewLauncher(false).OpenAsync(saved, release, deadline.Token);
            await owned.WaitForExitAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(40));
            var proof = JsonFiles.Read<System.Text.Json.JsonElement>(Path.Combine(root, "play-proof.json"));
            var prepared = ObjectWorkflow.ReadPreparation(saved)!;
            Require(owned.ExitCode == 0 && proof.GetProperty("passed").GetBoolean() && proof.GetProperty("scene").GetString() == prepared.UnityAssetRoot + "/Generated/CharacterPreview.unity", "Retry after timeout and cancellation reaches real Play in expected scene");
            JsonFiles.Write(Path.Combine(reports, "open-guards.json"), new { passed = true, timeoutSeconds = 2, defaultTimeoutSeconds = 180, timeoutMessage, timedOut, cancelled, opened.Message, proof, root });
        }
        finally { if (!owned.HasExited) { owned.Kill(true); await owned.WaitForExitAsync(); } }
    }
    else if (args[3] is "partial" or "partial-resume" or "partial-check")
    {
        var state = JsonFiles.Read<TargetState>(statePath);
        var saved = new SavedPlan(state.First, JsonFiles.Read<ExtractionPlan>(state.First));
        var prior = ObjectWorkflow.ReadResult(saved)!;
        var inventory = ObjectWorkflow.ReadInventory(saved, prior.InventoryPath!);
        var model = inventory.Objects.Single(row => row.IsModelCandidate && row.PathId == saved.Plan.RootPathId);
        var clips = new[] { "A_actor_chen_ui_relax_loop", "A_actor_chen_customized_state_walk_loop" }
            .Select(name => inventory.Objects.Single(row => row.Kind == "AnimationClip" && row.Name == name)).ToArray();
        var selection = new ObjectExportSelection(model.Key, model.LinkedAvatarKey, clips.Select(c => c.Key).ToList(), new AnimationPreparationOptions { ModelImportScale = 100 });
        var prepared = args[3] != "partial" ? ObjectWorkflow.ReadPreparation(saved)!
            : await new ObjectWorkflow().ExecuteAsync(saved, "prepare", prior.InventoryPath, selection, progress, deadline.Token);
        string configPath = Path.Combine(prepared.UnityPackageDirectory!, prepared.UnityAssetRoot!, "adaptation.json");
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        string source = Path.Combine(Path.GetDirectoryName(configPath)!, config["clips"]![1]!["source"]!.GetValue<string>());
        string text = File.ReadAllText(source);
        var path = new Regex(@"(?m)^(\s*path: )([^\r\n]+)$");
        Require(path.IsMatch(text), "Fault fixture has a real Transform path");
        text = path.Replace(text, "${1}AcceptanceMissingBone", 1);
        File.WriteAllText(source, text);
        string relative = Path.GetRelativePath(Path.GetDirectoryName(configPath)!, source).Replace('\\', '/');
        foreach (var input in config["inputs"]!.AsArray()) if (input!["path"]!.GetValue<string>() == relative) input["sha256"] = Hash(source);
        File.WriteAllText(configPath, config.ToJsonString(JsonFiles.Options));
        var files = prepared.Files.Select(file => Path.GetFullPath(file.Path).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)
            || Path.GetFullPath(file.Path).Equals(Path.GetFullPath(configPath), StringComparison.OrdinalIgnoreCase)
            ? file with { Bytes = new FileInfo(file.Path).Length, Sha256 = Hash(file.Path) } : file).ToList();
        prepared = prepared with { Files = files, UnityConfigSha256 = Hash(configPath) };
        string task = Path.GetDirectoryName(saved.FilePath)!;
        foreach (string output in new[] { "objects/preparation.json", "objects/result.json", "objects/" + prepared.RunId + "/operation.json" }) JsonFiles.Write(Path.Combine(task, output), prepared);
        JsonFiles.Write(Path.Combine(reports, "partial-fixture.json"), new { purpose = "Deliberately invalid path in one declared test input; not a game-data decode failure", source, relative, configPath, prepared.RunId });
        try { await new UnityAdaptationRunner().ExecuteAsync(saved, progress, deadline.Token); throw new Exception("Invalid binding was silently accepted"); }
        catch (InvalidDataException)
        {
            var summary = UnityFailureSummary.Read(saved)!;
            string expectedKey = config["clips"]![0]!["id"]!.GetValue<string>();
            Require(summary.ConvertedKeys.SequenceEqual([expectedKey]) && summary.Problems.Count == 1 && summary.Problems[0].Unmatched > 0, "Partial failure identifies exact successful clip and invalid path");
            bool rejected = false;
            try { await UnityAdaptationResults.ImportAsync(saved, deadline.Token); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Partial generation cannot be imported as complete success");
            string generated = Path.Combine(state.ProjectB, prepared.UnityAssetRoot!, "Generated");
            var snapshot = Directory.GetFiles(generated, "*", SearchOption.AllDirectories).ToDictionary(file => file, Hash);
            JsonFiles.Write(Path.Combine(reports, "partial-output-before-recovery.json"), snapshot);
            JsonFiles.Write(Path.Combine(reports, "partial-verification.json"), new { passed = true, summary, prepared.RunId, saved.FilePath, recoveryVerified = false });
        }
    }
    else if (args[3] == "generation-guards")
    {
        var state = JsonFiles.Read<TargetState>(statePath);
        var saved = new SavedPlan(state.First, JsonFiles.Read<ExtractionPlan>(state.First));
        var prior = ObjectWorkflow.ReadResult(saved)!;
        var inventory = ObjectWorkflow.ReadInventory(saved, prior.InventoryPath!);
        var model = inventory.Objects.Single(row => row.IsModelCandidate && row.PathId == saved.Plan.RootPathId);
        var clips = new[] { "A_actor_chen_ui_relax_loop", "A_actor_chen_customized_state_walk_loop" }
            .Select(name => inventory.Objects.Single(row => row.Kind == "AnimationClip" && row.Name == name)).ToArray();
        var prepared = await new ObjectWorkflow().ExecuteAsync(saved, "prepare", prior.InventoryPath,
            new ObjectExportSelection(model.Key, model.LinkedAvatarKey, clips.Select(c => c.Key).ToList(), new AnimationPreparationOptions { ModelImportScale = 100 }), progress, deadline.Token);
        var checks = new List<object>();
        var before = Process.GetProcessesByName("Unity").Select(p => p.Id).ToHashSet();
        try { await new UnityAdaptationRunner(TimeSpan.FromSeconds(2)).ExecuteAsync(saved, progress, deadline.Token); throw new Exception("Timeout was not observed"); }
        catch (TimeoutException error)
        {
            var run = UnityAdaptationRunner.ReadLatest(saved)!;
            Require(run.Status == "failed" && run.ExitCode != null, "Timed-out generation recorded with owned process exited");
            checks.Add(new { name = "shortened-real-timeout", error = error.Message, run });
        }
        using (var stop = new CancellationTokenSource())
        {
            bool afterStart = false;
            var trigger = new InlineProgress<ExtractionProgress>(p => { progress.Report(p); if (p.Percent == 55 && !afterStart) { afterStart = true; stop.Cancel(); } });
            try { await new UnityAdaptationRunner().ExecuteAsync(saved, trigger, stop.Token); throw new Exception("In-progress generation was not cancelled"); }
            catch (OperationCanceledException)
            {
                var run = UnityAdaptationRunner.ReadLatest(saved)!;
                Require(afterStart && run.Status == "cancelled" && run.ExitCode != null, "Actual Unity generation cancelled and recorded");
                checks.Add(new { name = "in-progress-cancellation", run });
            }
        }
        string fault = Path.Combine(state.ProjectB, "Assets/AcceptanceFault/Editor/Injected.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(fault)!);
        Require(!File.Exists(fault), "Fault injection file does not replace existing content");
        try
        {
            File.WriteAllText(fault, "#error AcceptanceInjectedCompileFailure\n");
            try { await new UnityAdaptationRunner().ExecuteAsync(saved, progress, deadline.Token); throw new Exception("Compile failure was accepted"); }
            catch (InvalidDataException error)
            {
                var run = UnityAdaptationRunner.ReadLatest(saved)!;
                Require(run.Status == "failed" && run.ExitCode != 0 && File.ReadAllText(run.LogPath).Contains("AcceptanceInjectedCompileFailure"), "Real script compile failure remains failed");
                checks.Add(new { name = "compile-failure", error = error.Message, run });
            }
            File.WriteAllText(fault, "using UnityEditor; [InitializeOnLoad] public static class AcceptanceEarlyExit { static AcceptanceEarlyExit() { if (System.Environment.GetEnvironmentVariable(\"ENDFIELD_ACCEPTANCE_EARLY_EXIT\") == \"1\") EditorApplication.Exit(37); } }\n");
            Environment.SetEnvironmentVariable("ENDFIELD_ACCEPTANCE_EARLY_EXIT", "1");
            try { await new UnityAdaptationRunner().ExecuteAsync(saved, progress, deadline.Token); throw new Exception("Early editor exit was accepted"); }
            catch (InvalidDataException error)
            {
                var run = UnityAdaptationRunner.ReadLatest(saved)!;
                Require(run.Status == "failed" && run.ExitCode == 37 && !File.Exists(run.ReceiptPath), "Actual early Unity exit rejected without native receipt");
                checks.Add(new { name = "early-exit", error = error.Message, run });
            }
            finally { Environment.SetEnvironmentVariable("ENDFIELD_ACCEPTANCE_EARLY_EXIT", null); }
        }
        finally { File.Delete(fault); File.Delete(fault + ".meta"); }
        var recovered = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, deadline.Token);
        Require(recovered.Result.Adapted && !recovered.Run.Reused && recovered.Run.ExitCode == 0, "Fresh generation succeeds after cancellation, timeout and faults");
        var after = Process.GetProcessesByName("Unity").Select(p => p.Id).ToHashSet();
        Require(after.IsSubsetOf(before), "No spawned Unity process remains after batch verification");
        JsonFiles.Write(Path.Combine(reports, "generation-guards.json"), new { passed = true, defaultGenerationTimeoutMinutes = 60, testedTimeoutSeconds = 2, checks, recovered, saved.FilePath });
    }
    else throw new ArgumentException("Unknown case");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    JsonFiles.Write(Path.Combine(reports, args[3] + "-failure.json"), new { passed = false, error = error.ToString() });
    return 1;
}
static void Require(bool pass, string message) { if (!pass) throw new Exception(message); Console.WriteLine("PASS " + message); }
static void Expect<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
sealed record TargetState(string First, string Second, string ProjectB, string ProjectA, string Hash1, string Hash2);
sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
