using System;
using UnityEngine;

namespace EndfieldCharacterWorkbench
{
// Runtime controls only; no edit-mode animation sampling or global time changes.
public sealed class CharacterPreviewControls : MonoBehaviour
{
    public Animator animator;
    public Camera previewCamera;
    public AnimationClip[] clips = new AnimationClip[0];
    public string[] statePaths = new string[0];
    public string[] displayNames = new string[0];
    public Avatar[] clipAvatars = new Avatar[0];
    public Vector3 cameraPivot;
    public float cameraDistance = 3;
    public float cameraRadius = 1;
    public int selected;

    bool paused, ready;
    float speed = 1, yaw, pitch = 4, zoom = 1, previousSpeed = 1;
    string error;
    Vector2 listScroll, panelScroll;
    Rect previousCameraRect = new Rect(0, 0, 1, 1);
    int previousWidth, previousHeight;

    void Start()
    {
        previousSpeed = animator ? animator.speed : 1;
        if (previewCamera) previousCameraRect = previewCamera.rect;
        if (!animator || !animator.runtimeAnimatorController || clips.Length == 0 || clips.Length != statePaths.Length)
        { error = "预览配置不完整，请重新生成独立适配包。"; return; }
        for (int i = 0; i < clips.Length; i++)
        {
            if (!clips[i] || string.IsNullOrEmpty(statePaths[i]) || !animator.HasState(0, Animator.StringToHash(statePaths[i])))
            { error = "找不到动画或播放状态，请检查生成报告。"; return; }
            var expected = clipAvatars != null && i < clipAvatars.Length && clipAvatars[i] ? clipAvatars[i] : animator.avatar;
            if (!expected || !expected.isValid || clips[i].isHumanMotion && !expected.isHuman)
            { error = "动画缺少有效的配套 Avatar，请用新版工具重新生成预览。"; return; }
        }
        ready = true;
        int initial = Mathf.Clamp(selected, 0, clips.Length - 1);
        // A transition/end clip naturally stops. Prefer the available looping action on first open.
        if (initial == 0 && !clips[0].isLooping)
            for (int i = 1; i < clips.Length; i++) if (clips[i].isLooping) { initial = i; break; }
        Select(initial);
        UpdateCamera();
    }

    void OnEnable() { if (ready && animator) animator.speed = paused ? 0 : speed; }
    void OnDisable()
    {
        if (ready && animator) animator.speed = previousSpeed;
        if (ready && previewCamera) previewCamera.rect = previousCameraRect;
    }

    void LateUpdate()
    {
        if (previewCamera && Screen.width > 0)
        {
            float scale = Mathf.Clamp(Screen.width / 1100f, .65f, 1.5f);
            float reserved = Mathf.Min(.55f, 454 * scale / Screen.width);
            previewCamera.rect = new Rect(reserved, 0, 1 - reserved, 1);
            if (previousWidth != Screen.width || previousHeight != Screen.height)
            {
                previousWidth = Screen.width; previousHeight = Screen.height;
                float vertical = previewCamera.fieldOfView * Mathf.Deg2Rad / 2;
                float horizontal = Mathf.Atan(Mathf.Tan(vertical) * previewCamera.aspect);
                float angle = Mathf.Max(.001f, Mathf.Min(vertical, horizontal));
                cameraDistance = Mathf.Max(.00001f, cameraRadius) / Mathf.Sin(angle) * 1.25f;
                UpdateCamera();
            }
        }
    }

    void Select(int index)
    {
        if (!ready) return;
        selected = (index + clips.Length) % clips.Length;
        if (clipAvatars != null && selected < clipAvatars.Length && clipAvatars[selected]) animator.avatar = clipAvatars[selected];
        // Reset missing channels when switching clips with different binding sets.
        animator.Rebind();
        animator.Play(Animator.StringToHash(statePaths[selected]), 0, 0);
        animator.speed = paused ? 0 : speed;
        animator.Update(0);
    }

    public void SelectClip(int index) { Select(index); }
    public string DisplayName(int index) { return displayNames != null && index >= 0 && index < displayNames.Length && !string.IsNullOrEmpty(displayNames[index]) ? displayNames[index] : clips[index].name; }
    public void Restart() { Select(selected); }
    public void SeekNormalized(float value) { Seek(value); }
    public void SetPaused(bool value) { paused = value; if (ready && animator) animator.speed = paused ? 0 : speed; }
    public void SetSpeed(float value) { speed = Mathf.Clamp(value, .1f, 2); if (ready && animator) animator.speed = paused ? 0 : speed; }

