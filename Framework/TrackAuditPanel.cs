using System.Text.Json;
using EndfieldChenTool.Framework.Domain;
using EndfieldChenTool.Framework.Services;

namespace EndfieldChenTool.Framework;

internal sealed class TrackAuditPanel : UserControl
{
    private readonly TextBox search = new() { Dock = DockStyle.Fill, PlaceholderText = "查找动画、源路径、属性、目标路径或原因" };
    private readonly CheckBox lossesOnly = new() { Text = "只看未输出的轨道", Checked = true, AutoSize = true };
    private readonly Label summary = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "生成或读取结果后显示逐轨道记录。" };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White };
    private readonly List<Row> rows = [];
    private sealed record Row(string Clip, string Result, string SourceHash, string Source, string Property, string Target, string Reason, bool Written);
    public TrackAuditPanel()
    {
        Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 38)); layout.RowStyles.Add(new(SizeType.Absolute, 32));
        layout.RowStyles.Add(new(SizeType.Absolute, 42)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(search, 0, 0); layout.Controls.Add(lossesOnly, 0, 1); layout.Controls.Add(summary, 0, 2); layout.Controls.Add(grid, 0, 3);
        foreach (var column in new[] { ("源节点", 180), ("原因", 180), ("属性", 160), ("完整源路径", 380), ("完整目标路径", 380), ("动画", 160), ("结果", 160), ("原路径哈希", 110) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column.Item1, Width = column.Item2, SortMode = DataGridViewColumnSortMode.Automatic });
        Controls.Add(layout); search.TextChanged += (_, _) => RefreshRows(); lossesOnly.CheckedChanged += (_, _) => RefreshRows();
    }
    public void LoadAudit(SavedPlan saved)
    {
        rows.Clear();
        try
        {
            var report = UnityDiagnosticReport.ReadVerifiedReport(saved);
            foreach (var clip in report.GetProperty("clips").EnumerateArray())
            {
                if (!clip.TryGetProperty("tracks", out var tracks)) continue;
                bool adapted = clip.GetProperty("adapted").GetBoolean();
                foreach (var track in tracks.EnumerateArray())
                {
                    string decision = Value(track, "decision"); bool written = adapted && decision == "retain";
                    string outcome = written ? "已输出" : decision == "omit" ? "规则省略" : decision == "reject" ? "拒绝绑定" : "未输出";
                    if (!adapted && decision == "retain") outcome = "片段失败，未输出（拟保留）";
                    rows.Add(new(Value(clip, "name"), outcome, Value(track, "sourceHash"), Value(track, "sourcePath"), Value(track, "property"), Value(track, "targetPath"), Reason(Value(track, "reason")), written));
                }
            }
            summary.Text = rows.Count == 0 ? "旧版结果没有逐轨道明细。请用新版建立独立适配包。" : $"共 {rows.Count} 条属性轨道；已输出 {rows.Count(r => r.Written)}；未输出 {rows.Count(r => !r.Written)}。片段生成失败时，拟保留轨道也不算已输出。";
        }
        catch (Exception error) { summary.Text = "轨道记录尚不可读：" + error.Message; }
        RefreshRows();
    }
    private void RefreshRows()
    {
        grid.Rows.Clear(); string query = search.Text.Trim();
        foreach (var row in rows.Where(r => (!lossesOnly.Checked || !r.Written)
            && string.Join(" ", r.Clip, r.Result, r.SourceHash, r.Source, r.Property, r.Target, r.Reason).Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            int index = grid.Rows.Add(row.Source.Split('/').Last(), row.Reason, row.Property, row.Source, row.Target, row.Clip, row.Result, row.SourceHash);
            foreach (DataGridViewCell cell in grid.Rows[index].Cells) cell.ToolTipText = row.Source + "\n" + row.Property + "\n" + row.Reason + "\n目标：" + row.Target;
        }
    }
    private static string Value(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";
    internal static string Reason(string value) => value switch
    {
        "exact-path" => "完整路径对应", "unique-suffix-path" => "唯一后缀对应", "missing-target-node" => "目标骨架没有该节点",
        "unresolved-source-hash" => "尚未解析出源路径", "ambiguous-source-hash" => "源哈希对应多个路径",
        "missing-source-rest-pose" => "所选源骨架缺少该节点可核对的参考姿态",
        "ambiguous-target-path" => "目标路径有歧义", "user-excluded-node" => "用户排除节点", "user-excluded-descendant" => "用户排除后代",
        "scene-translation-disabled" => "移除场景对象位移", "unsupported-component-track" => "暂不支持该组件属性",
        "unsupported-object-reference-track" => "暂不支持对象引用轨道", "animator-track-in-generic-mode" => "Generic 模式不写入 Animator 属性",
        "multiple-source-tracks-to-one-target" => "多条源轨道指向同一目标", _ => value.Length == 0 ? "未执行或提前终止" : value
    };
}
