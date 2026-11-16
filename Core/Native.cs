using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GameModeHub.Core
{
    /// <summary>P/Invokes compartilhados.</summary>
    internal static class Native
    {
        // ---------- Janela / foco ----------
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int SW_RESTORE = 9;

        // ---------- Cursor ----------
        [DllImport("user32.dll")] public static extern bool ClipCursor(IntPtr rect);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

        // ---------- Hotkeys ----------
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        public const int WM_HOTKEY = 0x0312;

        // ---------- Energia ----------
        [DllImport("powrprof.dll")] public static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);
        [DllImport("powrprof.dll")] public static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);
        [DllImport("powrprof.dll")] public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
        [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr hMem);

        // ---------- Processos / memória / CPU ----------
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_SET_QUOTA = 0x0100;
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder exeName, ref uint size);
        [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr hProcess);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
        [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        // ---------- Mensagens ----------
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
        public const uint WM_CLOSE = 0x0010, WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x0002;
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        // ---------- Barra de título escura (Windows 10 20H1+ / 11) ----------
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19, DWMWA_CAPTION_COLOR = 35;
    }
}
