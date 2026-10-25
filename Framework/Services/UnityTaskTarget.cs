using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

// A Unity destination can change without changing the hashed extraction plan.
public static class UnityTaskTarget
{
    private sealed record Target(string Schema, Guid TaskId, string PlanSha256,
        string Project, string Editor, DateTimeOffset SavedAt);

    public static SavedPlan Resolve(SavedPlan saved)
    {
        string path = TargetPath(saved);
        if (!File.Exists(path)) return saved;
        var target = JsonFiles.Read<Target>(path);
        if (target.Schema != "endfield-unity-task-target-v1" || target.TaskId != saved.Plan.Id
            || target.PlanSha256 != UnityProjectFiles.Hash(saved.FilePath, CancellationToken.None)
            || !Path.IsPathFullyQualified(target.Project) || !Path.IsPathFullyQualified(target.Editor))
            throw new InvalidDataException("当前任务的 Unity 目标记录无效，请重新保存任务目标。");
        return saved with { Plan = saved.Plan with { Options = saved.Plan.Options with {
            UnityProject = target.Project, UnityEditor = target.Editor } } };
    }

    public static void Save(SavedPlan saved, string project, string editor)
    {
        project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        editor = Path.GetFullPath(editor);
        UnityAdaptationRunner.ValidateProject(project, editor);
        _ = new WorkspaceStore().Prepare(saved.Plan.Options with { UnityProject = project });
        string path = TargetPath(saved);
        using var taskLock = new FileStream(Path.Combine(Path.GetDirectoryName(path)!, "extraction.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try { taskLock.Lock(0, 1); }
        catch (IOException error) { throw new IOException("任务正在运行，请完成或取消后再更换目标项目。", error); }
        try
        {
            JsonFiles.Write(path, new Target("endfield-unity-task-target-v1", saved.Plan.Id,
                UnityProjectFiles.Hash(saved.FilePath, CancellationToken.None), project, editor, DateTimeOffset.Now));
        }
        finally { taskLock.Unlock(0, 1); }
    }

    private static string TargetPath(SavedPlan saved)
    {
        string root = Path.Combine(Path.GetFullPath(saved.Plan.Options.WorkspaceDirectory), "tasks", saved.Plan.Id.ToString("N"));
        if (!Path.GetFullPath(saved.FilePath).Equals(Path.Combine(root, "plan.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("任务目标必须保存于对应任务目录。");
        string path = Path.Combine(root, "unity-target.json");
        UnityProjectFiles.NoLinks(path);
        UnityProjectFiles.NoLinks(saved.FilePath);
        return path;
    }
}
