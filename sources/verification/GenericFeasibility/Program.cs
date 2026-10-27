using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

// Experimental harness only. No role-specific path IDs, bundles, rig choices or scales.
if (args.Length < 6) throw new ArgumentException("<package> <catalog> <baseline-plan> <reports> <project> <discover|generate> [role...]");
string package = Path.GetFullPath(args[0]), reports = Path.GetFullPath(args[3]), project = Path.GetFullPath(args[4]);
if (!project.StartsWith(@"D:\AAAChatGPT\UnityValidation\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Use an isolated validation project.");
Directory.CreateDirectory(reports);
AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", package + Path.DirectorySeparatorChar);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(40));
var progress = new ProgressSink(reports);
var store = new WorkspaceStore();
var workflow = new ObjectWorkflow();
if (args[5] == "parser-shape")
{
    var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(package, "runtime/object-probe/AnimeStudio.dll"));
    foreach (var name in new[] { "Animator", "AnimatorController", "AnimatorOverrideController", "RuntimeAnimatorController" })
    {
        var type = assembly.GetType("AnimeStudio." + name);
        Console.WriteLine(name + ": " + string.Join(", ", type?.GetFields().Select(f => f.Name + "=" + f.FieldType.Name) ?? []));
    }
}
else if (args[5] is "associate" or "association-guards")
{
    var checks = new List<object>();
    foreach (string role in args.Skip(6))
    {
        string path = Path.Combine(reports, "controller-evidence", role, "inventory.json");
        var inventory = JsonFiles.Read<JsonElement>(path);
        var resolved = AssociationResolver.Resolve(inventory, role);
        JsonFiles.Write(Path.Combine(reports, role, "association.json"), new { passed = true, role, inventoryPath = path, association = resolved });
        if (args[5] == "associate") continue;
        void Reject(string name, Action<JsonArray> mutate)
        {
            var copy = JsonNode.Parse(inventory.GetRawText())!;
            mutate(copy["objects"]!.AsArray());
            try { AssociationResolver.Resolve(JsonSerializer.SerializeToElement(copy), role); }
            catch (InvalidDataException error) { checks.Add(new { role, name, rejected = true, reason = error.Message }); return; }
            throw new Exception("Unsafe automatic selection accepted: " + name);
        }
        JsonNode Model(JsonArray a) => a.Single(o => o!["key"]!.GetValue<string>() == resolved.ModelKey)!;
        Reject("duplicate-independent-model", a => { var n = Model(a).DeepClone(); n["key"] = "duplicate-model"; a.Add(n); });
        Reject("missing-avatar", a => a.Remove(a.Single(o => o!["key"]!.GetValue<string>() == resolved.AvatarKey)));
        Reject("missing-controller", a => Model(a)["linkedControllerKey"] = null);
        Reject("missing-controller-clip", a => Model(a)["controllerClipKeys"]!.AsArray().Add("absent-clip"));
        Reject("contradictory-parent", a => Model(a)["parentGameObjectKey"] = "unexpected-parent");
        Reject("unproven-independent-root", a => Model(a)["hierarchyRoot"] = false);
        Reject("duplicate-idle", a => { var n = a.Single(o => o!["key"]!.GetValue<string>() == resolved.ClipKeys[0])!.DeepClone(); n["key"] = "duplicate-clip"; a.Add(n); Model(a)["controllerClipKeys"]!.AsArray().Add("duplicate-clip"); });
        var anonymous = JsonNode.Parse(inventory.GetRawText())!;
        var rows = anonymous["objects"]!.AsArray();
        foreach (var node in rows) if (node!["name"] is JsonValue name && name.TryGetValue<string>(out var text)) node["name"] = text.Replace(role, "unseen_sample", StringComparison.Ordinal);
        var permuted = rows.Reverse().Select(n => n!.DeepClone()).ToArray();
        anonymous["objects"] = new JsonArray(permuted);
        var again = AssociationResolver.Resolve(JsonSerializer.SerializeToElement(anonymous), "unseen_sample");
        if (again.ModelKey != resolved.ModelKey || again.AvatarKey != resolved.AvatarKey || !again.ClipKeys.Order().SequenceEqual(resolved.ClipKeys.Order())) throw new Exception("Role name/order dependence.");
        checks.Add(new { role, name = "anonymous-role-and-reversed-inventory", passed = true });
    }
    JsonFiles.Write(Path.Combine(reports, args[5] + ".json"), new { passed = true, checks });
}
else if (args[5] is "discover" or "resume")
{
    var catalog = new JsonCatalogProvider().Load(args[1]);
    var baseline = JsonFiles.Read<ExtractionPlan>(args[2]);
    int incomplete = 0;
    foreach (string role in args.Skip(6))
    {
        if (!Regex.IsMatch(role, "^[a-z0-9_]+$")) throw new Exception("Invalid sample role.");
        string directory = Path.Combine(reports, role);
        Directory.CreateDirectory(directory);
        try
        {
            var character = catalog.Characters.Single(c => c.Id == role);
            var resources = character.Resources.Where(r => r.Kind == ResourceKind.Model).ToArray();
            if (resources.Length == 0) throw new Exception("No model name candidates in catalog; automatic discovery is incomplete.");
            var options = baseline.Options with { WorkspaceDirectory = Path.Combine(directory, "workspace"), UnityProject = project };
            var marker = Path.Combine(directory, "plan-path.txt");
            var saved = args[5] == "resume" ? new SavedPlan(File.ReadAllText(marker), JsonFiles.Read<ExtractionPlan>(File.ReadAllText(marker)))
                : store.Save(new TaskPlanner().Create(catalog, character, resources.Select(r => r.Id).ToArray(), options));
            File.WriteAllText(Path.Combine(directory, "plan-path.txt"), saved.FilePath);
            if (args[5] != "resume") await new BundleExtractionBackend().ExecuteAsync(saved, progress, deadline.Token);
            var inspected = await workflow.ExecuteAsync(saved, "inspect", null, null, progress, deadline.Token);
            var inventory = ObjectWorkflow.ReadInventory(saved, inspected.InventoryPath!);
            var raw = JsonFiles.Read<JsonElement>(inspected.InventoryPath!);
            var resolved = AssociationResolver.Resolve(raw, role);
            var model = inventory.Objects.Single(o => o.Key == resolved.ModelKey);
            var avatar = inventory.Objects.Single(o => o.Key == resolved.AvatarKey);
            var chosen = resolved.ClipKeys.Select(key => inventory.Objects.Single(o => o.Key == key)).ToList();
            var selection = new ObjectExportSelection(model.Key, avatar.Key, chosen.Select(c => c.Key).ToList());
            var exported = await workflow.ExecuteAsync(saved, "export", inspected.InventoryPath, selection, progress, deadline.Token);
            if (exported.Failures.Count != 0) throw new Exception("Export failed.");
            JsonFiles.Write(Path.Combine(directory, "case.json"), new { role, saved.FilePath, inspected.InventoryPath, model, avatar, clips = chosen, exported,
                modelSelectionEvidence = "unique standalone hierarchy root among actual skinned role model candidates; nested scene instances excluded", avatarAssociation = "actual model Animator reference",
                clipAssociation = resolved.Evidence, controllerAssociationConfirmed = true, sourceRestPoseOwnershipProven = false });
            Console.WriteLine("DISCOVERED " + role + " actual objects=" + inventory.Objects.Count);
        }
        catch (Exception error)
        {
            incomplete++;
            JsonFiles.Write(Path.Combine(directory, "failure.json"), new { role, stage = "discovery", error = error.ToString() });
            Console.WriteLine("INCOMPLETE " + role + ": " + error.Message);
        }
    }
    if (incomplete > 0) return 1;
}
else if (args[5] == "generate")
{
    var units = JsonFiles.Read<JsonElement>(Path.Combine(reports, "unit-verification.json"));
    if (!units.GetProperty("passed").GetBoolean()) throw new Exception("Unit inference has not passed.");
    foreach (var unit in units.GetProperty("cases").EnumerateArray())
    {
        string role = unit.GetProperty("role").GetString()!;
        string directory = Path.Combine(reports, role);
        var row = JsonFiles.Read<JsonElement>(Path.Combine(directory, "case.json"));
        var associated = JsonFiles.Read<JsonElement>(Path.Combine(directory, "association.json"));
        var resolution = associated.GetProperty("association");
        if (!associated.GetProperty("passed").GetBoolean()
            || resolution.GetProperty("modelKey").GetString() != row.GetProperty("model").GetProperty("key").GetString()
            || resolution.GetProperty("avatarKey").GetString() != row.GetProperty("avatar").GetProperty("key").GetString()
            || !resolution.GetProperty("clipKeys").EnumerateArray().Select(c => c.GetString()).Order().SequenceEqual(row.GetProperty("clips").EnumerateArray().Select(c => c.GetProperty("key").GetString()).Order()))
            throw new Exception("Controller association differs from exported snapshot.");
        string planPath = row.GetProperty("filePath").GetString()!;
        var saved = new SavedPlan(planPath, JsonFiles.Read<ExtractionPlan>(planPath));
        string inventoryPath = row.GetProperty("inventoryPath").GetString()!;
        var positionScales = unit.GetProperty("animations").EnumerateArray().Select(a => {
            if (a.GetProperty("status").GetString() != "measured") throw new Exception("Position scale is not measured.");
            return a.GetProperty("positionScale").GetDecimal(); }).ToArray();
        if (positionScales.Length < 1 || positionScales.Max() / positionScales.Min() > 1.01m) throw new Exception("Inconsistent per-clip position units.");
        decimal positionScale = positionScales.Order().ElementAt(positionScales.Length / 2);
        if (Math.Abs(positionScale - 1) < .0001m) positionScale = 1;
        var selection = new ObjectExportSelection(row.GetProperty("model").GetProperty("key").GetString(), row.GetProperty("avatar").GetProperty("key").GetString(),
            row.GetProperty("clips").EnumerateArray().Select(c => c.GetProperty("key").GetString()!).ToList(),
            new AnimationPreparationOptions { ModelImportScale = unit.GetProperty("modelImportScale").GetDecimal(), PositionScale = positionScale, DropUnmatched = true });
        // Explicit experiment policy permits omissions; this is not the production strict default.
        var prepared = await workflow.ExecuteAsync(saved, "prepare", inventoryPath, selection, progress, deadline.Token);
        var generated = await new UnityAdaptationRunner().ExecuteAsync(saved, progress, deadline.Token);
        JsonFiles.Write(Path.Combine(directory, "generation.json"), new { experimental = true, controllerAssociationConfirmed = true, sourceRestPoseOwnershipProven = false,
            passed = generated.Result.Adapted, prepared, generated, scaleEvidence = unit, associated, playbackVerified = false });
        if (!generated.Result.Adapted) throw new Exception("Generation incomplete: " + role);
    }
}
else throw new Exception("Unknown mode.");
return 0;

sealed class ProgressSink(string reports) : IProgress<ExtractionProgress>
{
    int prior = -1;
    public void Report(ExtractionProgress p)
    {
        File.AppendAllText(Path.Combine(reports, "progress.log"), $"{DateTime.Now:HH:mm:ss} {p.Percent}% {p.Message}\n");
        if (p.Percent != prior) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {p.Percent}% {p.Message}");
        prior = p.Percent;
    }
}
