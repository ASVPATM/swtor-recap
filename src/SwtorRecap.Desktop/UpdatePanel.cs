using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SwtorRecap.Core;
using SwtorRecap.Engine;

namespace SwtorRecap.Desktop;

public sealed class UpdatePanel : StackPanel
{
    private readonly IReleaseUpdater? _updater;
    private readonly Func<ReleaseUpdate,string,Task> _install;
    private readonly Func<bool> _canInstall;
    private readonly TextBlock _message=new(){TextWrapping=TextWrapping.Wrap};
    private readonly Button _check=new(){Content="Check for updates"};
    private readonly Button _apply=new(){Content="Download, install and reopen",IsVisible=false};
    private readonly Button _cancel=new(){Content="Cancel download",IsVisible=false};
    private readonly ProgressBar _progress=new(){Minimum=0,Maximum=100,Height=5,IsVisible=false};
    private CancellationTokenSource? _operation;
    private ReleaseUpdate? _release;
    public UpdatePanel(IReleaseUpdater? updater,Func<ReleaseUpdate,string,Task> install,Func<bool> canInstall,bool portable=false)
    {
        _updater=updater;_install=install;_canInstall=canInstall;Spacing=12;
        AutomationProperties.SetAutomationId(this,"updates-panel");
        Children.Add(new TextBlock{Text="Updates",FontSize=22});
        Children.Add(new TextBlock{Text=$"SWTOR Recap {Versions.App} · Stable releases",FontSize=14});
        Children.Add(new TextBlock{Text="Checks github.com/ASVPATM/swtor-recap when you ask. Your logs and history stay on this computer.",TextWrapping=TextWrapping.Wrap});
        if(portable)Children.Add(new TextBlock{Text="Updating a portable copy installs the new version in your Windows user account. Use the Start menu shortcut afterwards. Your history is kept.",TextWrapping=TextWrapping.Wrap});
        var row=new WrapPanel();foreach(var button in new[]{_check,_apply,_cancel}){button.Margin=new Thickness(0,0,10,0);row.Children.Add(button);}
        AutomationProperties.SetAutomationId(_check,"updates-check");AutomationProperties.SetAutomationId(_apply,"updates-install");AutomationProperties.SetAutomationId(_cancel,"updates-cancel");
        Children.Add(row);Children.Add(_progress);Children.Add(_message);
        _check.IsEnabled=updater is not null;
        _message.Text=updater is null?"Updates are available in the Windows application.":"Installation saves a recovery backup and closes Recap. Close SWTOR first. Windows may ask you to allow this unsigned application.";
        _check.Click+=async(_,_)=>await Check();
        _apply.Click+=async(_,_)=>await Install();
        _cancel.Click+=(_,_)=>_operation?.Cancel();
    }
    public void Cancel()=>_operation?.Cancel();
    private async Task Check()
    {
        _operation=new();_check.IsEnabled=false;_apply.IsVisible=false;_message.Text="Checking GitHub…";
        try{var result=await _updater!.CheckAsync(_operation.Token);_release=result.Release;_message.Text=result.Message;_apply.IsVisible=_release is not null;}
        catch(OperationCanceledException){_message.Text="The update check timed out or was cancelled. Try again.";}
        catch(Exception ex){_message.Text="Could not check for updates. "+ex.Message;}
        finally{_operation.Dispose();_operation=null;_check.IsEnabled=true;}
    }
    private async Task Install()
    {
        if(_release is null)return;
        if(!_canInstall()){_message.Text="Close SWTOR and finish or cancel imports before installing an update.";return;}
        _operation=new();_check.IsEnabled=false;_apply.IsEnabled=false;_cancel.IsVisible=true;_progress.IsVisible=true;_progress.Value=0;
        try
        {
            var path=await _updater!.DownloadAsync(_release,new Progress<int>(p=>{_progress.Value=p;_message.Text=$"Downloading version {_release.Version} · {p}%";}),_operation.Token);
            _operation.Token.ThrowIfCancellationRequested();_cancel.IsVisible=false;
            _message.Text="Download verified. Saving recovery backup…";
            await _install(_release,path);
        }
        catch(OperationCanceledException){_message.Text="Download cancelled. Your installed version is unchanged.";}
        catch(Exception ex){_message.Text="Update stopped. "+ex.Message;}
        finally{_operation.Dispose();_operation=null;_check.IsEnabled=true;_apply.IsEnabled=true;_cancel.IsVisible=false;_progress.IsVisible=false;}
    }
}
