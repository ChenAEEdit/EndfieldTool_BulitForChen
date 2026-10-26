using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using EndfieldCharacterWorkbench.Editor;

namespace EndfieldCharacterWorkbench.Verification
{
public static class UnityEdgeVerification
{
    [Serializable] class Result
    {
        public bool passed, prefabExists, sceneAbsent, menuSucceeded, repeatedMenuSucceeded, generatedUnchanged, missingSceneExplained;
        public string error, config;
    }
    public static void PrefabOnly()
    {
        string config = Environment.GetEnvironmentVariable("ENDFIELD_EDGE_CONFIG");
        string generated = Path.GetDirectoryName(config).Replace('\\', '/') + "/Generated";
        var result = new Result { config = config };
        var before = Snapshot(generated);
        try
        {
            result.prefabExists = AssetDatabase.LoadAssetAtPath<GameObject>(generated + "/Character.prefab");
            result.sceneAbsent = !File.Exists(generated + "/CharacterPreview.unity");
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<TextAsset>(config);
            CharacterAnimationAdapter.BuildSelected(); result.menuSucceeded = true;
            CharacterAnimationAdapter.BuildSelected(); result.repeatedMenuSucceeded = true;
            try { CharacterAnimationAdapter.OpenExistingPreview(config, true); }
            catch (Exception error) { result.missingSceneExplained = error.Message.Contains("未生成预览场景"); }
        }
        catch (Exception error) { result.error = error.ToString(); }
        var after = Snapshot(generated);
        result.generatedUnchanged = before.Count == after.Count && before.All(pair => after.ContainsKey(pair.Key) && after[pair.Key] == pair.Value);
        result.passed = result.prefabExists && result.sceneAbsent && result.menuSucceeded && result.repeatedMenuSucceeded && result.generatedUnchanged && result.missingSceneExplained;
        File.WriteAllText(Environment.GetEnvironmentVariable("ENDFIELD_EDGE_REPORT"), JsonUtility.ToJson(result, true));
        if (!result.passed) throw new Exception("Prefab-only menu verification failed: " + result.error);
    }
    static Dictionary<string, string> Snapshot(string root)
    {
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, path => {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream));
        });
    }
}
}
