using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace CoreSpectre.Services;

/// <summary>
/// Dependency-free Win32 Shell_NotifyIcon tray integration, balloon alerts, and RegisterHotKey manager.
/// </summary>
public sealed class TrayAndHotkeyService : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 42;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_INFO = 0x00000001;
    private const uint NIIF_WARNING = 0x00000002;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_H = 0x48;
    private const uint VK_L = 0x4C;
    private const uint VK_P = 0x50;

    private const int HOTKEY_HUD_TOGGLE = 9001;
    private const int HOTKEY_HUD_LOCK = 9002;
    private const int HOTKEY_QUICK_PURGE = 9003;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractAssociatedIconW(IntPtr hInst, System.Text.StringBuilder iconPath, ref ushort lpiIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly Window _owner;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private uint _taskbarCreatedMsg;
    private bool _trayAdded;
    private bool _hotkeysRegistered;
    private bool _disposed;

    public bool IsTrayAvailable => _trayAdded;

    public event Action? OnRestoreRequested;
    public event Action? OnToggleHudRequested;
    public event Action? OnToggleHudLockRequested;
    public event Action? OnQuickPurgeRequested;
    public event Action? OnExitRequested;

    public TrayAndHotkeyService(Window owner)
    {
        _owner = owner;
    }

    public void Initialize(bool enableHotkeys)
    {
        var helper = new WindowInteropHelper(_owner);
        _hwnd = helper.EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        string exePath = Environment.ProcessPath ?? "";
        if (!string.IsNullOrEmpty(exePath))
        {
            ushort idx = 0;
            var pathBuf = new System.Text.StringBuilder(exePath, 260);
            _hIcon = ExtractAssociatedIconW(IntPtr.Zero, pathBuf, ref idx);
        }

        AddTrayIcon();
        SetHotkeysEnabled(enableHotkeys);
    }

    private void AddTrayIcon()
    {
        if (_hwnd == IntPtr.Zero) return;
        var nid = CreateBaseNotifyIconData();
        nid.uFlags = NIF_MESSAGE | NIF_TIP | (_hIcon != IntPtr.Zero ? NIF_ICON : 0u);
        nid.uCallbackMessage = WM_TRAYICON;
        nid.hIcon = _hIcon;
        nid.szTip = "CoreSpectre";
        _trayAdded = Shell_NotifyIconW(NIM_ADD, ref nid);
    }

    public void SetHotkeysEnabled(bool enabled)
    {
        if (_hwnd == IntPtr.Zero) return;

        if (enabled && !_hotkeysRegistered)
        {
            const uint modifiers = MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;
            bool h1 = RegisterHotKey(_hwnd, HOTKEY_HUD_TOGGLE, modifiers, VK_H);
            bool h2 = RegisterHotKey(_hwnd, HOTKEY_HUD_LOCK, modifiers, VK_L);
            bool h3 = RegisterHotKey(_hwnd, HOTKEY_QUICK_PURGE, modifiers, VK_P);
            if (!h1 || !h2 || !h3)
            {
                Logger.Warn($"Global hotkey registration partial/failed (H={h1}, L={h2}, P={h3}).");
            }
            _hotkeysRegistered = h1 || h2 || h3;
        }
        else if (!enabled && _hotkeysRegistered)
        {
            UnregisterHotKey(_hwnd, HOTKEY_HUD_TOGGLE);
            UnregisterHotKey(_hwnd, HOTKEY_HUD_LOCK);
            UnregisterHotKey(_hwnd, HOTKEY_QUICK_PURGE);
            _hotkeysRegistered = false;
        }
    }

    public void ShowBalloon(string title, string message, bool isWarning = false)
    {
        if (!_trayAdded || _hwnd == IntPtr.Zero) return;

        var nid = CreateBaseNotifyIconData();
        nid.uFlags = NIF_INFO;
        nid.szInfoTitle = title.Length > 63 ? title[..63] : title;
        nid.szInfo = message.Length > 255 ? message[..255] : message;
        nid.dwInfoFlags = isWarning ? NIIF_WARNING : NIIF_INFO;
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    private NOTIFYICONDATA CreateBaseNotifyIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = "CoreSpectre",
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
        {
            AddTrayIcon();
        }
        else if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HOTKEY_HUD_TOGGLE)
            {
                OnToggleHudRequested?.Invoke();
                handled = true;
            }
            else if (id == HOTKEY_HUD_LOCK)
            {
                OnToggleHudLockRequested?.Invoke();
                handled = true;
            }
            else if (id == HOTKEY_QUICK_PURGE)
            {
                OnQuickPurgeRequested?.Invoke();
                handled = true;
            }
        }
        else if (msg == WM_TRAYICON)
        {
            int mouseMsg = lParam.ToInt32() & 0xFFFF;
            if (mouseMsg == WM_LBUTTONDBLCLK || mouseMsg == WM_LBUTTONUP)
            {
                OnRestoreRequested?.Invoke();
                handled = true;
            }
            else if (mouseMsg == WM_RBUTTONUP)
            {
                ShowTrayContextMenu();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowTrayContextMenu()
    {
        bool isEn = LocalizationService.IsEnglish;
        SetForegroundWindow(_hwnd);

        var cm = new ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
        };

        var miOpen = new MenuItem { Header = isEn ? "Open CoreSpectre" : "Открыть CoreSpectre" };
        miOpen.Click += (_, _) => OnRestoreRequested?.Invoke();
        cm.Items.Add(miOpen);

        var miHud = new MenuItem { Header = isEn ? "Toggle HUD (Ctrl+Shift+H)" : "Переключить HUD (Ctrl+Shift+H)" };
        miHud.Click += (_, _) => OnToggleHudRequested?.Invoke();
        cm.Items.Add(miHud);

        var miLock = new MenuItem { Header = isEn ? "HUD Click-Through (Ctrl+Shift+L)" : "HUD Click-Through (Ctrl+Shift+L)" };
        miLock.Click += (_, _) => OnToggleHudLockRequested?.Invoke();
        cm.Items.Add(miLock);

        cm.Items.Add(new Separator());

        var miPurge = new MenuItem { Header = isEn ? "Flush RAM Cache" : "Сбросить кэш ОЗУ" };
        miPurge.Click += (_, _) => OnQuickPurgeRequested?.Invoke();
        cm.Items.Add(miPurge);

        cm.Items.Add(new Separator());

        var miExit = new MenuItem { Header = isEn ? "Exit" : "Выход" };
        miExit.Click += (_, _) => OnExitRequested?.Invoke();
        cm.Items.Add(miExit);

        cm.IsOpen = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SetHotkeysEnabled(false);

        if (_trayAdded && _hwnd != IntPtr.Zero)
        {
            var nid = CreateBaseNotifyIconData();
            Shell_NotifyIconW(NIM_DELETE, ref nid);
            _trayAdded = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }
}