    void Seek(float normalized)
    {
        if (!ready) return;
        animator.Play(Animator.StringToHash(statePaths[selected]), 0, Mathf.Clamp01(normalized));
        animator.Update(0);
    }

    float NormalizedTime()
    {
        float value = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
        if (float.IsNaN(value) || float.IsInfinity(value)) return 0;
        return clips[selected].isLooping ? Mathf.Repeat(value, 1) : Mathf.Clamp01(value);
    }

    void UpdateCamera()
    {
        if (!previewCamera) return;
        var offset = Quaternion.Euler(pitch, yaw, 0) * Vector3.forward * (cameraDistance * zoom);
        previewCamera.farClipPlane = Mathf.Max(previewCamera.nearClipPlane + 1, offset.magnitude + cameraRadius * 4);
        previewCamera.transform.position = cameraPivot + offset;
        previewCamera.transform.LookAt(cameraPivot);
    }

    void OnGUI()
    {
        float scale = Mathf.Clamp(Screen.width / 1100f, .65f, 1.5f);
        Matrix4x4 previousMatrix = GUI.matrix;
        bool previousEnabled = GUI.enabled;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
        try
        {
            GUILayout.BeginArea(new Rect(12, 12, 430, Mathf.Max(200, Screen.height / scale - 24)), GUI.skin.box);
            panelScroll = GUILayout.BeginScrollView(panelScroll);
            GUILayout.Label("角色动画预览");
            GUILayout.Label("基础材质预览 · 播放效果尚未验证");
            if (!string.IsNullOrEmpty(error)) { GUILayout.Label(error); GUILayout.EndScrollView(); GUILayout.EndArea(); return; }
            if (!ready) { GUILayout.Label("正在准备播放…"); GUILayout.EndScrollView(); GUILayout.EndArea(); return; }
            GUILayout.Label((selected + 1) + " / " + clips.Length + "   " + DisplayName(selected));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("上一个")) Select(selected - 1);
            if (GUILayout.Button(paused ? "继续" : "暂停")) SetPaused(!paused);
            if (GUILayout.Button("重播")) Restart();
            if (GUILayout.Button("下一个")) Select(selected + 1);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("速度 " + speed.ToString("0.00") + "×", GUILayout.Width(105));
            float nextSpeed = GUILayout.HorizontalSlider(speed, .1f, 2);
            if (!Mathf.Approximately(nextSpeed, speed)) SetSpeed(nextSpeed);
            GUILayout.EndHorizontal();
            float time = NormalizedTime();
            GUILayout.Label((time * clips[selected].length).ToString("0.00") + " / " + clips[selected].length.ToString("0.00") + " 秒"
                + (clips[selected].isLooping ? "  循环" : "  单次"));
            float nextTime = GUILayout.HorizontalSlider(time, 0, 1);
            if (!Mathf.Approximately(nextTime, time)) Seek(nextTime);
            GUILayout.BeginHorizontal();
            GUI.enabled = paused;
            float frame = 1 / (Mathf.Max(1, clips[selected].frameRate) * Mathf.Max(.001f, clips[selected].length));
            if (GUILayout.Button("上一帧")) Seek(time - frame);
            if (GUILayout.Button("下一帧")) Seek(time + frame);
            GUI.enabled = previousEnabled;
            GUILayout.EndHorizontal();

            GUILayout.Label("动画列表");
            listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(Mathf.Clamp(Screen.height / scale - 480, 65, 185)));
            for (int i = 0; i < clips.Length; i++)
                if (GUILayout.Button((i == selected ? "▶ " : "") + DisplayName(i))) Select(i);
            GUILayout.EndScrollView();
            GUILayout.Label("视角");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("左转")) { yaw -= 20; UpdateCamera(); }
            if (GUILayout.Button("右转")) { yaw += 20; UpdateCamera(); }
            if (GUILayout.Button("拉近")) { zoom = Mathf.Max(.25f, zoom * .8f); UpdateCamera(); }
            if (GUILayout.Button("拉远")) { zoom = Mathf.Min(5, zoom * 1.25f); UpdateCamera(); }
            if (GUILayout.Button("复位")) { yaw = 0; pitch = 4; zoom = 1; UpdateCamera(); }
            GUILayout.EndHorizontal();
            GUILayout.Label("暂停后拖动进度或逐帧查看；关闭 Play 后恢复场景。\n该场景未复原游戏布料、IK 或专用着色器。");
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }
        finally { GUI.matrix = previousMatrix; GUI.enabled = previousEnabled; }
    }
}
}
