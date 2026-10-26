using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace EndfieldCharacterWorkbench.Verification
{
[InitializeOnLoad]
public static class UnityHoldVerification
{
    [Serializable] class Proof { public bool passed, actualPlay, timeAdvanced; public string scene; public float first, last; }
    static float first;
    static double started;
    static UnityHoldVerification()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ENDFIELD_ACCEPTANCE_HOLD"))) EditorApplication.update += Tick;
    }
    public static void Begin()
    {
        string root = Environment.GetEnvironmentVariable("ENDFIELD_ACCEPTANCE_HOLD");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ready.txt"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
        // Deliberately block this owned editor's main thread, proving timeout/cancel
        // behavior against an actually running editor rather than a simulated lock.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, "release.txt")) && watch.Elapsed.TotalSeconds < 120) Thread.Sleep(100);
        if (watch.Elapsed.TotalSeconds >= 120) EditorApplication.Exit(2);
    }
    static void Tick()
    {
        if (!EditorApplication.isPlaying) return;
        var controls = UnityEngine.Object.FindFirstObjectByType<CharacterPreviewControls>();
        if (!controls || !controls.animator || !controls.animator.runtimeAnimatorController) return;
        float time = controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
        if (started == 0) { started = EditorApplication.timeSinceStartup; first = time; return; }
        if (EditorApplication.timeSinceStartup - started < .7) return;
        var proof = new Proof { actualPlay = true, first = first, last = time, timeAdvanced = time > first + .05f,
            scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path };
        proof.passed = proof.timeAdvanced;
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("ENDFIELD_ACCEPTANCE_HOLD"), "play-proof.json"), JsonUtility.ToJson(proof, true));
        EditorApplication.Exit(proof.passed ? 0 : 1);
    }
}
}
