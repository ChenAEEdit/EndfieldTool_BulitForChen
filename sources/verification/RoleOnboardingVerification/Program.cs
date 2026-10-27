using System.Text.Json;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

if (args.Length != 3) { Console.Error.WriteLine("Usage: <tool package> <experiment case.json> <report directory>"); return 2; }
string package = Path.GetFullPath(args[0]), casePath = Path.GetFullPath(args[1]), reports = Path.GetFullPath(args[2]);
Directory.CreateDirectory(reports);
AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", package + Path.DirectorySeparatorChar);
var caseFile = JsonFiles.Read<JsonElement>(casePath);
string role = caseFile.GetProperty("role").GetString()!;
if (role is not ("ardelia" or "deepfin")) throw new InvalidDataException("This onboarding acceptance is limited to ardelia and deepfin.");
string planPath = caseFile.GetProperty("filePath").GetString()!;
var saved = UnityTaskTarget.Resolve(new SavedPlan(planPath, JsonFiles.Read<ExtractionPlan>(planPath)));
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(50));
var progress = new Progress<ExtractionProgress>(row => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {row.Percent}% {row.Message}"));
var workflow = new ObjectWorkflow();
var inspected = await workflow.ExecuteAsync(saved, "inspect", null, null, progress, stop.Token);
var inventory = ObjectWorkflow.ReadInventory(saved, inspected.InventoryPath!);
var association = CharacterAssociationResolver.Resolve(inventory, role);
var evidence = new Dictionary<string, object?> { ["role"] = role, ["inventoryPath"] = inspected.InventoryPath,
    ["association"] = association, ["strictDefaultRejectedPartialTracks"] = false, ["partialOptInGenerated"] = false };

async Task<(ObjectOperationResult Prepared, UnityGenerationOutcome? Generated)> Generate(bool dropUnmatched, string runName)
{
    var selection = new ObjectExportSelection(association.Model.Key, association.Avatar.Key,
        association.Clips.Select(row => row.Key).ToList(), new AnimationPreparationOptions {
            ModelImportScale = 1, PositionScale = 1, InferUnits = true,
            DropUnmatched = dropUnmatched, DropSceneTranslation = true, CreatePreviewScene = true
        });
    var prepared = await workflow.ExecuteAsync(saved, "prepare", inspected.InventoryPath, selection, progress, stop.Token);
    if (prepared.Status != "unity_prepared") throw new InvalidOperationException(runName + " package preparation failed: " + prepared.Message);
    try
    {
        var generated = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, stop.Token);
        return (prepared, generated);
    }
    catch (Exception error)
    {
        evidence[runName + "Error"] = error.ToString();
        return (prepared, null);
    }
}

var strictRun = await Generate(dropUnmatched: false, "strictDefault");
evidence["strictDefaultRejectedPartialTracks"] = strictRun.Generated == null;
if (strictRun.Generated != null)
{
    var strictReport = JsonFiles.Read<JsonElement>(strictRun.Generated.Result.UnityReportPath!);
    int strictOmissions = strictReport.GetProperty("clips").EnumerateArray().Sum(clip => clip.GetProperty("tracks").EnumerateArray()
        .Count(track => track.GetProperty("decision").GetString() == "omit"));
    if (strictOmissions != 0) throw new InvalidOperationException("Strict default generated while omitting tracks.");
    evidence["strictDefaultHadNoOmissions"] = true;
}
var optedIn = await Generate(dropUnmatched: true, "partialOptIn");
if (optedIn.Generated == null || !optedIn.Generated.Result.Adapted || optedIn.Generated.Result.PlaybackVerified
    || optedIn.Generated.Result.UnityPreviewScene == null || !File.Exists(optedIn.Generated.Result.UnityPreviewScene))
    throw new InvalidOperationException("Explicit partial adaptation did not generate a verified package and preview scene.");
evidence["partialOptInGenerated"] = true;
evidence["preparedRunId"] = optedIn.Prepared.RunId;
evidence["generated"] = optedIn.Generated;
var verifiedReport = JsonFiles.Read<JsonElement>(optedIn.Generated.Result.UnityReportPath!);
var units = verifiedReport.GetProperty("unitInference");
if (!units.GetProperty("passed").GetBoolean()) throw new InvalidDataException("Unit inference did not pass.");
float modelScale = units.GetProperty("modelImportScale").GetSingle();
float positionScale = units.GetProperty("sharedPositionScale").GetSingle();
int modelSegments = units.GetProperty("measuredModelSegments").GetInt32();
int animationCount = units.GetProperty("clips").GetArrayLength();
if (Math.Abs(modelScale - 100) > .01f || Math.Abs(positionScale - 1) > .01f || modelSegments < 100 || animationCount != 2)
    throw new InvalidDataException($"Inferred units differ from experiment baseline: model={modelScale}, position={positionScale}, segments={modelSegments}, clips={animationCount}.");
int trackCount = verifiedReport.GetProperty("clips").EnumerateArray().Sum(clip => clip.GetProperty("tracks").GetArrayLength());
int omitted = verifiedReport.GetProperty("clips").EnumerateArray().Sum(clip => clip.GetProperty("tracks").EnumerateArray()
    .Count(track => track.GetProperty("decision").GetString() == "omit"));
if (trackCount <= 0 || omitted <= 0 || verifiedReport.GetProperty("clips").EnumerateArray()
    .SelectMany(clip => clip.GetProperty("tracks").EnumerateArray())
    .Any(track => track.GetProperty("decision").GetString() == "omit" && string.IsNullOrWhiteSpace(track.GetProperty("reason").GetString())))
    throw new InvalidDataException("Explicit omissions are missing from the per-track audit.");
evidence["unitInference"] = units;
evidence["trackAudit"] = new { total = trackCount, omitted, allOmissionsHaveReasons = true };
evidence["playbackVerified"] = false;
JsonFiles.Write(Path.Combine(reports, "formal-integration.json"), evidence);
Console.WriteLine($"PASS {role}: association, strict-default rejection, explicit partial generation, unit evidence and {trackCount} audited tracks.");
return 0;
