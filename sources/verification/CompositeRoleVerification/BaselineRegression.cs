using System.Text.Json;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

internal static class BaselineRegression
{
    public static async Task RunAsync(string baselinePath, string reports, string project, IProgress<ExtractionProgress> progress, CancellationToken token)
    {
        var baseline = JsonFiles.Read<ExtractionPlan>(baselinePath);
        var saved = new WorkspaceStore().Save(baseline with { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.Now,
            Options = baseline.Options with { WorkspaceDirectory = Path.Combine(reports, "workspace"), UnityProject = project } });
        File.WriteAllText(Path.Combine(reports, "plan-path.txt"), saved.FilePath);
        await new BundleExtractionBackend().ExecuteAsync(saved, progress, token);
        var workflow = new ObjectWorkflow();
        var inspected = await workflow.ExecuteAsync(saved, "inspect", null, null, progress, token);
        var association = CharacterAssociationResolver.Resolve(ObjectWorkflow.ReadInventory(saved, inspected.InventoryPath!), baseline.CharacterId);
        var selection = new ObjectExportSelection(association.Model.Key, association.Avatar.Key, association.Clips.Select(c => c.Key).ToList(),
            new AnimationPreparationOptions { InferUnits = true, DropUnmatched = false, DropSceneTranslation = false, CreatePreviewScene = true });
        var prepared = await workflow.ExecuteAsync(saved, "prepare", inspected.InventoryPath, selection, progress, token);
        string? error = null;
        try { await new UnityAdaptationRunner().ExecuteAsync(saved, progress, token); }
        catch (Exception ex) { error = ex.Message; }
        string native = Path.Combine(project, prepared.UnityAssetRoot!, "Generated/adaptation-result.json");
        var report = JsonFiles.Read<JsonElement>(native);
        var clips = report.GetProperty("clips").EnumerateArray().ToArray();
        int tracks = clips.Sum(c => c.GetProperty("tracks").GetArrayLength());
        bool passed = report.GetProperty("configSha256").GetString() == prepared.UnityConfigSha256
            && report.GetProperty("unitInference").GetProperty("passed").GetBoolean() && clips.Length == 2 && tracks > 0
            && report.GetProperty("diagnostics").GetProperty("lodGroups").GetInt32() > 0
            && clips.SelectMany(c => c.GetProperty("tracks").EnumerateArray()).Any(t => t.GetProperty("decision").GetString() == "reject")
            && !report.GetProperty("adapted").GetBoolean() && error != null;
        JsonFiles.Write(Path.Combine(reports, "baseline-regression.json"), new { passed, role = baseline.CharacterId, saved.FilePath,
            inspected.InventoryPath, prepared.RunId, reportPath = native, tracks, error,
            unitInference = report.GetProperty("unitInference"), partialOptIn = false });
        if (!passed) throw new InvalidDataException("Single-model strict-mode regression failed; inspect native report.");
        Console.WriteLine($"PASS {baseline.CharacterId}: real model assembly and unit inference; strict mode preserves rejection of {tracks} audited tracks.");
    }
}
