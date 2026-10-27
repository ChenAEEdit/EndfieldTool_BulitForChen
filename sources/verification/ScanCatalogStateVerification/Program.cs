using System.Text.Json;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

var root = Path.Combine(Environment.CurrentDirectory, ".scan-state-verification-" + Guid.NewGuid().ToString("N"));
var outside = root + "-outside";
var workingRoot = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (!Path.GetFullPath(root).StartsWith(workingRoot, StringComparison.OrdinalIgnoreCase)
    || !Path.GetFullPath(outside).StartsWith(workingRoot, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Verification temp paths escaped the repository working directory.");
try
{
    Directory.CreateDirectory(Path.Combine(root, "index", "sample"));
    Directory.CreateDirectory(outside);
    string catalogPath = Path.Combine(root, "index", "sample", "catalog.json");
    string reportPath = Path.Combine(root, "index", "sample", "report.json");
    var catalog = new ResourceCatalog
    {
        GameBuild = "verification-build", GameAssemblySha256 = new string('A', 64),
        SourceDescription = "verification"
    };
    JsonFiles.Write(catalogPath, catalog);
    File.WriteAllText(reportPath, "{}");
    var store = new ScanCatalogStateStore();
    store.Save(root, catalogPath, reportPath);
    if (!store.TryLoad(root, out var restored, out var restoredPath, out var restoredReport, out var warning)
        || restored?.GameBuild != catalog.GameBuild || restoredPath != Path.GetFullPath(catalogPath)
        || restoredReport != Path.GetFullPath(reportPath) || warning != null)
        throw new InvalidOperationException("A valid recent scan index did not restore.");

    File.Delete(reportPath);
    if (!store.TryLoad(root, out restored, out _, out restoredReport, out warning)
        || restored?.GameBuild != catalog.GameBuild || restoredReport != null || warning == null)
        throw new InvalidOperationException("A missing report hid an otherwise valid scan index.");
    File.WriteAllText(reportPath, "{}");

    string outsideCatalog = Path.Combine(outside, "catalog.json");
    string outsideReport = Path.Combine(outside, "report.json");
    JsonFiles.Write(outsideCatalog, catalog);
    File.WriteAllText(outsideReport, "{}");
    File.WriteAllText(Path.Combine(root, "index", "last-scan.json"), JsonSerializer.Serialize(new
    {
        schema = "endfield-scan-catalog-state-v1", catalogPath = outsideCatalog, reportPath = outsideReport
    }));
    if (store.TryLoad(root, out _, out _, out _, out warning) || warning == null)
        throw new InvalidOperationException("A pointer into another workspace was accepted.");

    File.WriteAllText(Path.Combine(root, "index", "last-scan.json"), "{ invalid json");
    if (store.TryLoad(root, out _, out _, out _, out warning) || warning == null)
        throw new InvalidOperationException("A malformed pointer did not fail safely.");

    var oldSettings = JsonSerializer.Deserialize<WorkspaceOptions>("{}")!;
    if (oldSettings.ScanWorkers != 4) throw new InvalidOperationException("Legacy settings did not default to four workers.");
    if (args.Length == 3)
    {
        string package = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", package + Path.DirectorySeparatorChar);
        var options = new WorkspaceOptions
        {
            GameDirectory = Path.GetFullPath(args[1]), WorkspaceDirectory = Path.GetFullPath(args[2]),
            ScanWorkers = 4
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var run = await new GameResourceIndexer().BuildAsync(options, false,
            new Progress<IndexProgress>(_ => { }), timeout.Token);
        if (run.Catalog == null || run.Catalog.Characters.Count != 152 || run.CatalogPath == null)
            throw new InvalidOperationException("The packaged scanner did not return the cached game index.");
        var report = JsonFiles.Read<JsonElement>(run.ReportPath);
        if (report.GetProperty("workers").GetInt32() != 4 || report.GetProperty("reusedChunks").GetInt32() != 42)
            throw new InvalidOperationException("The .NET runner did not pass worker count or preserve cache reuse.");
        Console.WriteLine("PASS packaged .NET scanner forwarded 4 workers and restored all 42 cached CHKs.");
    }
    else if (args.Length != 0) throw new ArgumentException("Optional arguments: <package> <game> <workspace>");
    Console.WriteLine("PASS scan-state restore, workspace isolation, corrupt-state fallback, legacy worker default.");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
}
