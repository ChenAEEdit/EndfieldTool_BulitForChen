using System.Text.Json;
using System.Diagnostics;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

if (args.Length == 2 && args[0] == "guards")
{
    AssociationGuards.Run(args[1]);
    return;
}

if (args.Length > 2 && args[0] == "shape")
{
    string runtime = Path.GetFullPath(args[1]);
    System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
        File.Exists(Path.Combine(runtime, name.Name + ".dll")) ? System.Reflection.Assembly.LoadFrom(Path.Combine(runtime, name.Name + ".dll")) : null;
    var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(runtime, "AnimeStudio.dll"));
    foreach (string name in args.Skip(2))
    {
        var type = assembly.GetType("AnimeStudio." + name)!;
        Console.WriteLine(name + " FIELDS: " + string.Join(", ", type.GetFields().Select(f => f.Name + ":" + f.FieldType.Name)));
        Console.WriteLine(name + " METHODS: " + string.Join(", ", type.GetMethods().Where(m => m.DeclaringType == type).Select(m => m.ToString())));
    }
    return;
}

if (args.Length < 7) throw new ArgumentException("<package> <catalog> <baseline-plan> <reports> <project> <discover|inspect> <role...>");
string package = Path.GetFullPath(args[0]), reports = Path.GetFullPath(args[3]), project = Path.GetFullPath(args[4]);
if (!project.StartsWith(@"D:\AAAChatGPT\UnityValidation\", StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("An independent validation project is required.");
Directory.CreateDirectory(reports);
AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", package + Path.DirectorySeparatorChar);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(60));
var progress = new ProgressSink(Path.Combine(reports, "discovery.log"));
var workflow = new ObjectWorkflow();
int incomplete = 0;
foreach (string role in args.Skip(6))
{
    string directory = Path.Combine(reports, role);
    Directory.CreateDirectory(directory);
    try
    {
        if (args[5] == "regression")
        {
            await BaselineRegression.RunAsync(args[2], directory, project, progress, deadline.Token);
            continue;
        }
        if (args[5] == "adapt")
        {
            var discovery = JsonFiles.Read<JsonElement>(Path.Combine(directory, "discovery.json"));
            string planPath = discovery.GetProperty("filePath").GetString()!;
            var savedCase = new SavedPlan(planPath, JsonFiles.Read<ExtractionPlan>(planPath));
            string inventoryPath = discovery.GetProperty("inventoryPath").GetString()!;
            var inv = ObjectWorkflow.ReadInventory(savedCase, inventoryPath);
            var association = CharacterAssociationResolver.Resolve(inv, role, includeSpecialIdle: true);
            JsonFiles.Write(Path.Combine(directory, "association.json"), new { role, association, inventoryPath });
            if (!Directory.Exists(project))
            {
                var start = new ProcessStartInfo(savedCase.Plan.Options.UnityEditor) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (string value in new[] { "-batchmode", "-quit", "-nographics", "-createProject", project, "-logFile", Path.Combine(reports, "create-project.log") }) start.ArgumentList.Add(value);
                using var editor = Process.Start(start)!;
                await editor.WaitForExitAsync(deadline.Token);
                if (editor.ExitCode != 0) throw new InvalidDataException("Independent Unity project creation failed.");
            }
            var selection = new ObjectExportSelection(association.Model.Key, association.Avatar.Key, association.Clips.Select(c => c.Key).ToList(),
                new AnimationPreparationOptions { InferUnits = true, DropUnmatched = false, DropSceneTranslation = false, CreatePreviewScene = true })
                { RelatedModelKeys = association.RelatedModels.Select(m => m.Key).ToList() };
            var prepared = await workflow.ExecuteAsync(savedCase, "prepare", inventoryPath, selection, progress, deadline.Token);
            JsonFiles.Write(Path.Combine(directory, "prepared.json"), prepared);
            var generated = await new UnityAdaptationRunner().ExecuteAsync(savedCase, progress, deadline.Token);
            JsonFiles.Write(Path.Combine(directory, "generated.json"), generated);
            Console.WriteLine("GENERATED " + role + "; actual Play still required.");
            continue;
        }
        SavedPlan saved;
        if (args[5] == "discover")
        {
            var catalog = new JsonCatalogProvider().Load(args[1]);
            var baseline = JsonFiles.Read<ExtractionPlan>(args[2]);
            var character = catalog.Characters.Single(c => c.Id == role);
            // Include animation entry points even when plaintext model discovery has no result.
            saved = new WorkspaceStore().Save(new TaskPlanner().Create(catalog, character,
                character.Resources.Select(r => r.Id).ToArray(), baseline.Options with { UnityProject = project }));
            File.WriteAllText(Path.Combine(directory, "plan-path.txt"), saved.FilePath);
            await new BundleExtractionBackend().ExecuteAsync(saved, progress, deadline.Token);
        }
        else
        {
            string path = File.ReadAllText(Path.Combine(directory, "plan-path.txt"));
            saved = new(path, JsonFiles.Read<ExtractionPlan>(path));
        }
        var inspected = args[5] == "expand"
            ? await workflow.ExpandModelReferencesAsync(saved, JsonFiles.Read<JsonElement>(Path.Combine(directory, "discovery.json")).GetProperty("inventoryPath").GetString()!, progress, deadline.Token)
            : args[5] == "reinspect"
            ? await workflow.ReinspectAsync(saved, JsonFiles.Read<JsonElement>(Path.Combine(directory, "discovery.json")).GetProperty("inventoryPath").GetString()!, progress, deadline.Token)
            : await workflow.ExecuteAsync(saved, "inspect", null, null, progress, deadline.Token);
        var inventory = ObjectWorkflow.ReadInventory(saved, inspected.InventoryPath!);
        JsonFiles.Write(Path.Combine(directory, "discovery.json"), new {
            role, saved.FilePath, inspected.InventoryPath, count = inventory.Objects.Count,
            models = inventory.Objects.Where(o => o.IsModelCandidate),
            avatars = inventory.Objects.Where(o => o.Kind == "Avatar"),
            clips = inventory.Objects.Where(o => o.Kind == "AnimationClip")
        });
        Console.WriteLine($"DISCOVERED {role}: {inventory.Objects.Count} objects; {inspected.InventoryPath}");
    }
    catch (Exception error)
    {
        incomplete++;
        JsonFiles.Write(Path.Combine(directory, "failure.json"), new { role, stage = args[5], error = error.ToString() });
        Console.WriteLine($"STOPPED {role}: {error.Message}");
    }
}
Environment.ExitCode = incomplete == 0 ? 0 : 1;

sealed class ProgressSink(string path) : IProgress<ExtractionProgress>
{
    public void Report(ExtractionProgress value) => File.AppendAllText(path, $"{DateTimeOffset.Now:O} {value.Percent} {value.Message}\n");
}
