using UnityEngine;

namespace EndfieldChenTool.Generated
{
public class PlaybackControls : MonoBehaviour
{
    public string clipLabel = "Walk / loop";
    Animator animator;
    bool paused;
    void Start() { animator = GetComponent<Animator>(); }
    void OnGUI()
    {
        GUI.Box(new Rect(16, 16, 280, 102), "Chen / re-extracted animation");
        GUI.Label(new Rect(28, 44, 250, 22), clipLabel);
        if (GUI.Button(new Rect(28, 77, 110, 27), paused ? "Resume" : "Pause"))
        { paused = !paused; animator.speed = paused ? 0 : 1; }
        if (GUI.Button(new Rect(150, 77, 110, 27), "Restart")) animator.Play("Walk", 0, 0);
    }
}

}
