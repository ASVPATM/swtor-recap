using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SwtorRecap.Core;

namespace SwtorRecap.Desktop;

public sealed class StartupWindow : Window
{
    private readonly ITrackerClient _client;
    private readonly Action<TrackerSnapshot> _ready;
    private readonly CancellationTokenSource _closed = new();
    private readonly TextBlock _message = new() { Text = "Opening your history…", TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 3, Foreground = AppTheme.Accent };
    private readonly Button _retry = new() { Content = "Try again", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center };
    private bool _busy;
    public StartupWindow(ITrackerClient client, Action<TrackerSnapshot> ready)
    {
        _client = client; _ready = ready;
        Title = "SWTOR Recap"; Width = 440; Height = 350; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AppTheme.Apply(this);
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SwtorRecap/Assets/app.ico")));
        AutomationProperties.SetAutomationId(this, "startup-window");
        AutomationProperties.SetAutomationId(_retry, "startup-retry");
        var panel = new StackPanel { Spacing = 18, Margin = new Thickness(32), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://SwtorRecap/Assets/logo.png"))), Width = 84, Height = 84 });
        panel.Children.Add(new TextBlock { Text = "SWTOR Recap", FontSize = 24, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(_message); panel.Children.Add(_progress); panel.Children.Add(_retry); Content = panel;
        Opened += async (_, _) => await Connect();
        _retry.Click += async (_, _) => { (_client as ITrackerReconnect)?.PermitAgentLaunch(); await Connect(); };
        Closed += (_, _) => _closed.Cancel();
    }
    private async Task Connect()
    {
        if (_busy) return;
        _busy = true; _retry.IsVisible = false; _progress.IsVisible = true; _message.Text = "Opening your history…";
        try
        {
            var snapshot = await _client.RequestAsync<TrackerSnapshot>("Snapshot", cancellationToken: _closed.Token);
            if (_closed.IsCancellationRequested) return;
            _ready(snapshot); Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _message.Text = "Could not open the tracker. " + ex.Message;
            _progress.IsVisible = false; _retry.IsVisible = true;
            Height = 420;
        }
        finally { _busy = false; }
    }
}
