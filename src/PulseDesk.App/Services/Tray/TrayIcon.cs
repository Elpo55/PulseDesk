using System.Runtime.InteropServices;
using static PulseDesk.App.Services.Tray.TrayNativeMethods;

namespace PulseDesk.App.Services.Tray;

/// <summary>
/// Notification-area icon with its context menu, implemented directly on <c>Shell_NotifyIcon</c>
/// (no third-party dependency). Must be created and used on the UI thread, whose message loop
/// dispatches the icon's messages.
/// </summary>
internal sealed unsafe partial class TrayIcon : IDisposable
{
    private const string WindowClassName = "PulseDesk.TrayWindow";
    private const uint CallbackMessage = WmApp + 1;
    private const uint IconId = 1;
    private const int CommandOpen = 1;
    private const int CommandPauseResume = 2;
    private const int CommandSettings = 3;
    private const int CommandExit = 4;

    // Win32 window procedures are static; only one tray icon exists per process.
    private static TrayIcon? _instance;

    private readonly uint _taskbarCreatedMessage;
    private readonly nint _module;
    private nint _window;
    private nint _icon;
    private string _tooltip = "PulseDesk";
    private bool _paused;
    private bool _added;

    public TrayIcon(string iconPath)
    {
        if (_instance is not null)
        {
            throw new InvalidOperationException("Only one tray icon can exist.");
        }

        _instance = this;
        _module = GetModuleHandle(0);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        fixed (char* className = WindowClassName)
        {
            var windowClass = new WindowClassEx
            {
                Size = (uint)sizeof(WindowClassEx),
                WindowProcedure = &WindowProcedure,
                Instance = _module,
                ClassName = className,
            };
            RegisterClassEx(windowClass);
        }

        // A hidden top-level window (not message-only) so it receives the "TaskbarCreated" broadcast
        // sent when Explorer restarts, and can restore the icon.
        _window = CreateWindowEx(0, WindowClassName, "PulseDesk", 0, 0, 0, 0, 0, 0, 0, _module, 0);
        if (_window == 0)
        {
            _instance = null;
            throw new InvalidOperationException($"The tray window could not be created (error {Marshal.GetLastPInvokeError()}).");
        }

        var dpi = GetDpiForSystem();
        _icon = LoadImage(0, iconPath, ImageIcon, GetSystemMetricsForDpi(SmCxSmIcon, dpi), GetSystemMetricsForDpi(SmCySmIcon, dpi), LrLoadFromFile);
        Add();
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? PauseResumeRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? ExitRequested;

    /// <summary>The user clicked the last notification balloon.</summary>
    public event EventHandler? BalloonClicked;

    /// <summary>Updates the text shown when hovering the icon (127 characters max).</summary>
    public void SetTooltip(string text)
    {
        _tooltip = text.Length > 127 ? text[..127] : text;
        if (_added)
        {
            var data = CreateData(NifTip | NifShowTip);
            ShellNotifyIcon(NimModify, ref data);
        }
    }

    /// <summary>Switches the menu between "Pause monitoring" and "Resume monitoring".</summary>
    public void SetPaused(bool paused) => _paused = paused;

    /// <summary>Shows a notification balloon attached to the icon.</summary>
    public void ShowInfo(string title, string text)
    {
        if (!_added)
        {
            return;
        }

        const uint nifInfo = 0x10;
        var data = CreateData(nifInfo);
        Copy(title, data.InfoTitle, 64);
        Copy(text, data.Info, 256);
        ShellNotifyIcon(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateData(0);
            ShellNotifyIcon(NimDelete, ref data);
            _added = false;
        }

        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }

        if (_window != 0)
        {
            DestroyWindow(_window);
            _window = 0;
            UnregisterClass(WindowClassName, _module);
        }

        if (_instance == this)
        {
            _instance = null;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        var instance = _instance;
        if (instance is not null && window == instance._window)
        {
            try
            {
                if (instance.HandleMessage(message, wParam, lParam))
                {
                    return 0;
                }
            }
            catch (Exception ex)
            {
                // Exceptions must never cross back into native code.
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private bool HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            // With NOTIFYICON_VERSION_4, the low word of lParam is the notification.
            return HandleCallback((uint)(lParam & 0xFFFF), wParam);
        }

        if (message == _taskbarCreatedMessage)
        {
            _added = false;
            Add();
            return true;
        }

        return false;
    }

    private bool HandleCallback(uint notification, nint wParam)
    {
        switch (notification)
        {
            case NinSelect:
            case NinKeySelect:
            case WmLButtonDblClk:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                return true;
            case NinBalloonUserClick:
                BalloonClicked?.Invoke(this, EventArgs.Empty);
                return true;
            case WmContextMenu:
                // With NOTIFYICON_VERSION_4, wParam carries the anchor point of the menu.
                ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                return true;
            default:
                return false;
        }
    }

    private void Add()
    {
        var data = CreateData(NifMessage | NifIcon | NifTip | NifShowTip);
        _added = ShellNotifyIcon(NimAdd, ref data);
        if (_added)
        {
            data.TimeoutOrVersion = NotifyIconVersion4;
            ShellNotifyIcon(NimSetVersion, ref data);
        }
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MfString | MfDefault, CommandOpen, "Open PulseDesk");
            AppendMenu(menu, MfString, CommandPauseResume, _paused ? "Resume monitoring" : "Pause monitoring");
            AppendMenu(menu, MfString, CommandSettings, "Settings");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, CommandExit, "Exit");

            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(_window);
            var command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmNoNotify | TpmBottomAlign, x, y, _window, 0);
            PostMessage(_window, WmNull, 0, 0);

            switch (command)
            {
                case CommandOpen:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandPauseResume:
                    PauseResumeRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandSettings:
                    SettingsRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandExit:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private NotifyIconData CreateData(uint flags)
    {
        var data = new NotifyIconData
        {
            Size = (uint)sizeof(NotifyIconData),
            Window = _window,
            Id = IconId,
            Flags = flags,
            CallbackMessage = CallbackMessage,
            Icon = _icon,
        };
        Copy(_tooltip, data.Tip, 128);
        return data;
    }

    private static void Copy(string text, char* destination, int capacity)
    {
        var length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(destination, capacity));
        destination[length] = '\0';
    }
}
