using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Ghostcam.Studio.ViewModels;

namespace Ghostcam.Studio;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private System.Drawing.Icon? _ownedTrayIcon;
    private bool _isExplicitExit;

    public bool IsExplicitExit => _isExplicitExit;

    public MainWindow()
    {
        InitializeComponent();
        _notifyIcon = CreateNotifyIcon();
    }

    private System.Windows.Forms.NotifyIcon CreateNotifyIcon()
    {
        System.Drawing.Icon trayIcon = LoadTrayIcon();

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        contextMenu.Items.Add("Open Ghostcam Studio", null, (_, _) => RestoreFromTray());
        contextMenu.Items.Add("Start / Stop Virtual Camera", null, (_, _) =>
            (DataContext as MainViewModel)?.ToggleVirtualCameraCommand.Execute(null));
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) =>
        {
            _isExplicitExit = true;
            Close();
        });

        var notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Ghostcam Studio",
            Icon = trayIcon,
            ContextMenuStrip = contextMenu,
            Visible = false
        };
        notifyIcon.DoubleClick += (_, _) => RestoreFromTray();
        return notifyIcon;
    }

    private System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var resourceInfo = Application.GetResourceStream(
                new Uri("pack://application:,,,/Ghostcam.Studio;component/ghostcam.ico", UriKind.Absolute));
            if (resourceInfo?.Stream is Stream stream)
            {
                using (stream)
                {
                    _ownedTrayIcon = new System.Drawing.Icon(stream);
                    return _ownedTrayIcon;
                }
            }
        }
        catch
        {
        }

        return System.Drawing.SystemIcons.Application;
    }

    public void MinimizeToSystemTray()
    {
        ShowInTaskbar = false;
        Hide();
        _notifyIcon.Visible = true;
        _notifyIcon.ShowBalloonTip(
            2000,
            "Ghostcam Studio",
            "Running in background (Virtual Camera active)",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    public void RestoreFromTray()
    {
        _notifyIcon.Visible = false;
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _ownedTrayIcon?.Dispose();
        _ownedTrayIcon = null;

        base.OnClosing(e);
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
