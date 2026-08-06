using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Eslee.TrayFolder.Services;
using Eslee.TrayFolder.UI;

namespace Eslee.TrayFolder.Native;

public sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly Icon _icon;
    private readonly TaskbarCreatedWindow _taskbarWindow;
    private readonly Action _togglePopup;
    private bool _disposed;

    public TrayIconController(
        Action togglePopup,
        Action openPopup,
        Action openSettings,
        Action exit,
        IAppLogger logger)
    {
        _togglePopup = togglePopup;
        _icon = CreateFolderIcon();
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add("Tray Folder 열기", null, (_, _) => openPopup());
        _contextMenu.Items.Add("설정", null, (_, _) => openSettings());
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("종료", null, (_, _) => exit());

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Tray Folder",
            ContextMenuStrip = _contextMenu,
            Visible = true,
        };
        _notifyIcon.MouseClick += OnMouseClick;

        _taskbarWindow = new TaskbarCreatedWindow(exception =>
            _ = logger.ErrorAsync(
                "Windows Explorer 재시작 감시를 초기화하지 못했습니다. 트레이 아이콘 자동 복구 감시만 비활성화합니다.",
                exception));
        _taskbarWindow.TaskbarCreated += OnTaskbarCreated;
    }

    public static PixelPoint GetCursorPosition()
    {
        if (NativeMethods.GetCursorPos(out var point))
        {
            return new PixelPoint(point.X, point.Y);
        }

        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        return new PixelPoint(work.Right, work.Bottom);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _taskbarWindow.TaskbarCreated -= OnTaskbarCreated;
        _taskbarWindow.Dispose();
        _notifyIcon.MouseClick -= OnMouseClick;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip = null;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _icon.Dispose();
    }

    private static Icon CreateFolderIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var darkBrush = new SolidBrush(Color.FromArgb(38, 93, 196)))
        using (var lightBrush = new SolidBrush(Color.FromArgb(76, 139, 245)))
        using (var highlightPen = new Pen(Color.FromArgb(215, 232, 255), 1.5f))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillRectangle(darkBrush, 5, 7, 11, 6);
            graphics.FillRectangle(lightBrush, 4, 11, 24, 16);
            graphics.DrawLine(highlightPen, 8, 16, 24, 16);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _togglePopup();
        }
    }

    private void OnTaskbarCreated(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // NotifyIcon also listens for TaskbarCreated; toggling makes recovery explicit.
        _notifyIcon.Visible = false;
        _notifyIcon.Visible = true;
    }

    private sealed class TaskbarCreatedWindow : NativeWindow, IDisposable
    {
        private readonly uint _taskbarCreatedMessage;

        public TaskbarCreatedWindow(Action<Exception> initializationFailed)
        {
            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
            if (_taskbarCreatedMessage == 0)
            {
                initializationFailed(new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "RegisterWindowMessageW가 TaskbarCreated 메시지를 등록하지 못했습니다."));
                return;
            }

            CreateHandle(new CreateParams
            {
                Caption = "Eslee.TrayFolder.TaskbarWatcher",
            });
        }

        public event EventHandler? TaskbarCreated;

        public void Dispose()
        {
            if (Handle != nint.Zero)
            {
                DestroyHandle();
            }
        }

        protected override void WndProc(ref Message message)
        {
            if ((uint)message.Msg == _taskbarCreatedMessage)
            {
                TaskbarCreated?.Invoke(this, EventArgs.Empty);
            }

            base.WndProc(ref message);
        }
    }
}
