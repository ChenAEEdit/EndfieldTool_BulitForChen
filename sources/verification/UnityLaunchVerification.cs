using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EndfieldCharacterWorkbench.Verification
{
[InitializeOnLoad]
public static class UnityLaunchVerification
{
    [Serializable] class Request { public string scene = "", directory = ""; }
    [Serializable] class Result
    {
        public bool passed, actualPlay, correctScene, timeAdvanced, defaultLoop, human;
        public string firstRequest = "", secondRequest = "", error = "";
        public float firstTime, lastTime;
    }
    static Request request;
    static Result result;
    static double started;
    static double playingAt;
    static UnityLaunchVerification()
    {
        string path = Environment.GetEnvironmentVariable("ENDFIELD_LAUNCH_CHECK");
        if (string.IsNullOrEmpty(path)) return;
        if (!Application.dataPath.Replace('\\', '/').StartsWith("D:/AAAChatGPT/UnityValidation/", StringComparison.OrdinalIgnoreCase)) return;
        request = JsonUtility.FromJson<Request>(File.ReadAllText(path)); result = new Result();
        started = EditorApplication.timeSinceStartup; EditorApplication.update += Tick;
    }
    static void Tick()
    {
        try
        {
            if (EditorApplication.timeSinceStartup - started > 90) throw new Exception("Launcher verification timed out.");
            if (!EditorApplication.isPlaying) return;
            var controls = UnityEngine.Object.FindFirstObjectByType<CharacterPreviewControls>();
            if (!controls || !(bool)typeof(CharacterPreviewControls).GetField("ready", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controls)) return;
            string file = "Library/EndfieldCharacterWorkbench/preview-open-result.json";
            if (!File.Exists(file)) return;
            var receipt = JsonUtility.FromJson<EndfieldCharacterWorkbench.Editor.CharacterAnimationAdapter.PreviewOpenReceipt>(File.ReadAllText(file));
            if (receipt.status != "opened") return;
            EditorApplication.QueuePlayerLoopUpdate();
            if (string.IsNullOrEmpty(result.firstRequest))
            {
                result.firstRequest = receipt.requestId; result.actualPlay = true;
                result.correctScene = SceneManager.GetActiveScene().path == request.scene;
                result.defaultLoop = controls.clips[controls.selected].isLooping;
                result.human = controls.animator.isHuman;
                result.firstTime = controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                playingAt = EditorApplication.timeSinceStartup;
                File.WriteAllText(Path.Combine(request.directory, "launch-started.json"), JsonUtility.ToJson(result, true));
            }
            else if (receipt.requestId != result.firstRequest) result.secondRequest = receipt.requestId;
            if (EditorApplication.timeSinceStartup - playingAt < .5 || string.IsNullOrEmpty(result.secondRequest)) return;
            result.lastTime = controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
            result.timeAdvanced = result.lastTime > result.firstTime + .02f;
            result.passed = result.actualPlay && result.correctScene && result.defaultLoop && result.timeAdvanced;
            Finish(result.passed ? 0 : 1);
        }
        catch (Exception error) { result.error = error.ToString(); Finish(1); }
    }
    static void Finish(int code)
    {
        EditorApplication.update -= Tick;
        File.WriteAllText(Path.Combine(request.directory, "launch-verification.json"), JsonUtility.ToJson(result, true));
        EditorApplication.Exit(code);
    }
}
}
