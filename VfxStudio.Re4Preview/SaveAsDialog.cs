using System.Runtime.InteropServices;

namespace VfxStudio.Re4Preview;

/// <summary>The Windows Save As dialog (comdlg32). It runs on its own STA thread so the render loop
/// keeps drawing while it is open. Windows asks before replacing an existing file.</summary>
static class SaveAsDialog
{
    /// <param name="owner">The preview window's HWND, so the dialog stays in front of it.</param>
    /// <param name="fileName">The proposed file name, e.g. "wp01_000.efx.5571972".</param>
    /// <returns>The chosen full path, or null when the user cancelled.</returns>
    public static Task<string?> ShowAsync(nint owner, string? directory, string fileName)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(Show(owner, directory, fileName)); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "Save As dialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static string? Show(nint owner, string? directory, string fileName)
    {
        // RE Engine files end in ".efx.<version>", so the filter matches every name ending in that
        // version and no default extension is appended.
        string extension = Path.GetExtension(fileName);
        string filter = $"EFX 文件 (*.efx{extension})\0*.efx{extension}\0所有文件 (*.*)\0*.*\0\0";
        const int capacity = 32768;
        nint buffer = Marshal.AllocHGlobal(capacity * 2);
        nint filterPtr = Marshal.StringToHGlobalUni(filter);
        nint directoryPtr = directory == null ? 0 : Marshal.StringToHGlobalUni(directory);
        nint titlePtr = Marshal.StringToHGlobalUni("另存为");
        try
        {
            var chars = new char[capacity];
            fileName.AsSpan(0, Math.Min(fileName.Length, capacity - 1)).CopyTo(chars);
            Marshal.Copy(chars, 0, buffer, capacity);
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                lpstrFilter = filterPtr,
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = capacity,
                lpstrInitialDir = directoryPtr,
                lpstrTitle = titlePtr,
                Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER | OFN_HIDEREADONLY,
            };
            if (GetSaveFileNameW(ref ofn)) return Marshal.PtrToStringUni(buffer);
            uint error = CommDlgExtendedError();
            if (error != 0) throw new InvalidOperationException($"另存为对话框打开失败（错误 0x{error:X}）。");
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(filterPtr); Marshal.FreeHGlobal(titlePtr);
            if (directoryPtr != 0) Marshal.FreeHGlobal(directoryPtr);
        }
    }

    private const int OFN_OVERWRITEPROMPT = 0x2, OFN_HIDEREADONLY = 0x4, OFN_NOCHANGEDIR = 0x8,
        OFN_PATHMUSTEXIST = 0x800, OFN_EXPLORER = 0x80000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize; public nint hwndOwner; public nint hInstance;
        public nint lpstrFilter; public nint lpstrCustomFilter; public int nMaxCustFilter; public int nFilterIndex;
        public nint lpstrFile; public int nMaxFile; public nint lpstrFileTitle; public int nMaxFileTitle;
        public nint lpstrInitialDir; public nint lpstrTitle; public int Flags;
        public short nFileOffset; public short nFileExtension; public nint lpstrDefExt;
        public nint lCustData; public nint lpfnHook; public nint lpTemplateName;
        public nint pvReserved; public int dwReserved; public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetSaveFileNameW(ref OpenFileName ofn);
    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();
}
