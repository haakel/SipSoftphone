using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace SipSoftphone;

public partial class ToastWindow : Window
{
    private readonly Action _onAnswer;
    private readonly Action _onReject;
    private readonly DispatcherTimer _closeTimer;

    public ToastWindow(string from, string? name, Action onAnswer, Action onReject)
    {
        InitializeComponent();
        _onAnswer = onAnswer;
        _onReject = onReject;

        CallerName.Text = string.IsNullOrWhiteSpace(name) ? from : $"{name} — {from}";

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - Height - 12;

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _closeTimer.Tick += (_, _) => TryClose();
        _closeTimer.Start();
    }

    private void OnAnswerClick(object sender, RoutedEventArgs e)
    {
        _onAnswer();
        TryClose();
    }

    private void OnRejectClick(object sender, RoutedEventArgs e)
    {
        _onReject();
        TryClose();
    }

    private void OnBodyClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Owner is MainWindow mw)
        {
            mw.Show();
            if (mw.WindowState == WindowState.Minimized) mw.WindowState = WindowState.Normal;
            mw.Activate();
        }
        e.Handled = true;
    }

    public void TryClose()
    {
        _closeTimer.Stop();
        try { Close(); } catch { }
    }
}
