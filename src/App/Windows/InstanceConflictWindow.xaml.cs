using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class InstanceConflictWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SingleInstanceCoordinator _coordinator;
    private bool _allowClose;
    private bool _isClosingOtherInstances;

    internal InstanceConflictWindow(SingleInstanceCoordinator coordinator)
    {
        _coordinator = coordinator;
        InitializeComponent();
    }

    internal bool ContinueWithCurrentInstance { get; private set; }

    private async void OnCloseOtherInstancesClick(object sender, RoutedEventArgs e)
    {
        if (_isClosingOtherInstances) return;
        SetBusy(true);
        try
        {
            var result = await _coordinator.CloseOtherInstancesAsync(
                TimeSpan.FromSeconds(20));
            if (result.Succeeded &&
                _coordinator.TryAcquirePrimaryInstance(TimeSpan.FromSeconds(2)))
            {
                DiagnosticLogger.Info("lifecycle", "instance_conflict_resolved",
                    ("action", "close_other"));
                ContinueWithCurrentInstance = true;
                _allowClose = true;
                DialogResult = true;
                return;
            }

            ShowCloseFailure(result.RemainingInstanceCount);
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("lifecycle",
                "instance_conflict_close_failed", error);
            ShowCloseFailure(1);
        }
    }

    private void OnCloseCurrentClick(object sender, RoutedEventArgs e)
    {
        DiagnosticLogger.Info("lifecycle", "instance_conflict_resolved",
            ("action", "close_current"));
        ContinueWithCurrentInstance = false;
        _allowClose = true;
        DialogResult = false;
    }

    private void SetBusy(bool busy)
    {
        _isClosingOtherInstances = busy;
        CloseOtherInstancesButton.IsEnabled = !busy;
        CloseCurrentInstanceButton.IsEnabled = !busy;
        HeaderCloseButton.IsEnabled = !busy;
        ErrorText.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            BusyRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(850))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                });
        }
        else
        {
            BusyRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private void ShowCloseFailure(int remainingInstanceCount)
    {
        ErrorText.Text = LocalizationService.Format(
            "CloseOtherInstancesFailedFormat", Math.Max(remainingInstanceCount, 1));
        DiagnosticLogger.Info("lifecycle", "instance_conflict_close_failed",
            ("remaining", remainingInstanceCount));
        ErrorText.Visibility = Visibility.Visible;
        SetBusy(false);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosingOtherInstances && !_allowClose)
        {
            e.Cancel = true;
            return;
        }
        if (_allowClose) return;
        ContinueWithCurrentInstance = false;
        _allowClose = true;
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
