using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FileOrganizer.Widgets.QuickLook;

/// <summary>前面Explorerの選択と、誤爆防止に必要なフォーカス/IME状態だけを取得する。</summary>
public sealed class ExplorerSelectionProvider
{
    private const int GcsCompStr = 0x0008;

    public QuickLookActivationContext Capture()
    {
        IntPtr foreground = GetForegroundWindow();
        bool isExplorer = IsExplorerWindow(foreground);
        if (!isExplorer)
        {
            // プロセス判定で外れた時点で、IME・コントロール・COM選択取得へ進まない。
            return new QuickLookActivationContext(
                IsSpaceKeyDown: true,
                IsImeComposing: false,
                IsExplorerForeground: false,
                IsEditControlFocused: false,
                IsFileListFocused: false,
                IsFullScreenApplicationActive: false,
                SelectedFilePath: null);
        }

        IntPtr focus = GetFocusedControl(foreground);
        string focusClass = GetClassNameSafe(focus);
        bool editing = HasAncestorClass(focus, "Edit") || HasAncestorClass(focus, "RichEdit");
        bool fileList = HasAncestorClass(focus, "DirectUIHWND") || HasAncestorClass(focus, "SysListView32");
        bool imeComposing = focus != IntPtr.Zero && IsImeComposing(focus);
        bool fullScreen = IsFullScreen(foreground);
        string? selectedPath = fileList && !editing && !imeComposing && !fullScreen
            ? TryGetSelectedPath(foreground)
            : null;

        return new QuickLookActivationContext(
            IsSpaceKeyDown: true,
            IsImeComposing: imeComposing,
            IsExplorerForeground: isExplorer,
            IsEditControlFocused: editing || focusClass.StartsWith("Windows.UI.Core", StringComparison.OrdinalIgnoreCase),
            IsFileListFocused: fileList,
            IsFullScreenApplicationActive: fullScreen,
            SelectedFilePath: selectedPath);
    }

    private static bool IsExplorerWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out uint processId);
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static IntPtr GetFocusedControl(IntPtr foreground)
    {
        uint threadId = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(threadId, ref info) ? info.Focus : IntPtr.Zero;
    }

    private static bool HasAncestorClass(IntPtr window, string expectedPrefix)
    {
        for (IntPtr current = window; current != IntPtr.Zero; current = GetParent(current))
        {
            if (GetClassNameSafe(current).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string GetClassNameSafe(IntPtr window)
    {
        if (window == IntPtr.Zero) return string.Empty;
        var builder = new StringBuilder(256);
        return GetClassName(window, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
    }

    private static bool IsImeComposing(IntPtr focus)
    {
        IntPtr context = ImmGetContext(focus);
        if (context == IntPtr.Zero) return false;
        try { return ImmGetCompositionString(context, GcsCompStr, IntPtr.Zero, 0) > 0; }
        finally { ImmReleaseContext(focus, context); }
    }

    private static string? TryGetSelectedPath(IntPtr foreground)
    {
        object? shell = null;
        object? windowsObject = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return null;
            shell = Activator.CreateInstance(shellType);
            windowsObject = ((dynamic)shell!).Windows();
            dynamic windows = windowsObject;
            for (int index = 0; index < windows.Count; index++)
            {
                dynamic window = windows.Item(index);
                object? documentObject = null;
                object? selectedObject = null;
                object? itemObject = null;
                try
                {
                    if ((long)window.HWND != foreground.ToInt64()) continue;
                    documentObject = window.Document;
                    selectedObject = ((dynamic)documentObject).SelectedItems();
                    dynamic selected = selectedObject;
                    if (selected.Count != 1) return null;
                    itemObject = selected.Item(0);
                    string path = (string)((dynamic)itemObject).Path;
                    return File.Exists(path) ? path : null;
                }
                finally
                {
                    ReleaseCom(itemObject);
                    ReleaseCom(selectedObject);
                    ReleaseCom(documentObject);
                    if (Marshal.IsComObject(window)) Marshal.FinalReleaseComObject(window);
                }
            }
        }
        catch { return null; }
        finally
        {
            ReleaseCom(windowsObject);
            ReleaseCom(shell);
        }
        return null;
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private static bool IsFullScreen(IntPtr window)
    {
        if (window == IntPtr.Zero || !GetWindowRect(window, out Rect windowRect)) return false;
        IntPtr monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
        return windowRect.Left <= info.Monitor.Left && windowRect.Top <= info.Monitor.Top &&
               windowRect.Right >= info.Monitor.Right && windowRect.Bottom >= info.Monitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size; public uint Flags; public IntPtr Active; public IntPtr Focus; public IntPtr Capture;
        public IntPtr MenuOwner; public IntPtr MoveSize; public IntPtr Caret; public Rect CaretRect;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int maxCount);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr window);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr window, IntPtr context);
    [DllImport("imm32.dll", EntryPoint = "ImmGetCompositionStringW")]
    private static extern int ImmGetCompositionString(IntPtr context, int index, IntPtr buffer, int bufferLength);
}
