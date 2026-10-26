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
[InitializeOnLoad]
public static class UnityMouseVerification
{
    [Serializable] class Request { public string scene = "", directory = "", invocation = ""; }
    [Serializable] class ControlRect { public string id; public float x, y, width, height; }
    [Serializable] class State
    {
        public string invocation, scene, clip, error = "";
        public bool ready, actualPlay, paused, avatarHuman, clipHuman;
        public int processId, selected, frames;
        public float normalized, speed, yaw, zoom;
        public ControlRect[] controls;
    }
    static readonly Dictionary<string, ControlRect> rects = new Dictionary<string, ControlRect>();
    static Request request;
    static double lastWrite, started;
    static int frames;
    static UnityMouseVerification()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ENDFIELD_MOUSE_CHECK"))) return;
        CharacterPreviewControls.GuiControlRendered += Observe;
        EditorApplication.update += Tick;
    }
    static void Load()
    {
        if (request == null) request = JsonUtility.FromJson<Request>(File.ReadAllText(Environment.GetEnvironmentVariable("ENDFIELD_MOUSE_CHECK")));
    }
    public static void Begin()
    {
        Load(); Directory.CreateDirectory(request.directory);
        SessionState.SetString("EndfieldMouseCheck", request.invocation);
        CharacterAnimationAdapter.OpenExistingPreview(request.scene.Replace("Generated/CharacterPreview.unity", "adaptation.json"), true);
        var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        var game = EditorWindow.GetWindow(type); game.maximized = true; game.Focus();
    }
    static void Observe(string id, Rect rect)
    {
        rects[id] = new ControlRect { id = id, x = rect.x, y = rect.y, width = rect.width, height = rect.height };
    }
    static T Field<T>(CharacterPreviewControls controls, string name)
    { return (T)typeof(CharacterPreviewControls).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controls); }
    static void Tick()
    {
        try
        {
            Load();
            if (SessionState.GetString("EndfieldMouseCheck", "") != request.invocation) return;
            if (File.Exists(Path.Combine(request.directory, "stop.txt"))) { EditorApplication.Exit(0); return; }
            if (!EditorApplication.isPlaying) return;
            EditorApplication.QueuePlayerLoopUpdate();
            if (started == 0) started = EditorApplication.timeSinceStartup;
            if (EditorApplication.timeSinceStartup - started > 150) throw new Exception("Mouse verification timed out");
            frames++;
            if (EditorApplication.timeSinceStartup - lastWrite < .1) return;
            lastWrite = EditorApplication.timeSinceStartup;
            var controls = UnityEngine.Object.FindFirstObjectByType<CharacterPreviewControls>();
            if (!controls || !Field<bool>(controls, "ready")) return;
            var state = new State { invocation = request.invocation, scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path,
                ready = true, actualPlay = true, processId = System.Diagnostics.Process.GetCurrentProcess().Id, frames = frames,
                selected = controls.selected, clip = controls.DisplayName(controls.selected), avatarHuman = controls.animator.isHuman,
                clipHuman = controls.clips[controls.selected].isHumanMotion, normalized = controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime,
                paused = Field<bool>(controls, "paused"), speed = Field<float>(controls, "speed"), yaw = Field<float>(controls, "yaw"), zoom = Field<float>(controls, "zoom"),
                controls = rects.Values.ToArray() };
            string file = Path.Combine(request.directory, "state.json"), temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(state, true));
            if (File.Exists(file)) File.Delete(file); File.Move(temporary, file);
        }
        catch (Exception error)
        {
            if (request != null) File.WriteAllText(Path.Combine(request.directory, "native-error.txt"), error.ToString());
            EditorApplication.Exit(1);
        }
    }
}
}
