using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VfxStudio.Mcp;

/// <summary>Explains why connecting to the preview window's control pipe was refused. Both ends use
/// CurrentUserOnly, which .NET builds from the token owner: an elevated process owns its pipe as the
/// Administrators group, so a window and an MCP server on different sides of UAC cannot talk.</summary>
internal static class PipeAccessDiagnosis
{
    public static string Explain(string pipeName, string systemMessage)
    {
        using var current = Process.GetCurrentProcess();
        bool selfElevated = IsElevated(current.Id) == true;
        int session = current.SessionId;
        var windows = new List<(int Pid, bool? Elevated, int Session)>();
        foreach (var process in Process.GetProcessesByName("VfxStudio.Re4Preview"))
            using (process) windows.Add((process.Id, IsElevated(process.Id), process.SessionId));

        // A non-elevated process cannot open an elevated process's token, so unreadable means elevated here.
        bool WindowElevated((int Pid, bool? Elevated, int Session) w) => w.Elevated == true || w.Elevated == null && !selfElevated;
        string self = selfElevated ? "this MCP server runs as administrator" : "this MCP server runs without administrator rights";
        string reason;
        if (windows.Count == 0)
            reason = $"No VfxStudio.Re4Preview process is running, so another program holds the pipe name {pipeName}. Close it, or give the window and the MCP server another name (--ai-pipe / --pipe).";
        else if (windows.Any(w => w.Session != session))
            reason = "The preview window runs in another Windows session; the pipe only admits the user and session that started it.";
        else if (!selfElevated && windows.Any(WindowElevated))
            reason = "The preview window runs as administrator and " + self + "; an elevated window's pipe only admits elevated clients. " +
                "Close the window and start it without administrator rights (re4preview_launch starts it at this server's level), or run the MCP client as administrator too.";
        else if (selfElevated && windows.Any(w => w.Elevated == false))
            reason = "The preview window runs without administrator rights and " + self + "; an elevated client refuses a pipe that the Administrators group does not own. " +
                "Run the MCP client without administrator rights, or close the window and let re4preview_launch start it from this server.";
        else
            reason = $"The preview windows run at this server's level, so the name {pipeName} belongs to a pipe whose security does not admit this process (another user's or another program's).";
        string list = string.Join("; ", windows.Select(w => $"pid {w.Pid} " + (w.Elevated switch
        {
            true => "as administrator", false => "without administrator rights", null => selfElevated ? "token unreadable" : "token unreadable (elevated)"
        }) + (w.Session != session ? $", session {w.Session} (server: {session})" : "")));
        return $"Access to the RE4 preview control pipe was denied ({systemMessage}). {reason}" + (list.Length > 0 ? " Preview windows: " + list + "." : "");
    }

    /// <summary>True or false when the process token can be read, null when it cannot.</summary>
    public static bool? IsElevated(int pid)
    {
        IntPtr process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out IntPtr token)) return null;
            try { return GetTokenInformation(token, 20 /* TokenElevation */, out int elevation, 4, out _) ? elevation != 0 : null; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
}
