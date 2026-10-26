using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using EndfieldCharacterWorkbench.Editor;

namespace EndfieldCharacterWorkbench.Verification
{
public static class UnityGeneratedKneeVerification
{
    [Serializable] class Request { public string directory = ""; public string[] configs = new string[0]; }
    public static void Run()
    {
        var request = JsonUtility.FromJson<Request>(File.ReadAllText(Environment.GetEnvironmentVariable("ENDFIELD_GENERATED_KNEE_CHECK")));
        Directory.CreateDirectory(request.directory);
        var results = new List<string>();
        try
        {
            foreach (string configPath in request.configs)
            {
                string generated = Path.GetDirectoryName(configPath).Replace('\\', '/') + "/Generated";
                var config = JsonUtility.FromJson<CharacterAnimationAdapter.Config>(File.ReadAllText(configPath));
                var report = JsonUtility.FromJson<CharacterAnimationAdapter.BuildReport>(File.ReadAllText(generated + "/adaptation-result.json"));
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(generated + "/Character.prefab");
                if (!report.adapted || !prefab) throw new Exception("Requires complete generated package: " + configPath);
                var generic = AssetDatabase.LoadAllAssetsAtPath(Path.GetDirectoryName(configPath).Replace('\\', '/') + "/" + config.model).OfType<Avatar>().Single(a => a.isValid && !a.isHuman);
                foreach (var clip in report.clips.Where(c => c.name.Contains("walk")))
                {
                    var avatar = clip.humanoid ? AssetDatabase.LoadAssetAtPath<Avatar>(report.humanAvatar) : generic;
                    foreach (string mode in new[] { "current", "without_knee_correctives" })
                    {
                        string id = results.Count.ToString("D2") + "_" + clip.name;
                        var variant = typeof(KneeDiagnostics).GetMethod("Inspect", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,
                            new object[] { prefab.GetComponent<Animator>(), id, mode, request.directory, clip.output, clip.state, avatar });
                        string json = JsonUtility.ToJson(variant, true);
                        if (!json.Contains("edge99")) throw new Exception("No sampled knee edges");
                        results.Add(json);
                    }
                }
            }
            File.WriteAllText(Path.Combine(request.directory, "knee-verification.json"), "{\"schema\":\"endfield-generated-knee-verification-v1\",\"passed\":true,\"qualityPassed\":false,\"sampling\":\"Editor Animator sampling; actual Play verified separately\",\"variants\":[" + string.Join(",", results.ToArray()) + "]}");
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(request.directory, "error.txt"), error.ToString());
            EditorApplication.Exit(1);
        }
    }
}
}
