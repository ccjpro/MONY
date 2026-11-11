using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace MONY;

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint AddIcon = 0;
    private const uint ModifyIcon = 1;
    private const uint DeleteIcon = 2;
    private const uint SetFocus = 3;
    private const uint SetVersion = 4;
    private const uint ContextMenu = 0x007B;
    private const uint Select = 0x0400;
    private const uint KeySelect = 0x0401;
    private const uint BalloonClick = 0x0405;
    private const uint QueryEndSession = 0x0011;
    private const uint EndSession = 0x0016;
    private const uint SystemCommand = 0x0112;
    private const uint MinimizeCommand = 0xF020;
    private const uint ShowCommand = 1;
    private const uint WebCommand = 2;
    private const uint ExitCommand = 3;

    private readonly IntPtr _window;
    private readonly IntPtr _icon;
    private readonly uint _taskbarCreated;
    private readonly SubclassProc _callback;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _show;
    private readonly Func<bool> _webAvailable;
    private readonly Action _openWeb;
    private readonly Action _exit;
    private readonly Func<bool> _minimize;
    private readonly bool _subclassed;

    internal bool IsRegistered { get; private set; }

    internal TrayIcon(IntPtr window, Action show, Func<bool> webAvailable, Action openWeb, Action exit, Func<bool> minimize)
    {
        _window = window;
        _show = show;
        _webAvailable = webAvailable;
        _openWeb = openWeb;
        _exit = exit;
        _minimize = minimize;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _callback = WindowProc;
        _subclassed = SetWindowSubclass(_window, _callback, 1, 0);
        ExtractIconEx(Environment.ProcessPath!, 0, IntPtr.Zero, out _icon, 1);
        Register();
    }

    private void Register()
    {
        if (!_subclassed || _icon == IntPtr.Zero)
            return;

        var data = MakeData();
        data.Flags = 0x01 | 0x02 | 0x04 | 0x80; // Message, icon, tooltip, and the version 4 standard tooltip.
        data.Callback = CallbackMessage;
        data.Icon = _icon;
        data.Tip = UiText.T("MONY · 性能监控");
        IsRegistered = ShellNotifyIcon(AddIcon, ref data);
        if (!IsRegistered)
            return;

        data.Version = 4;
        if (!ShellNotifyIcon(SetVersion, ref data))
        {
            ShellNotifyIcon(DeleteIcon, ref data);
            IsRegistered = false;
        }
    }

    private IntPtr WindowProc(IntPtr window, uint message, nuint wParam, nint lParam, nuint id, nuint state)
    {
        if (message == CallbackMessage)
        {
            switch ((uint)lParam & 0xFFFF)
            {
                case Select or KeySelect or BalloonClick:
                    _dispatcher.TryEnqueue(() => _show());
                    break;
                case ContextMenu:
                    var x = (short)(wParam & 0xFFFF);
                    var y = (short)((wParam >> 16) & 0xFFFF);
                    _dispatcher.TryEnqueue(() => ShowMenu(x, y));
                    break;
            }
            return IntPtr.Zero;
        }

        if (message == SystemCommand && (wParam & 0xFFF0) == MinimizeCommand && _minimize())
            return IntPtr.Zero;

        if (message == _taskbarCreated)
            Register();
        else if (message == QueryEndSession)
            return new IntPtr(1);
        else if (message == EndSession && wParam != 0)
            _dispatcher.TryEnqueue(() => _exit());

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        uint command;
        try
        {
            AppendMenu(menu, 0, ShowCommand, UiText.T("打开性能监控"));
            AppendMenu(menu, _webAvailable() ? 0u : 1u, WebCommand, UiText.T("打开 Web 页面"));
            AppendMenu(menu, 0x0800, 0, null);
            AppendMenu(menu, 0, ExitCommand, UiText.T("退出 MONY"));
            SetForegroundWindow(_window);
            command = TrackPopupMenu(menu, 0x0102, x, y, 0, _window, IntPtr.Zero);
            PostMessage(_window, 0, 0, 0);
        }
        finally
        {
            DestroyMenu(menu);
        }

        switch (command)
        {
            case ShowCommand:
                _show();
                break;
            case WebCommand:
                _openWeb();
                break;
            case ExitCommand:
                _exit();
                break;
            default:
                var data = MakeData();
                ShellNotifyIcon(SetFocus, ref data);
                break;
        }
    }

    internal void Notify(string title, string message)
    {
        if (!IsRegistered) return;
        var data = MakeData();
        data.Flags = 0x10; // NIF_INFO
        data.InfoTitle = title.Length > 63 ? title[..63] : title;
        data.Info = message.Length > 255 ? message[..255] : message;
        data.InfoFlags = 0x02; // NIIF_WARNING
        ShellNotifyIcon(ModifyIcon, ref data);
    }

    private NotifyIconData MakeData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = 1,
        Tip = string.Empty,
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    public void Dispose()
    {
        if (IsRegistered)
        {
            var data = MakeData();
            ShellNotifyIcon(DeleteIcon, ref data);
            IsRegistered = false;
        }
        if (_subclassed)
            RemoveWindowSubclass(_window, _callback, 1);
        if (_icon != IntPtr.Zero)
            DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal uint Size;
        internal IntPtr Window;
        internal uint Id;
        internal uint Flags;
        internal uint Callback;
        internal IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Guid;
        internal IntPtr BalloonIcon;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr window, uint message, nuint wParam, nint lParam, nuint id, nuint state);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, nuint id, nuint state);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, nuint wParam, nint lParam);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint operation, ref NotifyIconData data);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? label);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, nuint wParam, nint lParam);
}
