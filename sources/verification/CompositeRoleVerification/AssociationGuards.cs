using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

internal static class AssociationGuards
{
    public static void Run(string reportPath)
    {
        var body = new BundleObjectRow { Key = "body", Kind = "GameObject", Name = "chr_9999_sample_uimodel",
            IsModelCandidate = true, HierarchyRoot = true, BoneReferenceCount = 100, LinkedAvatarKey = "avatar",
            LinkedControllerKey = "controller", ControllerEvidence = "actual-animator-controller-reference",
            ControllerClipKeys = ["overview", "relax", "special"] };
        var baseline = new List<BundleObjectRow> { body,
            new() { Key = "avatar", Kind = "Avatar" },
            new() { Key = "overview", Kind = "AnimationClip", Name = "A_actor_sample_ui_overview_loop" },
            new() { Key = "relax", Kind = "AnimationClip", Name = "A_actor_sample_ui_relax_loop" },
            new() { Key = "special", Kind = "AnimationClip", Name = "A_actor_sample_ui_relax_sp_01" } };
        var checks = new List<string>();
        void Check(string name, bool passed) { if (!passed) throw new Exception(name); checks.Add(name); }
        ResolvedCharacterAssociation Resolve(List<BundleObjectRow> rows, bool special = false) =>
            CharacterAssociationResolver.Resolve(new ObjectInventory { Objects = rows }, "sample", special);
        void Reject(string name, List<BundleObjectRow> rows)
        {
            try { Resolve(rows); }
            catch (InvalidDataException) { checks.Add(name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        Check("existing two-loop API retained", Resolve(baseline).Clips.Count == 2);
        Check("full idle includes controller-referenced special", Resolve(baseline, true).Clips.Count == 3);
        var prop = new BundleObjectRow { Key = "prop", Kind = "GameObject", Name = "StaticProp", IsModelCandidate = true,
            HierarchyRoot = false, ParentGameObjectKey = "body", RendererCount = 1 };
        var multi = new List<BundleObjectRow>(baseline) { prop };
        Check("body plus static prop without Avatar accepted", Resolve(multi).RelatedModels.Single().Key == "prop");
        multi.Add(prop with { Key = "unrelated", HierarchyRoot = true, ParentGameObjectKey = null });
        Check("unrelated root not silently attached", Resolve(multi).RelatedModels.Count == 1);
        Reject("duplicate body refused", [.. baseline, body with { Key = "duplicate" }]);
        Reject("missing Avatar refused", baseline.Where(o => o.Key != "avatar").ToList());
        Reject("missing controller refused", [body with { LinkedControllerKey = null }, .. baseline.Skip(1)]);
        Reject("missing clip refused", baseline.Where(o => o.Key != "relax").ToList());
        Reject("contradictory parent refused", [body with { ParentGameObjectKey = "parent" }, .. baseline.Skip(1)]);
        Reject("idle ambiguity refused", [body with { ControllerClipKeys = [.. body.ControllerClipKeys, "other"] }, .. baseline.Skip(1),
            baseline[2] with { Key = "other" }]);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<ObjectExportSelection>("{\"modelKey\":\"body\",\"avatarKey\":\"avatar\",\"clipKeys\":[\"overview\"]}", JsonFiles.Options)!;
        Check("old single-model selection readable", legacy.RelatedModelKeys.Count == 0 && legacy.ModelKey == "body");
        var original = body with { SourceBundle = @"C:\old\bundle.ab", SourceAsset = "CAB-example", PathId = "123" };
        var moved = original with { Key = "new-snapshot-key", SourceBundle = @"D:\new\bundle.ab" };
        Check("investigation preserves logical selection across snapshot paths", ObjectWorkflow.MatchSnapshotObject(new ObjectInventory { Objects = [moved] }, original).Key == moved.Key);
        bool ambiguous = false;
        try { ObjectWorkflow.MatchSnapshotObject(new ObjectInventory { Objects = [moved, moved with { Key = "duplicate" }] }, original); }
        catch (InvalidDataException) { ambiguous = true; }
        Check("ambiguous preserved selection refused", ambiguous);
        JsonFiles.Write(reportPath, new { passed = true, checks });
        Console.WriteLine($"PASS {checks.Count} association and compatibility checks.");
    }
}
