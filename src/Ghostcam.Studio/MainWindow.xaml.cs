using System;
using System.ComponentModel;
using System.Windows;
using Ghostcam.Studio.ViewModels;

namespace Ghostcam.Studio;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
