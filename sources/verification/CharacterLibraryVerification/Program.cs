using System.Text.Json;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

string package = Path.GetFullPath(args[0]), reports = Path.GetFullPath(args[1]);
AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", package + Path.DirectorySeparatorChar);
var plans = JsonFiles.Read<JsonElement>(Path.Combine(reports, "fixture-plans.json")).EnumerateArray().ToArray();
var checks = new List<string>();
var logLock = new object();
string logPath = Path.Combine(reports, args.Length > 2 && args[2] == "cache" ? "cache-workflow.log" : "runner.log");
var progress = new Progress<ExtractionProgress>(row => { lock (logLock) File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss ") + row.Message + Environment.NewLine); });
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(20));
var runner = new UnityAdaptationRunner();
SavedPlan Load(int index) { string p = plans[index].GetProperty("path").GetString()!; return new(p, JsonFiles.Read<ExtractionPlan>(p)); }
if (args.Length > 3 && args[2] == "cache")
{
    var baseline = JsonFiles.Read<JsonElement>(args[3]);
    string planPath = baseline.GetProperty("filePath").GetString()!, inventoryPath = baseline.GetProperty("inventoryPath").GetString()!;
    var saved = new SavedPlan(planPath, JsonFiles.Read<ExtractionPlan>(planPath));
    var association = CharacterAssociationResolver.Resolve(ObjectWorkflow.ReadInventory(saved, inventoryPath), saved.Plan.CharacterId);
    var runs = new List<ObjectOperationResult>(); var roots = new List<string>();
    foreach (var clip in association.Clips.Take(2))
    {
        var selection = new ObjectExportSelection(association.Model.Key, association.Avatar.Key, [clip.Key], new AnimationPreparationOptions { InferUnits = true, DropUnmatched = false, DropSceneTranslation = false, CreatePreviewScene = true });
        var result = await new ObjectWorkflow().ExecuteAsync(saved, "prepare", inventoryPath, selection, progress, stop.Token);
        runs.Add(result);
        roots.Add(JsonFiles.Read<JsonElement>(Path.Combine(result.UnityPackageDirectory!, result.UnityAssetRoot!, "adaptation.json")).GetProperty("libraryRoot").GetString()!);
    }
    if (runs.Count != 2 || roots[0] != roots[1] || runs[1].Extra?["modelExportReused"].GetBoolean() != true) throw new Exception("Actual export cache was not reused across motion selections.");
    JsonFiles.Write(Path.Combine(reports, "cache-workflow-verification.json"), new { passed = true, firstRun = runs[0].RunId, secondRun = runs[1].RunId,
        secondModelExportReused = true, libraryRoot = roots[0], sourcePlan = planPath, sourceInventory = inventoryPath });
    Console.WriteLine("PASS actual export cache across two different source-animation selections.");
    return;
}
if (args.Length > 2 && args[2] == "open")
{
    var opened = await new UnityPreviewLauncher(showEditor: false).OpenAsync(Load(0), progress, stop.Token);
    JsonFiles.Write(Path.Combine(reports, "open-verification.json"), new { passed = true, processId = Environment.ProcessId, opened.Message, opened.Result.UnityPreviewScene });
    Console.WriteLine("PASS desktop cold-open request and shared-preview receipt.");
    return;
}
var first = await runner.ExecuteAsync(Load(0), progress, stop.Token);
checks.Add("first motion pack imported through desktop runner");
string project = Load(0).Plan.Options.UnityProject;
var config = JsonFiles.Read<JsonElement>(Path.Combine(project, first.Result.UnityAssetRoot!, "adaptation.json"));
string library = config.GetProperty("libraryRoot").GetString()!;
string fbx = Directory.GetFiles(Path.Combine(project, library, "Model"), "*.fbx").Single();
string meta = fbx + ".meta", before = File.ReadAllText(meta), receipt = File.ReadAllText(Path.Combine(project, library, "Generated/library.json"));
var second = await runner.ExecuteAsync(Load(1), progress, stop.Token);
if (File.ReadAllText(meta) != before || File.ReadAllText(Path.Combine(project, library, "Generated/library.json")) != receipt) throw new Exception("Shared model or GUID changed.");
checks.Add("second task reuses model files, GUID, settings and immutable receipt");
if (Directory.GetFiles(Path.Combine(project, "Assets/EndfieldCharacterLibrary"), "*.fbx", SearchOption.AllDirectories).Length != 1
    || Directory.GetFiles(Path.Combine(project, "Assets/EndfieldCharacterExports"), "*.fbx", SearchOption.AllDirectories).Length != 0)
    throw new Exception("Model duplicated across motion packs.");
checks.Add("one FBX in character library and none in motion-pack folders");
var reused = await runner.ExecuteAsync(Load(0), progress, stop.Token);
if (!reused.Run.Reused) throw new Exception("Repeated run regenerated outputs.");
checks.Add("earlier motion pack remains verifiable and reuses outputs after append");
bool refused = false;
try { await runner.ExecuteAsync(Load(2), progress, stop.Token); }
catch (InvalidDataException) { refused = true; }
if (!refused) throw new Exception("Unsupported required track was accepted.");
checks.Add("strict invalid motion fails without replacing shared character");
try
{
    File.AppendAllText(meta, "\n# fixture edit\n");
    refused = false;
    try { await UnityAdaptationResults.ImportAsync(Load(0), stop.Token); } catch (InvalidDataException) { refused = true; }
    if (!refused) throw new Exception("Edited model importer settings accepted.");
    checks.Add("modified shared importer metadata refused without overwrite");
}
finally { File.WriteAllText(meta, before, new System.Text.UTF8Encoding(false)); }
await UnityAdaptationResults.ImportAsync(Load(0), stop.Token);
JsonFiles.Write(Path.Combine(reports, "runner-verification.json"), new { passed = true, checks, libraryRoot = library,
    firstConfig = first.Result.UnityAssetRoot + "/adaptation.json", secondConfig = second.Result.UnityAssetRoot + "/adaptation.json" });
Console.WriteLine($"PASS {checks.Count} desktop import/library checks.");
