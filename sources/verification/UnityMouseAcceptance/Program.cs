using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

if (args.Length != 3) return 2;
string project = Path.GetFullPath(args[0]), scene = args[1], reports = Path.GetFullPath(args[2]);
if (!project.StartsWith(@"D:\AAAChatGPT\UnityValidation\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Independent projects only");
Directory.CreateDirectory(reports);
string request = Path.Combine(reports, "request.json");
string invocation = Guid.NewGuid().ToString("N");
int originX = int.Parse(Environment.GetEnvironmentVariable("ENDFIELD_MOUSE_ORIGIN_X") ?? "0");
int originY = int.Parse(Environment.GetEnvironmentVariable("ENDFIELD_MOUSE_ORIGIN_Y") ?? "0");
int listOffsetY = int.Parse(Environment.GetEnvironmentVariable("ENDFIELD_MOUSE_LIST_OFFSET_Y") ?? "0");
Write(request, new { scene, directory = reports, invocation });
File.Copy("sources/verification/UnityMouseVerification.cs", Path.Combine(project, "Assets/EndfieldCharacterValidation/Editor/UnityMouseVerification.cs"), true);
var start = new ProcessStartInfo(@"D:\Unity 3D\Editor\6000.0.84f1\Editor\Unity.exe") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Normal };
foreach (string a in new[] { "-projectPath", project, "-executeMethod", "EndfieldCharacterWorkbench.Verification.UnityMouseVerification.Begin", "-logFile", Path.Combine(reports, "unity.log") }) start.ArgumentList.Add(a);
start.Environment["ENDFIELD_MOUSE_CHECK"] = request;
using var owned = Process.Start(start)!;
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var checks = new List<object>();
try
{
    var first = await Wait(s => s.GetProperty("ready").GetBoolean() && s.GetProperty("controls").GetArrayLength() >= 12);
    Require(first.GetProperty("scene").GetString() == scene && first.GetProperty("processId").GetInt32() == owned.Id, "Actual Play in owned expected scene");
    owned.Refresh(); Native.ShowWindow(owned.MainWindowHandle, 9); Native.SetForegroundWindow(owned.MainWindowHandle); await Task.Delay(500);
    Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint foreground);
    Require(foreground == owned.Id, "Unity foreground belongs to this verification process");
    Capture("before.png");
    await Click("pause"); var paused = await Wait(s => s.GetProperty("paused").GetBoolean());
    await Task.Delay(550); var still = await Read();
    Require(Math.Abs(Number(still, "normalized") - Number(paused, "normalized")) < .0001, "Mouse pause freezes animation time");
    int selected = paused.GetProperty("selected").GetInt32();
    await Click("next"); var next = await Wait(s => s.GetProperty("selected").GetInt32() != selected);
    Require(next.GetProperty("clipHuman").GetBoolean() == next.GetProperty("avatarHuman").GetBoolean(), "Mouse switch selects matching Generic or Humanoid Avatar");
    await Click("time", .65); var seek = await Wait(s => Math.Abs(Number(s, "normalized") - .65) < .1);
    double beforeFrame = Number(seek, "normalized"); await Click("frame-next");
    var frame = await Wait(s => Number(s, "normalized") > beforeFrame + .001);
    Require(Number(frame, "normalized") < beforeFrame + .2, "Mouse single frame advances a bounded amount");
    await Click("speed", .75); await Wait(s => Number(s, "speed") > 1.25 && Number(s, "speed") < 2);
    Require(true, "Mouse speed slider changes playback speed");
    await Click("restart"); await Wait(s => Number(s, "normalized") < .02); Require(true, "Mouse restart resets paused clip time");
    await Click("previous"); var previous = await Wait(s => s.GetProperty("selected").GetInt32() == selected);
    Require(previous.GetProperty("clipHuman").GetBoolean() == previous.GetProperty("avatarHuman").GetBoolean(), "Mouse previous restores appropriate Avatar");
    await Click("view-left"); await Wait(s => Math.Abs(Number(s, "yaw") + 20) < .01);
    await Click("view-near"); await Wait(s => Number(s, "zoom") < .9);
    await Click("view-reset"); await Wait(s => Math.Abs(Number(s, "yaw")) < .01 && Math.Abs(Number(s, "zoom") - 1) < .01);
    Require(true, "Mouse camera rotate, zoom and reset");
    await Click("clip-2"); await Wait(s => s.GetProperty("selected").GetInt32() == 2); Require(true, "Mouse animation list selects clip");
    await Click("pause"); var resumed = await Wait(s => !s.GetProperty("paused").GetBoolean());
    double time = Number(resumed, "normalized"); await Wait(s => Number(s, "normalized") > time + .1);
    Require(true, "Mouse resume advances animation time");
    Capture("after.png");
    Write(Path.Combine(reports, "mouse-verification.json"), new { passed = true, actualMouseInput = true, checks, first, last = await Read() });
    return 0;
}
catch (Exception error)
{
    Capture("failure.png"); Write(Path.Combine(reports, "mouse-verification.json"), new { passed = false, actualMouseInput = true, error = error.ToString(), checks });
    Console.Error.WriteLine(error); return 1;
}
finally
{
    File.WriteAllText(Path.Combine(reports, "stop.txt"), "stop");
    if (!owned.HasExited)
    {
        try { await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) { if (!owned.HasExited) { owned.Kill(true); await owned.WaitForExitAsync(); } }
    }
}
async Task<JsonElement> Read()
{
    while (true)
    {
        deadline.Token.ThrowIfCancellationRequested();
        if (owned.HasExited) throw new Exception("Owned Unity exited: " + owned.ExitCode);
        try
        {
            using var stream = new FileStream(Path.Combine(reports, "state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var state = JsonSerializer.Deserialize<JsonElement>(stream);
            if (state.GetProperty("invocation").GetString() == invocation) return state;
        }
        catch (IOException) { } catch (JsonException) { }
        await Task.Delay(100, deadline.Token);
    }
}
async Task<JsonElement> Wait(Func<JsonElement, bool> condition)
{
    while (true) { var state = await Read(); if (condition(state)) return state; await Task.Delay(100, deadline.Token); }
}
async Task Click(string id, double fraction = .5)
{
    var state = await Read(); var rect = state.GetProperty("controls").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
    int x = originX + (int)(Number(rect, "x") + Number(rect, "width") * fraction), y = originY + (int)(Number(rect, "y") + Number(rect, "height") * .5);
    if (id.StartsWith("clip-", StringComparison.Ordinal)) y += listOffsetY;
    Require(Screen.PrimaryScreen!.Bounds.Contains(x, y), "Control on display: " + id);
    Native.SetCursorPos(x, y); await Task.Delay(180);
    uint down = Native.SendInput(1, [new Native.Input { mouse = new Native.MouseInput { flags = 2 } }], Marshal.SizeOf<Native.Input>());
    await Task.Delay(90);
    uint up = Native.SendInput(1, [new Native.Input { mouse = new Native.MouseInput { flags = 4 } }], Marshal.SizeOf<Native.Input>());
    Require(down == 1 && up == 1, "Native mouse input submitted: " + id);
    checks.Add(new { id, x, y, originX, originY, listOffsetY, stateFrame = state.GetProperty("frames").GetInt32() });
    await Task.Delay(250);
}
void Require(bool pass, string message) { if (!pass) throw new Exception(message); checks.Add(new { passed = true, message }); Console.WriteLine("PASS " + message); }
static double Number(JsonElement e, string key) => e.GetProperty(key).GetDouble();
static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
void Capture(string name)
{
    var area = Screen.PrimaryScreen!.Bounds; using var bitmap = new Bitmap(area.Width, area.Height); using var graphics = Graphics.FromImage(bitmap);
    graphics.CopyFromScreen(area.Location, Point.Empty, bitmap.Size); bitmap.Save(Path.Combine(reports, name));
}
static class Native
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int mode);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] public struct MouseInput { public int x, y; public uint data, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct Input { public uint type; public MouseInput mouse; }
}
