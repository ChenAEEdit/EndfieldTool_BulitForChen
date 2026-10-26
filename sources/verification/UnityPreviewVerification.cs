using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EndfieldCharacterWorkbench.Verification
{
[InitializeOnLoad]
public static class UnityPreviewVerification
{
    [Serializable] class Request { public string scene = "", directory = "", invocation = ""; }
    [Serializable] class Result
    {
        public string schema = "endfield-preview-play-verification-v1", invocation, scene, error;
        public bool passed, actualPlay, timeAdvanced, seek, restart;
        public bool pause, speed, switchClip;
        public bool savedPoseMatchesPrefab;
        public bool mouseInteractionTested = false;
        public int meshes, nonFiniteVertices;
        public float height, firstTime, lastTime;
        public List<ClipCheck> clips = new List<ClipCheck>();
    }
    [Serializable] class ClipCheck { public string name; public float length, firstTime, lastTime, humanScale; public bool looping, timeAdvanced, human; public int movingTransforms, movingBodyBones; public string[] mainBoneMotions; }
    static Request request;
    static Result result;
    static CharacterPreviewControls controls;
    static double started;
    static int phase;
    static float pausedTime;
    static double pausedAt;
    static int clipIndex;
    static double clipStarted;
    static float clipFirstTime;
    static UnityPreviewVerification() { if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ENDFIELD_PREVIEW_CHECK"))) EditorApplication.update += Tick; }
    public static void Begin()
    {
        Load(); Directory.CreateDirectory(request.directory);
        SessionState.SetString("EndfieldPreviewCheck", request.invocation);
        EditorSceneManager.OpenScene(request.scene, OpenSceneMode.Single);
        var root = GameObject.Find("Character");
        var source = PrefabUtility.GetCorrespondingObjectFromSource(root);
        var expected = source.GetComponentsInChildren<Transform>(true).ToDictionary(item => AnimationUtility.CalculateTransformPath(item, source.transform));
        bool rest = root.GetComponentsInChildren<Transform>(true).All(item =>
        {
            Transform original;
            return expected.TryGetValue(AnimationUtility.CalculateTransformPath(item, root.transform), out original)
                && Vector3.Distance(item.localPosition, original.localPosition) < .000001f
                && Quaternion.Angle(item.localRotation, original.localRotation) < .01f
                && Vector3.Distance(item.localScale, original.localScale) < .000001f;
        });
        SessionState.SetBool("EndfieldPreviewPose." + request.invocation, rest);
        result.savedPoseMatchesPrefab = rest;
        if (Environment.GetEnvironmentVariable("ENDFIELD_PREVIEW_USE_ENTRY") == "1")
            typeof(EndfieldCharacterWorkbench.Editor.CharacterAnimationAdapter).GetMethod("OpenRequestedPreview", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        else EditorApplication.isPlaying = true;
    }
    static void Load()
    {
        request = JsonUtility.FromJson<Request>(File.ReadAllText(Environment.GetEnvironmentVariable("ENDFIELD_PREVIEW_CHECK")));
        result = new Result { invocation = request.invocation, scene = request.scene,
            savedPoseMatchesPrefab = SessionState.GetBool("EndfieldPreviewPose." + request.invocation, false) };
    }
    static void Tick()
    {
        try
        {
            if (request == null) Load();
            if (SessionState.GetString("EndfieldPreviewCheck", "") != request.invocation || !EditorApplication.isPlaying) return;
            if (!controls)
            {
                controls = UnityEngine.Object.FindFirstObjectByType<CharacterPreviewControls>();
                if (!controls || !(bool)typeof(CharacterPreviewControls).GetField("ready", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controls)) return;
                started = EditorApplication.timeSinceStartup; result.actualPlay = true;
            }
            EditorApplication.QueuePlayerLoopUpdate();
            double elapsed = EditorApplication.timeSinceStartup - started;
            if (phase == 0 && elapsed > .3)
            { result.firstTime = controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime; Geometry(); phase = 1; }
            else if (phase == 1 && elapsed > 1.3)
            {
                result.lastTime = controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                result.timeAdvanced = result.lastTime > result.firstTime + .05f;
                Geometry(); Capture("body.png", false); Capture("face.png", true);
                controls.SetPaused(true); pausedTime = controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                pausedAt = EditorApplication.timeSinceStartup; phase = 2;
            }
            else if (phase == 2 && EditorApplication.timeSinceStartup - pausedAt > .25)
            {
                result.pause = Math.Abs(controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime - pausedTime) < .0001f;
                controls.SetSpeed(.5f); controls.SetPaused(false); result.speed = Math.Abs(controls.animator.speed - .5f) < .001f;
                controls.SeekNormalized(.7f);
                result.seek = Math.Abs(controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime - .7f) < .02f;
                controls.Restart();
                result.restart = controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime < .02f;
                if (controls.clips.Length > 1)
                {
                    controls.SelectClip(1);
                    result.switchClip = controls.selected == 1 && controls.animator.GetCurrentAnimatorStateInfo(0).fullPathHash == Animator.StringToHash(controls.statePaths[1]);
                    Geometry();
                }
                clipIndex = 0; BeginClipCheck(); phase = 3;
            }
            else if (phase == 3 && EditorApplication.timeSinceStartup - clipStarted > .3)
            {
                var clip = controls.clips[clipIndex];
                float currentTime = controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                controls.SetPaused(true); controls.SeekNormalized(.1f);
                var bones = controls.animator.GetComponentsInChildren<Transform>(true);
                var positions = bones.Select(item => item.localPosition).ToArray();
                var rotations = bones.Select(item => item.localRotation).ToArray();
                var scales = bones.Select(item => item.localScale).ToArray();
                controls.SeekNormalized(.7f);
                int moving = Enumerable.Range(0, bones.Length).Count(i => Vector3.Distance(positions[i], bones[i].localPosition) > .000001f
                    || Quaternion.Angle(rotations[i], bones[i].localRotation) > .05f || Vector3.Distance(scales[i], bones[i].localScale) > .000001f);
                Geometry();
                var main = Enumerable.Range(0, bones.Length).Where(i => new[] { "Bip001_L_Thigh", "Bip001_R_Thigh", "Bip001_L_UpperArm", "Bip001_R_UpperArm", "Bip001_Spine" }.Contains(bones[i].name)).ToArray();
                var display = typeof(CharacterPreviewControls).GetMethod("DisplayName");
                result.clips.Add(new ClipCheck { name = display == null ? clip.name : (string)display.Invoke(controls, new object[] { clipIndex }),
                    human = controls.animator.isHuman, humanScale = controls.animator.isHuman ? controls.animator.humanScale : 1,
                    movingBodyBones = main.Count(i => Quaternion.Angle(rotations[i], bones[i].localRotation) > .05f), length = clip.length, looping = clip.isLooping,
                    firstTime = clipFirstTime, lastTime = currentTime, timeAdvanced = currentTime > clipFirstTime + .001f, movingTransforms = moving,
                    mainBoneMotions = Enumerable.Range(0, bones.Length).Where(i => new[] { "Bip001_L_Thigh", "Bip001_R_Thigh", "Bip001_L_UpperArm", "Bip001_R_UpperArm", "Bip001_Spine" }.Contains(bones[i].name))
                        .Select(i => bones[i].name + ": " + Quaternion.Angle(rotations[i], bones[i].localRotation).ToString("F3")).ToArray() });
                if (clip.isLooping || clip.name.Contains("loop")) Capture("loop.png", false);
                clipIndex++;
                if (clipIndex < controls.clips.Length) BeginClipCheck();
                else
                {
                    result.passed = result.actualPlay && result.savedPoseMatchesPrefab && result.timeAdvanced && result.seek && result.restart && result.pause && result.speed && result.switchClip && result.meshes > 0
                        && result.nonFiniteVertices == 0 && result.height > .5f && result.height < 3
                        && result.clips.Count == controls.clips.Length && result.clips.All(item => item.timeAdvanced && item.length > 0 && item.movingTransforms > 0
                            && (Environment.GetEnvironmentVariable("ENDFIELD_REQUIRE_MAIN_BODY") != "1" || item.movingBodyBones > 0));
                    Finish(result.passed ? 0 : 1);
                }
            }
            if (elapsed > 60) throw new Exception("Preview Play check timed out.");
        }
        catch (Exception error) { if (result == null) result = new Result(); result.error = error.ToString(); Finish(1); }
    }
    static void BeginClipCheck()
    {
        controls.SetSpeed(1); controls.SetPaused(false); controls.SelectClip(clipIndex);
        clipFirstTime = controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
        clipStarted = EditorApplication.timeSinceStartup;
    }
    static void Geometry()
    {
        Bounds bounds = new Bounds(); bool first = true;
        var skins = controls.animator.GetComponentsInChildren<SkinnedMeshRenderer>(true); result.meshes = skins.Length;
        foreach (var skin in skins)
        {
            var mesh = new Mesh(); skin.BakeMesh(mesh, false);
            foreach (var point in mesh.vertices)
            {
                var world = skin.transform.TransformPoint(point);
                if (!Finite(world.x) || !Finite(world.y) || !Finite(world.z)) { result.nonFiniteVertices++; continue; }
                if (first) { bounds = new Bounds(world, Vector3.zero); first = false; } else bounds.Encapsulate(world);
            }
            UnityEngine.Object.DestroyImmediate(mesh);
        }
        result.height = bounds.size.y;
    }
    static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    static void Capture(string name, bool face)
    {
        var camera = controls.previewCamera; var position = camera.transform.position; var rotation = camera.transform.rotation;
        var target = camera.targetTexture; var rect = camera.rect; var previous = RenderTexture.active;
        var texture = new RenderTexture(900, 1000, 24); var picture = new Texture2D(900, 1000, TextureFormat.RGB24, false);
        try
        {
            if (face)
            {
                var head = controls.animator.GetComponentsInChildren<Transform>(true).First(item => item.name == "Bip001_Head");
                camera.transform.position = head.position + Vector3.forward * .6f; camera.transform.LookAt(head.position);
            }
            camera.targetTexture = texture; camera.rect = new Rect(0, 0, 1, 1); camera.Render(); RenderTexture.active = texture;
            picture.ReadPixels(new Rect(0, 0, 900, 1000), 0, 0); picture.Apply();
            File.WriteAllBytes(Path.Combine(request.directory, name), picture.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = target; camera.rect = rect; RenderTexture.active = previous;
            camera.transform.SetPositionAndRotation(position, rotation);
            UnityEngine.Object.DestroyImmediate(picture); texture.Release(); UnityEngine.Object.DestroyImmediate(texture);
        }
    }
    static void Finish(int code)
    {
        EditorApplication.update -= Tick;
        SessionState.EraseString("EndfieldPreviewCheck");
        File.WriteAllText(Path.Combine(request.directory, "play-verification.json"), JsonUtility.ToJson(result, true));
        EditorApplication.Exit(code);
    }
}
}
