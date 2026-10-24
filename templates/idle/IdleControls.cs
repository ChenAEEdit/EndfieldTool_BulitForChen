using UnityEngine;

namespace EndfieldChenTool.Idle
{
public class IdleControls : MonoBehaviour
{
    public string[] states;
    public string[] labels;
    int selected;
    bool paused;
    Animator animator;
    void Awake() { animator = GetComponent<Animator>(); }
    void OnGUI()
    {
        if (!animator || states == null || states.Length == 0) return;
        GUI.Box(new Rect(16, 16, 300, 100 + states.Length * 32), "Chen / Idle preview");
        for (int i = 0; i < states.Length; i++)
            if (GUI.Button(new Rect(28, 48 + i * 32, 276, 28), (selected == i ? "> " : "") + labels[i]))
            { selected = i; animator.Play(states[i], 0, 0); }
        if (GUI.Button(new Rect(28, 54 + states.Length * 32, 130, 28), paused ? "Resume" : "Pause"))
        { paused = !paused; animator.speed = paused ? 0 : 1; }
        if (GUI.Button(new Rect(174, 54 + states.Length * 32, 130, 28), "Restart")) animator.Play(states[selected], 0, 0);
    }
}
}
