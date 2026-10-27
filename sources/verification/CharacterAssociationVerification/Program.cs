using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

if (args.Length != 1) { Console.Error.WriteLine("Usage: <report.json>"); return 2; }
string role = "verification_role";
var model = new BundleObjectRow { Key = "model", Kind = "GameObject", Name = "chr_0025_verification_role_uimodel",
    IsModelCandidate = true, HierarchyRoot = true, BoneReferenceCount = 20, LinkedAvatarKey = "avatar",
    LinkedControllerKey = "controller", ControllerEvidence = "actual-animator-controller-reference",
    ControllerClipKeys = ["overview", "relax", "other"] };
var avatar = new BundleObjectRow { Key = "avatar", Kind = "Avatar", Name = "roleAvatar" };
var overview = new BundleObjectRow { Key = "overview", Kind = "AnimationClip", Name = "A_actor_verification_role_ui_overview_loop_01" };
var relax = new BundleObjectRow { Key = "relax", Kind = "AnimationClip", Name = "A_actor_verification_role_ui_relax_loop" };
var other = new BundleObjectRow { Key = "other", Kind = "AnimationClip", Name = "A_actor_verification_role_ui_wave" };
var original = new[] { model, avatar, overview, relax, other };
var checks = new List<object>();
void Reject(string name, BundleObjectRow[] rows)
{
    try { CharacterAssociationResolver.Resolve(new ObjectInventory { Objects = rows.ToList() }, role); }
    catch (InvalidDataException error) { checks.Add(new { name, rejected = true, reason = error.Message }); return; }
    throw new InvalidOperationException("Unsafe association accepted: " + name);
}
var resolved = CharacterAssociationResolver.Resolve(new ObjectInventory { Objects = original.ToList() }, role);
if (resolved.Model.Key != "model" || resolved.Avatar.Key != "avatar"
    || !resolved.Clips.Select(row => row.Key).Order().SequenceEqual(new[] { "overview", "relax" }))
    throw new InvalidOperationException("Valid controller association resolved incorrectly.");
checks.Add(new { name = "valid-reference-chain", passed = true, clips = resolved.Clips.Select(row => row.Key).ToArray() });
Reject("duplicate-model-root", original.Append(model with { Key = "duplicate" }).ToArray());
Reject("missing-avatar", original.Where(row => row.Key != "avatar").ToArray());
Reject("unsupported-controller-evidence", original.Select(row => row.Key == "model" ? row with { ControllerEvidence = "none" } : row).ToArray());
Reject("missing-controller-clip", original.Select(row => row.Key == "model" ? row with { ControllerClipKeys = ["overview", "relax", "missing"] } : row).ToArray());
Reject("contradictory-hierarchy", original.Select(row => row.Key == "model" ? row with { ParentGameObjectKey = "parent" } : row).ToArray());
Reject("ambiguous-idle", original.Select(row => row.Key == "model"
    ? row with { ControllerClipKeys = ["overview", "relax", "other", "duplicate-relax"] } : row)
    .Append(relax with { Key = "duplicate-relax", Name = "A_actor_verification_role_ui_relax_loop_02" }).ToArray());
JsonFiles.Write(Path.GetFullPath(args[0]), new { passed = true, checks });
Console.WriteLine($"PASS {checks.Count} role association checks.");
return 0;
