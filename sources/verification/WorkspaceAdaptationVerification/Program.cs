using System.Security.Cryptography;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

if (args.Length is not (3 or 4)) { Console.Error.WriteLine("Usage: <tool directory> <plan.json> <report directory> [--guards]"); return 2; }
string package = Path.GetFullPath(args[0]), planPath = Path.GetFullPath(args[1]), reports = Path.GetFullPath(args[2]);
Directory.CreateDirectory(reports);
AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", package + Path.DirectorySeparatorChar);
var saved = new SavedPlan(planPath, JsonFiles.Read<ExtractionPlan>(planPath));
string? deadline = Environment.GetEnvironmentVariable("ENDFIELD_VALIDATION_DEADLINE");
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(.1,
    Math.Min(20, deadline == null ? 20 : (DateTimeOffset.Parse(deadline) - DateTimeOffset.Now).TotalMinutes))));
var progress = new Progress<ExtractionProgress>(row => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {row.Percent}% {row.Message}"));
ObjectOperationResult? prepared = null;
UnityGenerationOutcome? generated = null, reused = null;
string? error = null;
var before = Snapshot();
JsonFiles.Write(Path.Combine(reports, "protected-before.json"), before);
try
{
    if (args.Length == 4)
    {
        var preparation = ObjectWorkflow.ReadPreparation(saved)!;
        var checks = new List<string>();
        string metadata = preparation.UnityPreviewScene + ".meta";
        byte[] original = File.ReadAllBytes(metadata);
        try
        {
            File.AppendAllText(metadata, "\n# guard verification\n");
            try { await UnityAdaptationResults.ImportAsync(saved, stop.Token); throw new Exception("Modified metadata was accepted."); }
            catch (InvalidDataException) { checks.Add("Modified asset metadata rejected"); }
        }
        finally { File.WriteAllBytes(metadata, original); }
        await UnityAdaptationResults.ImportAsync(saved, stop.Token); checks.Add("Exact metadata restoration accepted");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await new UnityAdaptationRunner().ExecuteAsync(saved, progress, cancelled.Token); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException) { checks.Add("Cancelled start creates no Unity process"); }
        }
        using (var busy = new FileStream(Path.Combine(saved.Plan.Options.UnityProject, "Library/EndfieldCharacterWorkbench/import.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            try { await new UnityAdaptationRunner().ExecuteAsync(saved, progress, stop.Token); throw new Exception("Busy project was accepted."); }
            catch (IOException) { checks.Add("Concurrent project import rejected"); }
        }
        reused = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, stop.Token);
        if (!reused.Run.Reused) throw new Exception("Valid generation not reusable after guard checks.");
        JsonFiles.Write(Path.Combine(reports, "guards.json"), new { passed = true, checks, reused });
    }
    else
    {
    var prior = ObjectWorkflow.ReadResult(saved) ?? throw new Exception("Missing inventory.");
    var inventory = ObjectWorkflow.ReadInventory(saved, prior.InventoryPath!);
    var model = inventory.Objects.Single(row => row.IsModelCandidate && row.PathId == saved.Plan.RootPathId);
    var clips = inventory.Objects.Where(row => row.Kind == "AnimationClip" && (row.Name == "A_actor_chen_ui_overview_loop" || row.Name == "A_actor_chen_ui_relax_loop")).ToArray();
    if (clips.Length != 2) throw new Exception("Two idle clips are required.");
    var selection = new ObjectExportSelection(model.Key, model.LinkedAvatarKey ?? throw new Exception("No linked Avatar."), clips.Select(clip => clip.Key).ToList(),
        new AnimationPreparationOptions { ModelImportScale = 100, CreatePreviewScene = true });
    prepared = await new ObjectWorkflow().ExecuteAsync(saved, "prepare", prior.InventoryPath, selection, progress, stop.Token);
    if (prepared.Status != "unity_prepared") throw new Exception("Preparation did not complete.");
    JsonFiles.Write(Path.Combine(reports, "prepared.json"), prepared);
    generated = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, stop.Token);
    if (!generated.Result.Adapted || generated.Result.PlaybackVerified || generated.Run.Reused
        || generated.Result.UnityPreviewScene == null || !File.Exists(generated.Result.UnityPreviewScene))
        throw new Exception("Fresh generation identity or scene failed.");
    JsonFiles.Write(Path.Combine(reports, "generated.json"), generated);
    reused = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, stop.Token);
    if (!reused.Run.Reused || reused.Result.RunId != generated.Result.RunId) throw new Exception("Reuse failed.");
    Console.WriteLine("PASS preparation, fresh native generation, strict result import and verified reuse.");
    }
}
catch (Exception ex) { error = ex.ToString(); Console.Error.WriteLine(error); }
var after = Snapshot();
var changed = before.Where(pair => !after.TryGetValue(pair.Key, out var hash) || hash != pair.Value).Select(pair => pair.Key).ToArray();
JsonFiles.Write(Path.Combine(reports, "protected-after.json"), new { files = after, changed });
if (changed.Length > 0) error = (error ?? "") + " Protected files changed: " + string.Join(", ", changed);
JsonFiles.Write(Path.Combine(reports, "verification.json"), new { passed = error == null, error, prepared, generated, reused,
    protectedFileCount = before.Count, protectedFilesChanged = changed, playbackVerified = false });
return error == null ? 0 : 1;

Dictionary<string, string> Snapshot()
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (string folder in new[] { "EndfieldChenTool", "ChenIdle", "ChenReextracted" })
    {
        string root = Path.Combine(saved.Plan.Options.UnityProject, "Assets", folder);
        if (!Directory.Exists(root)) continue;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        { using var stream = File.OpenRead(file); result[file] = Convert.ToHexString(SHA256.HashData(stream)); }
    }
    return result;
}
