using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine.SceneManagement;
using UnityEngine;
using EndfieldCharacterWorkbench.Editor;

namespace EndfieldCharacterWorkbench.Verification
{
public static class UnityMenuVerification
{
    [Serializable] class Report
    {
        public bool passed, missingSelectionReproduced, repeatedBuildReproduced, generatedFilesUnchanged;
        public string missingSelectionError, repeatedBuildError;
    }
    public static void ReproduceOriginal()
    {
        string config = Environment.GetEnvironmentVariable("ENDFIELD_MENU_CONFIG");
        string directory = Path.GetDirectoryName(config) + "/Generated";
        var before = Snapshot(directory);
        var report = new Report();
        Selection.activeObject = null;
        try { CharacterAnimationAdapter.BuildSelected(); }
        catch (Exception error) { report.missingSelectionError = error.Message; report.missingSelectionReproduced = error.Message.Contains("请选择") || error.Message.Contains("请在 Project"); }
        Selection.activeObject = AssetDatabase.LoadAssetAtPath<TextAsset>(config);
        try { CharacterAnimationAdapter.BuildSelected(); }
        catch (Exception error) { report.repeatedBuildError = error.Message; report.repeatedBuildReproduced = error.Message.Contains("本包已生成内容"); }
        var after = Snapshot(directory);
        report.generatedFilesUnchanged = before.Count == after.Count && before.All(pair => after.ContainsKey(pair.Key) && after[pair.Key] == pair.Value);
        report.passed = report.missingSelectionReproduced && report.repeatedBuildReproduced && report.generatedFilesUnchanged;
        File.WriteAllText(Environment.GetEnvironmentVariable("ENDFIELD_MENU_REPORT"), JsonUtility.ToJson(report, true));
        if (!report.passed) throw new Exception("Original menu reproduction did not match the reported errors.");
    }

    [Serializable] class FixedReport
    {
        public bool passed, noJsonSelectionNeeded, repeatedBuildOpensExisting, metadataTamperRejected,
            wrongConfigHashRejected, bridgeOpensExpectedScene, generatedFilesUnchanged;
        public bool legacyTposeChecked, legacyTposeRejected;
    }
    public static void VerifyFixed()
    {
        string config = Environment.GetEnvironmentVariable("ENDFIELD_MENU_CONFIG");
        string generated = Path.GetDirectoryName(config).Replace('\\', '/') + "/Generated";
        string scene = generated + "/CharacterPreview.unity";
        var before = Snapshot(generated);
        var report = new FixedReport();
        CharacterAnimationAdapter.OpenExistingPreview(config, false);
        Selection.activeObject = null;
        CharacterAnimationAdapter.BuildSelected();
        report.noJsonSelectionNeeded = SceneManager.GetActiveScene().path == scene;
        Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(generated + "/Character.prefab");
        CharacterAnimationAdapter.BuildSelected();
        CharacterAnimationAdapter.BuildSelected();
        report.repeatedBuildOpensExisting = SceneManager.GetActiveScene().path == scene;
        string metadata = scene + ".meta";
        byte[] original = File.ReadAllBytes(metadata);
        try
        {
            File.AppendAllText(metadata, "\n# metadata verification\n");
            try { CharacterAnimationAdapter.OpenExistingPreview(config, false); }
            catch (Exception) { report.metadataTamperRejected = true; }
        }
        finally { File.WriteAllBytes(metadata, original); }
        string work = "Library/EndfieldCharacterWorkbench";
        var request = new CharacterAnimationAdapter.PreviewOpenRequest { requestId = Guid.NewGuid().ToString("N"), configPath = config, configSha256 = "wrong", play = false };
        File.WriteAllText(work + "/preview-open.json", JsonUtility.ToJson(request));
        CharacterAnimationAdapter.OpenRequestedPreview();
        var receipt = JsonUtility.FromJson<CharacterAnimationAdapter.PreviewOpenReceipt>(File.ReadAllText(work + "/preview-open-result.json"));
        report.wrongConfigHashRejected = receipt.requestId == request.requestId && receipt.status == "failed";
        request.requestId = Guid.NewGuid().ToString("N");
        using (var stream = File.OpenRead(config)) using (var sha = SHA256.Create()) request.configSha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        File.WriteAllText(work + "/preview-open.json", JsonUtility.ToJson(request));
        CharacterAnimationAdapter.OpenRequestedPreview();
        receipt = JsonUtility.FromJson<CharacterAnimationAdapter.PreviewOpenReceipt>(File.ReadAllText(work + "/preview-open-result.json"));
        report.bridgeOpensExpectedScene = receipt.requestId == request.requestId && receipt.status == "opened" && receipt.scene == scene;
        string legacy = Environment.GetEnvironmentVariable("ENDFIELD_OLD_CONFIG");
        if (!string.IsNullOrEmpty(legacy))
        {
            report.legacyTposeChecked = true;
            try { CharacterAnimationAdapter.OpenExistingPreview(legacy, false); }
            catch (Exception error) { report.legacyTposeRejected = error.Message.Contains("主体肌肉通道"); }
        }
        var after = Snapshot(generated);
        report.generatedFilesUnchanged = before.Count == after.Count && before.All(pair => after.ContainsKey(pair.Key) && after[pair.Key] == pair.Value);
        report.passed = report.noJsonSelectionNeeded && report.repeatedBuildOpensExisting && report.metadataTamperRejected
            && report.wrongConfigHashRejected && report.bridgeOpensExpectedScene && report.generatedFilesUnchanged
            && (!report.legacyTposeChecked || report.legacyTposeRejected);
        File.WriteAllText(Environment.GetEnvironmentVariable("ENDFIELD_MENU_REPORT"), JsonUtility.ToJson(report, true));
        if (!report.passed) throw new Exception("Fixed menu verification failed.");
    }

    static Dictionary<string, string> Snapshot(string directory)
    {
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path, path =>
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream));
        });
    }
}
}
