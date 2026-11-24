using System.Numerics;
using Hexa.NET.ImGui;

namespace VfxStudio.Re4Preview;

/// <summary>The AI control channel as the MCP server sees it: whether the pipe listens, who may connect,
/// the request in progress and the latest answered connections.</summary>
internal sealed class PreviewMcpPanel
{
    public const string Title = "MCP 状态";
    public bool Visible = true;
    private bool errorsOnly;

    // TextColored and TextDisabled do not wrap; the panel is often narrow.
    private static void Wrapped(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
    private static void Dim(string text) => Wrapped(ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled], text);

    private static readonly Vector4 Good = new(.45f, .85f, .5f, 1), Bad = new(1, .45f, .35f, 1), Warn = new(1, .75f, .35f, 1);

    /// <param name="disabled">Why the window has no control pipe at all (automated runs, --no-ai-control), or null.</param>
    public void Draw(PreviewControlServer? control, PreviewControlRequest? active, string? disabled)
    {
        if (!Visible) return;
        ImGui.SetNextWindowSize(new Vector2(420, 520), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(Title, ref Visible)) { ImGui.End(); return; }
        if (control == null)
        {
            ImGui.TextColored(Warn, "AI 控制通道未开启");
            ImGui.TextWrapped(disabled ?? "本窗口不接受 MCP 请求。");
            ImGui.End(); return;
        }

        if (control.Error is { } error)
        {
            ImGui.TextColored(Bad, "通道不可用");
            ImGui.TextWrapped(error);
        }
        else ImGui.TextColored(Good, active != null || control.Connected ? "已连接" : "正在监听");
        Dim($"管道 \\\\.\\pipe\\{control.PipeName} · pid {Environment.ProcessId}");
        Dim("权限：" + (control.Elevated ? "管理员" : "普通用户") + $" · 开启于 {control.StartedAt:HH:mm:ss} · 已应答 {control.Connections} 次连接");
        if (control.Elevated)
            Wrapped(Warn, "本窗口以管理员身份运行：没有管理员权限的 MCP 客户端连接时会被拒绝访问，这类请求到不了本窗口，也不会出现在下面的记录里。");
        else Dim("管道只接受当前用户、同样没有管理员权限的进程。");

        ImGui.Separator();
        if (active != null)
        {
            ImGui.TextUnformatted($"正在执行：{active.Action}");
            Dim($"请求 {active.Id} · 剩余 {Math.Max(0, (active.Deadline - DateTime.UtcNow).TotalSeconds):F0} s 超时");
        }
        else ImGui.TextDisabled("没有正在执行的请求");
        if (control.Queued > 0) ImGui.TextDisabled($"排队 {control.Queued} 个");

        ImGui.Separator();
        var log = control.Log();
        ImGui.TextUnformatted($"最近的请求（{log.Length}）");
        ImGui.SameLine(); ImGui.Checkbox("只看失败", ref errorsOnly);
        if (ImGui.BeginTable("mcp-log", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("时间", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("耗时", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("结果", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();
            for (int i = log.Length - 1; i >= 0; i--)
            {
                var entry = log[i];
                if (errorsOnly && entry.Ok) continue;
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Time.ToString("HH:mm:ss"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Action ?? "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Milliseconds >= 1000 ? $"{entry.Milliseconds / 1000:F1} s" : $"{entry.Milliseconds:F0} ms");
                ImGui.TableNextColumn();
                if (entry.Ok) ImGui.TextColored(Good, "成功");
                else ImGui.TextColored(Bad, entry.Code ?? "失败");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip((entry.Message ?? (entry.Ok ? "成功" : "")) + (entry.ClientPid is int pid ? $"\n客户端 pid {pid}" : ""));
            }
            ImGui.EndTable();
        }
        ImGui.End();
    }
}
