using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Platform;
using Avalonia.Threading;
using SwtorRecap.Core;
using System.Globalization;

namespace SwtorRecap.Desktop;

public sealed class MainWindow : Window
{
    private readonly ITrackerClient _client;
    private readonly bool _preview;
    private readonly CancellationTokenSource _closed=new();
    private readonly TextBlock _status=new(){Text="Connecting…",FontSize=13,TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _notice=new(){FontSize=13,TextWrapping=TextWrapping.Wrap};
    private readonly ContentControl _body=new();
    private readonly TextBlock _title=new(){FontSize=29,FontWeight=FontWeight.SemiBold};
    private readonly Button _reconnect;
    private readonly Dictionary<string,Button> _navigation=[];
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(2)};
    private TrackerSnapshot? _snapshot;
    private RecapSnapshot? _recap;
    private string _page="Session";
    private bool _refreshing;
    private QueryFilter _historyFilter=new();
    private int _year=DateTime.Now.Year;
    private int? _month;
    private string? _character = null;
    private StackPanel? _sessionContent;
    private TextBlock? _jobStatus;
    private int _viewRequest;
    public MainWindow(ITrackerClient client,bool preview=false,bool settings=false,TrackerSnapshot? initialSnapshot=null)
    {
        _client=client;_preview=preview;_snapshot=initialSnapshot;Title="SWTOR Recap";Width=1080;Height=780;MinWidth=740;MinHeight=540;
        AppTheme.Apply(this);
        Icon=new WindowIcon(AssetLoader.Open(new Uri("avares://SwtorRecap/Assets/app.ico")));
        _reconnect=Action("Reconnect to tracker","tracker-reconnect",async()=>{(_client as ITrackerReconnect)?.PermitAgentLaunch();await RefreshSnapshot();});_reconnect.IsVisible=false;
        AutomationProperties.SetAutomationId(this,"recap-window");
        _notice.IsVisible=false;_notice.PropertyChanged+=(_,e)=>{if(e.Property==TextBlock.TextProperty)_notice.IsVisible=!string.IsNullOrWhiteSpace(_notice.Text);};
        var nav=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4};
        var brand=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10,VerticalAlignment=VerticalAlignment.Center};
        brand.Children.Add(new Image{Source=new Bitmap(AssetLoader.Open(new Uri("avares://SwtorRecap/Assets/logo.png"))),Width=34,Height=34});
        brand.Children.Add(new TextBlock{Text="SWTOR Recap",FontSize=17,FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        foreach(var name in new[]{"Session","History","Recap","Settings"})
        {
            var button=Action(name,"nav-"+name.ToLowerInvariant(),()=>{Navigate(name);return Task.CompletedTask;});_navigation[name]=button;nav.Children.Add(button);
        }
        var top=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=18};top.Children.Add(brand);Grid.SetColumn(nav,1);top.Children.Add(nav);
        _status.Foreground=AppTheme.Muted;
        var header=new StackPanel{Spacing=10,Margin=new Thickness(30,20,22,14)};header.Children.Add(top);header.Children.Add(_status);header.Children.Add(_reconnect);
        var content=new StackPanel{Spacing=22,Margin=new Thickness(30,16,30,32)};content.Children.Add(_title);content.Children.Add(_notice);content.Children.Add(_body);
        var layout=new Grid{RowDefinitions=new RowDefinitions("Auto,*")};layout.Children.Add(header);
        var scroll=new ScrollViewer{Content=content,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroll,1);layout.Children.Add(scroll);Content=layout;
        Opened+=async(_,_)=>{if(initialSnapshot is null)await RefreshSnapshot();else _status.Text=initialSnapshot.Message;Navigate(settings?"Settings":"Session");_timer.Start();};
        _timer.Tick+=async(_,_)=>await RefreshSnapshot();
        Closed+=(_,_)=>{_timer.Stop();_closed.Cancel();};
        KeyDown+=(_,e)=>{if(e.Key==Avalonia.Input.Key.Escape){_notice.Text="";if(_page=="Detail")Navigate("History");}};
    }
    public void Navigate(string page)
    {
        _viewRequest++;
        _page=page;_title.Text=page=="Recap"?"Your recorded year":page=="Detail"?"Recording details":page;_notice.Text="";
        foreach(var (name,button) in _navigation){button.FontWeight=name==page?FontWeight.SemiBold:FontWeight.Normal;button.Background=name==page?AppTheme.Brush("#223E42"):Brushes.Transparent;button.Foreground=name==page?AppTheme.Accent:AppTheme.Muted;}
        _=Run(async()=>
        {
            switch(page)
            {
                case "Session":RenderSession();break;
                case "History":await RenderHistory();break;
                case "Recap":await RenderRecap();break;
                case "Settings":RenderSettings();break;
            }
        });
    }
    private async Task Run(Func<Task> action)
    {
        try{await action();}catch(OperationCanceledException){}catch(Exception ex){_notice.Text=ex.Message;}
    }
    private async Task RefreshSnapshot()
    {
        if(_refreshing)return;_refreshing=true;
        try
        {
            var snapshot=await _client.RequestAsync<TrackerSnapshot>("Snapshot",cancellationToken:_closed.Token);
            var changed=_snapshot?.Revision!=snapshot.Revision||_snapshot?.Current?.Id!=snapshot.Current?.Id||_snapshot?.Settings.Paused!=snapshot.Settings.Paused;_snapshot=snapshot;
            _status.Text=(_preview?"PREVIEW  ·  ":"")+snapshot.Message;
            _reconnect.IsVisible=false;
            if(_page=="Session"&&(changed||_sessionContent is null))RenderSession();
            if(_jobStatus is not null)_jobStatus.Text=string.Join("\n",snapshot.Jobs.Take(3).Select(j=>$"{j.Kind}: {j.State.Replace('_',' ')} · {j.ProcessedBytes:N0} / {j.TotalBytes:N0} bytes{(j.Message is null?"":" · "+j.Message)}"));
        }
        catch(Exception ex) when(ex is not OperationCanceledException){_status.Text="Disconnected · Last saved values retained. "+ex.Message;_reconnect.IsVisible=true;}
        finally{_refreshing=false;}
    }
    private static StackPanel Stack(params Control[] controls){var stack=new StackPanel{Spacing=12};foreach(var c in controls)stack.Children.Add(c);return stack;}
    private static TextBlock Text(string text,double size=14)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap};
    private Button Action(string label,string id,Func<Task> action)
    {
        var button=new Button{Content=label,Padding=new Thickness(14,9),Margin=new Thickness(0,0,8,8),HorizontalAlignment=HorizontalAlignment.Left};AutomationProperties.SetAutomationId(button,id);AutomationProperties.SetName(button,label);
        button.Click+=async(_,_)=>{button.IsEnabled=false;try{await Run(action);}finally{button.IsEnabled=true;}};return button;
    }
    private static string Count(long value)=>value.ToString("N0",CultureInfo.CurrentCulture);
    public static string DisplayDate(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return "Undated recording";
        if(value.Length==10&&DateOnly.TryParse(value,CultureInfo.InvariantCulture,out var date))return date.ToString("MMM d, yyyy",CultureInfo.CurrentCulture);
        return DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.None,out var timestamp)?timestamp.ToLocalTime().ToString("MMM d, yyyy · h:mm tt",CultureInfo.CurrentCulture):value;
    }
    public static string Duration(long milliseconds)=>TimeSpan.FromMilliseconds(milliseconds).TotalHours>=1?$"{(int)TimeSpan.FromMilliseconds(milliseconds).TotalHours}h {TimeSpan.FromMilliseconds(milliseconds).Minutes}m":$"{TimeSpan.FromMilliseconds(milliseconds).Minutes}m {TimeSpan.FromMilliseconds(milliseconds).Seconds}s";
    public static string GameTimeLabel(Totals totals,bool characterFiltered=false)=>characterFiltered?"Not attributed":totals.Sessions==0&&totals.Imports>0?"Not observed":Duration(totals.GameMilliseconds);
    private static Control Metrics(Totals t,bool characterFiltered=false)
    {
        var grid=new Grid{ColumnDefinitions=new ColumnDefinitions("*,*"),RowDefinitions=new RowDefinitions("Auto,Auto,Auto,Auto"),ColumnSpacing=32,RowSpacing=26};
        var values=new[]{("Game-open time",GameTimeLabel(t,characterFiltered),"gameTime"),("Combat time",Duration(t.CombatMilliseconds),"combatTime"),("Damage dealt",t.Amounts is null?"Unavailable":Count(t.Amounts.Damage),"damage"),("Healing done",t.Amounts is null?"Unavailable":Count(t.Amounts.Healing),"healing"),("NPC kills",Count(t.Kills)+(t.KillsNeedReprocess?"+":""),"kills"),("Player deaths",Count(t.Deaths),"deaths"),("Boss encounters won",Count(t.Bosses),"bosses"),("Flashpoint / operation clears",$"{t.Flashpoints:N0}  /  {t.Operations:N0}","clears")};
        for(var i=0;i<values.Length;i++)
        {
            var value=values[i];var label=Text(value.Item1,13);label.Foreground=AppTheme.Muted;var number=Text(value.Item2,29);number.FontWeight=FontWeight.SemiBold;
            var stack=Stack(label,number);stack.Spacing=6;ToolTip.SetTip(stack,MetricDefinitions.All[value.Item3]);
            if(value.Item3 == "kills"){var note=Text(t.KillsNeedReprocess?"Reprocess saved logs to include older recordings":"Your damage > 50% of maximum health",12);note.Foreground=AppTheme.Muted;stack.Children.Add(note);}
            if(value.Item3 is "healing" or "clears"){var note=Text(value.Item3=="healing"?"Your raw healing, including self-heals":"Final boss defeated · mission turn-in excluded",12);note.Foreground=AppTheme.Muted;stack.Children.Add(note);}
            Grid.SetColumn(stack,i%2);Grid.SetRow(stack,i/2);grid.Children.Add(stack);
        }
        return new Border{Child=grid,Padding=new Thickness(24),CornerRadius=new CornerRadius(12),Background=AppTheme.Surface};
    }
    private void RenderSession()
    {
        _sessionContent=new(){Spacing=20};AutomationProperties.SetAutomationId(_sessionContent,"session-view");_body.Content=_sessionContent;
        if(_snapshot?.Current is not { } item)
        {
            _title.Text="Ready when you are";
            _sessionContent.Children.Add(Text("Start SWTOR to begin a new session.",20));
            _sessionContent.Children.Add(Text("Your finished recordings are saved in History. Combat logging must be enabled in SWTOR."));
            var idleActions=new WrapPanel();idleActions.Children.Add(Action("View history","session-history",()=>{Navigate("History");return Task.CompletedTask;}));
            idleActions.Children.Add(Action("Recording settings","session-setup",()=>{Navigate("Settings");return Task.CompletedTask;}));_sessionContent.Children.Add(idleActions);return;
        }
        _title.Text="Current session";
        _sessionContent.Children.Add(Text(string.Join(" · ",item.Characters.DefaultIfEmpty("Character not identified")),20));
        _sessionContent.Children.Add(Text($"{DisplayDate(item.StartedUtc)}{(_preview?" · Sample recording":"")}",13));
        _sessionContent.Children.Add(Metrics(item.Totals));
        _sessionContent.Children.Add(Text($"{item.Totals.Deaths:N0} player deaths · {item.Totals.FullRuns:N0} full runs observed"+(item.Totals.Lairs>0?$" · {item.Totals.Lairs:N0} lair clears":"")));
        if(item.GapCount>0)_sessionContent.Children.Add(Text("Partial coverage · Open recording details to inspect the affected data."));
        var actions=new WrapPanel{Orientation=Orientation.Horizontal};actions.Children.Add(Action("Recording details","session-details",()=>RenderDetail(item.Id)));
        actions.Children.Add(Action(_snapshot.Settings.Paused?"Resume recording":"Pause recording","session-pause",async()=>{await _client.RequestAsync<MutationResult>("Pause",!_snapshot.Settings.Paused);await RefreshSnapshot();}));_sessionContent.Children.Add(actions);
    }
    private async Task RenderHistory()
    {
        var request=++_viewRequest;var page=await _client.RequestAsync<HistoryPage>("History",_historyFilter,_closed.Token);if(request!=_viewRequest)return;
        var panel=new StackPanel{Spacing=14};AutomationProperties.SetAutomationId(panel,"history-view");
        var filters=new WrapPanel();
        var year=new TextBox{Text=_historyFilter.Year?.ToString()??"",PlaceholderText="All years",Width=120,Margin=new Thickness(0,0,10,10)};AutomationProperties.SetName(year,"History year");
        var characters=new ComboBox{ItemsSource=new[]{"All characters"}.Concat(page.Characters.Select(c=>c.Name)).ToArray(),SelectedIndex=Math.Max(0,Array.FindIndex(page.Characters,c=>c.Id==_historyFilter.CharacterId)+1),MinWidth=190};AutomationProperties.SetName(characters,"History character");
        characters.Margin=new Thickness(0,0,10,10);filters.Children.Add(year);filters.Children.Add(characters);
        filters.Children.Add(Action("Apply filters","history-filter",async()=>{_historyFilter=new(Year:int.TryParse(year.Text,out var y)?y:null,CharacterId:characters.SelectedIndex>0?page.Characters[characters.SelectedIndex-1].Id:null);await RenderHistory();}));panel.Children.Add(filters);
        if(page.Items.Length==0)panel.Children.Add(Text("No recordings match these filters. Import old logs or start SWTOR with combat logging enabled.",17));
        foreach(var item in page.Items)
        {
            var details=Stack(Text($"{DisplayDate(item.StartedUtc)}  ·  {(item.Kind=="import"?"Imported capture":"Game session")}",16),Text(string.Join(" · ",item.Characters.DefaultIfEmpty("Unresolved owner"))),
                Text($"Damage { (item.Totals.Amounts is null?"unavailable":Count(item.Totals.Amounts.Damage))}    Healing {(item.Totals.Amounts is null?"unavailable":Count(item.Totals.Amounts.Healing))}    Combat {Duration(item.Totals.CombatMilliseconds)}    NPC kills {item.Totals.Kills:N0}    Bosses {item.Totals.Bosses:N0}{(item.GapCount>0?"    Partial coverage":"")}"));
            var button=new Button{Content=details,Background=AppTheme.Surface,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(20,18)};
            button.Click+=async(_,_)=>await Run(()=>RenderDetail(item.Id));panel.Children.Add(button);
        }
        var paging=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};
        if(_historyFilter.Offset>0)paging.Children.Add(Action("Previous","history-previous",async()=>{_historyFilter=_historyFilter with{Offset=Math.Max(0,_historyFilter.Offset-_historyFilter.Limit)};await RenderHistory();}));
        if(page.HasMore)paging.Children.Add(Action("Next","history-next",async()=>{_historyFilter=_historyFilter with{Offset=_historyFilter.Offset+_historyFilter.Limit};await RenderHistory();}));panel.Children.Add(paging);_body.Content=panel;
    }
    private async Task RenderDetail(string id)
    {
        var request=++_viewRequest;var detail=await _client.RequestAsync<HistoryDetail>("Detail",id,_closed.Token);if(request!=_viewRequest)return;_page="Detail";_title.Text="Recording details";
        var panel=Stack(Action("Back to history","detail-back",()=>{Navigate("History");return Task.CompletedTask;}),Text(string.Join(" · ",detail.Item.Characters),20),Metrics(detail.Item.Totals));
        if(detail.Item.Totals.Amounts is { } amounts)panel.Children.Add(Text($"Effective damage: {amounts.EffectiveDamage:N0} ({amounts.DamageUnknown:N0} events unknown) · Effective healing: {amounts.EffectiveHealing:N0} ({amounts.HealingUnknown:N0} events unknown)"));
        if(detail.Item.Totals.KillsNeedReprocess)panel.Children.Add(Text("Older recordings need reprocessing to include NPC kills. Use Settings → Reprocess saved history."));
        var kills=Stack(Text(MetricDefinitions.All["kills"]));
        foreach(var kill in detail.Kills??[])kills.Children.Add(Text($"{kill.Name} · {kill.Count:N0}"));
        if(detail.Kills is not {Length:>0})kills.Children.Add(Text("No qualifying NPC kills recorded."));
        panel.Children.Add(new Expander{Header=$"NPC kills · {detail.Item.Totals.Kills:N0}",Content=kills});
        panel.Children.Add(Text("Activity timeline",20));
        if(detail.Outcomes.Length==0)panel.Children.Add(Text("No supported boss outcome recorded."));
        foreach(var outcome in detail.Outcomes)panel.Children.Add(Text($"{outcome.Name} · {outcome.Activity} · {outcome.Result.Replace('_',' ')} ({outcome.Basis}){(!outcome.Qualified?" · participation unconfirmed":"")}{(outcome.Terminal?" · terminal clear":"")}{(outcome.Full?" · full run observed":"")}"));
        var evidence=Stack();foreach(var source in detail.Sources)evidence.Children.Add(Text($"{source.Filename} · {source.Offset:N0}/{source.Length:N0} bytes · {source.Accepted:N0} accepted · {source.Rejected:N0} rejected · {source.Integrity}"));
        foreach(var gap in detail.Gaps)evidence.Children.Add(Text($"{gap.Dimension}: {gap.Reason.Replace('_',' ')} · bytes {gap.Start:N0}–{gap.End:N0}"));
        panel.Children.Add(new Expander{Header="Source coverage and evidence",Content=evidence});_body.Content=panel;
        foreach(var source in detail.Sources.Where(s=>s.Candidates?.Length>0))
        {
            var candidates=source.Candidates!;
            var owner=new ComboBox{ItemsSource=candidates.Select(c=>$"{c.Name} · {c.Id}").ToArray(),SelectedIndex=0,MinWidth=240};AutomationProperties.SetName(owner,"Recording owner for "+source.Filename);
            panel.Children.Add(Stack(Text("Choose the owner of unresolved records · "+source.Filename,18),owner,
                Action("Apply owner and reprocess","owner-"+source.Id,async()=>
                {
                    _notice.Text="Reprocessing this attribution…";
                    var result=await _client.RequestAsync<MutationResult>("AttributeOwner",new[]{source.Id,candidates[owner.SelectedIndex].Id});
                    await RefreshSnapshot();await RenderDetail(id);_notice.Text=result.Message;
                })));
        }
    }
    private async Task RenderRecap()
    {
        var request=++_viewRequest;var filter=new QueryFilter(_year,_month,_character);
        var recap=await _client.RequestAsync<RecapSnapshot>("Recap",filter,_closed.Token);
        var history=await _client.RequestAsync<HistoryPage>("History",new QueryFilter(Limit:1),_closed.Token);
        if(request!=_viewRequest)return;_recap=recap;
        _title.Text=_month is null?$"Your recorded {_year}":$"{new DateTime(_year,_month.Value,1):MMMM yyyy}";
        var panel=new StackPanel{Spacing=20};AutomationProperties.SetAutomationId(panel,"recap-view");
        var filters=new WrapPanel();var year=new NumericUpDown{Minimum=1900,Maximum=2200,Value=_year,Width=130};AutomationProperties.SetName(year,"Recap year");
        var month=new ComboBox{ItemsSource=new[]{"Full year"}.Concat(CultureInfo.CurrentCulture.DateTimeFormat.MonthNames.Take(12)).ToArray(),SelectedIndex=_month??0,Width=170};AutomationProperties.SetName(month,"Recap period");
        var characters=new ComboBox{ItemsSource=new[]{"All characters"}.Concat(history.Characters.Select(c=>c.Name)).ToArray(),SelectedIndex=Math.Max(0,Array.FindIndex(history.Characters,c=>c.Id==_character)+1),MinWidth=170,MaxWidth=280};AutomationProperties.SetAutomationId(characters,"recap-character");AutomationProperties.SetName(characters,"Recap character");
        foreach(var control in new Control[]{year,month,characters}){control.Margin=new Thickness(0,0,10,10);filters.Children.Add(control);}
        filters.Children.Add(Action("View recap","recap-filter",async()=>{_year=(int)(year.Value??DateTime.Now.Year);_month=month.SelectedIndex==0?null:month.SelectedIndex;_character=characters.SelectedIndex>0?history.Characters[characters.SelectedIndex-1].Id:null;await RenderRecap();}));
        filters.Children.Add(Action("Export recap image","recap-export",()=>ExportRecap(recap)));panel.Children.Add(filters);
        panel.Children.Add(Text($"{(recap.PeriodOpen?"Period in progress · ":"")}{recap.Points.Length:N0} recorded days · {recap.Totals.Sessions:N0} observed sessions · {recap.Totals.Imports:N0} imported captures · {recap.ReportingZone}",13));
        panel.Children.Add(Metrics(recap.Totals,recap.CharacterFilter is not null));
        panel.Children.Add(Text("Your rhythm",22));
        var groups=recap.Points.GroupBy(p=>_month is null?p.Date[..7]:p.Date).Select(g=>(Label:g.Key,Combat:g.Sum(p=>p.CombatMilliseconds),Damage:g.Sum(p=>p.Damage))).ToArray();
        var maximum=groups.Select(g=>g.Combat).DefaultIfEmpty(1).Max();
        foreach(var group in groups)
        {
            var row=new Grid{ColumnDefinitions=new ColumnDefinitions("100,*,150"),ColumnSpacing=12};row.Children.Add(Text(group.Label));
            var progress=new ProgressBar{Minimum=0,Maximum=Math.Max(1,maximum),Value=group.Combat,Foreground=AppTheme.Accent,Background=AppTheme.Surface,Height=8,VerticalAlignment=VerticalAlignment.Center};AutomationProperties.SetName(progress,group.Label+" combat time "+Duration(group.Combat));Grid.SetColumn(progress,1);row.Children.Add(progress);
            var value=Text(Duration(group.Combat));Grid.SetColumn(value,2);row.Children.Add(value);panel.Children.Add(row);
        }
        if(groups.Length==0)panel.Children.Add(Text("No dated recordings in this period. Undated captures remain in History."));
        panel.Children.Add(Text("Where you played",22));
        foreach(var activity in recap.Activities)panel.Children.Add(Text($"{activity.Name} · {activity.Clears:N0} terminal clears · first recorded in this period {activity.FirstRecordedDate}"));
        if(recap.Activities.Length==0)panel.Children.Add(Text("No supported terminal clears recorded in this period."));
        panel.Children.Add(Text("Your characters · logged combat time",22));
        foreach(var character in recap.Characters)panel.Children.Add(Text($"{character.Name}    {Duration(character.CombatMilliseconds)}    {character.Damage:N0} damage    {character.Healing:N0} healing"));
        if(recap.LongestSession is { } longest)panel.Children.Add(Text($"Longest recorded session: {Duration(longest.Totals.GameMilliseconds)} · {DisplayDate(longest.StartedUtc)}"));
        panel.Children.Add(new Expander{Header="Coverage and metric definitions",Content=Stack(recap.Coverage.Select(x=>(Control)Text(x)).Concat(MetricDefinitions.All.Select(x=>(Control)Text(x.Value))).ToArray())});_body.Content=panel;
    }
    private void RenderSettings()
    {
        var snapshot=_snapshot;if(snapshot is null){_body.Content=Text("Connect to the tracker to load settings.");return;}
        var settings=snapshot.Settings;var panel=new StackPanel{Spacing=18};AutomationProperties.SetAutomationId(panel,"settings-view");
        panel.Children.Add(Text("Recording",22));panel.Children.Add(Text("SWTOR creates the combat logs. Recap reads them without changing them; StarParse can read the same files."));
        panel.Children.Add(Text("Combat logs · read only",13));panel.Children.Add(Text(snapshot.ResolvedLogFolder??"Windows Documents / Star Wars - The Old Republic / CombatLogs",13));
        panel.Children.Add(Text("Saved history and backups",13));panel.Children.Add(Text(snapshot.DataDirectory??"%LOCALAPPDATA%\\SwtorRecap",13));
        var folder=new TextBox{Text=settings.LogFolder??"",PlaceholderText="Use the detected combat-log folder"};AutomationProperties.SetName(folder,"Combat-log folder override");panel.Children.Add(folder);
        panel.Children.Add(Action("Choose log folder","settings-folder",async()=>{var chosen=await StorageProvider.OpenFolderPickerAsync(new(){Title="Combat-log folder",AllowMultiple=false});if(chosen.Count>0)folder.Text=chosen[0].TryGetLocalPath();}));
        var startup=new CheckBox{Content="Start with Windows",IsChecked=settings.StartWithWindows,IsEnabled=!_preview};AutomationProperties.SetAutomationId(startup,"startup-preference");panel.Children.Add(startup);
        var once=new CheckBox{Content="Exit tracker after the next game session",IsChecked=settings.SessionOnce};panel.Children.Add(once);
        var save=Action("Save recording settings","settings-save",async()=>{var result=await _client.RequestAsync<MutationResult>("Settings",new SettingsUpdate(snapshot.Revision.SettingsRevision,folder.Text,startup.IsChecked==true,once.IsChecked==true));await RefreshSnapshot();RenderSettings();_notice.Text=result.Message;});
        void UpdateSave()=>save.IsEnabled=!snapshot.StartupVerified||(folder.Text??"")!=(settings.LogFolder??"")||(startup.IsChecked==true)!=settings.StartWithWindows||(once.IsChecked==true)!=settings.SessionOnce;
        folder.PropertyChanged+=(_,e)=>{if(e.Property==TextBox.TextProperty)UpdateSave();};startup.IsCheckedChanged+=(_,_)=>UpdateSave();once.IsCheckedChanged+=(_,_)=>UpdateSave();UpdateSave();panel.Children.Add(save);
        if(!snapshot.StartupVerified)panel.Children.Add(Text("Windows startup does not match the saved preference. Save settings to repair this application's entry."));
        panel.Children.Add(Text("History",22));
        var zone=new TextBox{Text=settings.ReportingZone};AutomationProperties.SetName(zone,"Source clock timezone for imports");panel.Children.Add(Text("Import source timezone"));panel.Children.Add(zone);
        var date=new TextBox{PlaceholderText="YYYY-MM-DD · optional when the original filename has a date"};AutomationProperties.SetName(date,"Import recording date");
        var independent=new CheckBox{Content="These are independent recordings, even if existing captures share their date"};AutomationProperties.SetAutomationId(independent,"import-independent");
        panel.Children.Add(new Expander{Header="Renamed files and overlap review",Content=Stack(Text("Use the original recording date for renamed files. Leave it empty to keep unknown dates out of calendar recaps. Confirm independence only for separate recordings; overlapping copies must not be added twice."),date,independent)});
        panel.Children.Add(Action("Import combat logs…","import-logs",async()=>
        {
            var files=await StorageProvider.OpenFilePickerAsync(new(){Title="Import SWTOR combat logs",AllowMultiple=true,FileTypeFilter=[new("Combat logs"){Patterns=["*.txt"]}]});
            if(files.Count==0)return;var paths=files.Select(f=>f.TryGetLocalPath()).Where(p=>p is not null).Cast<string>().ToArray();
            var result=await _client.RequestAsync<MutationResult>("Import",new ImportRequest(paths,zone.Text??settings.ReportingZone,string.IsNullOrWhiteSpace(date.Text)?null:date.Text.Trim(),ConfirmIndependent:independent.IsChecked==true));_notice.Text=result.Message;await RefreshSnapshot();
        }));
        _jobStatus=Text(string.Join("\n",snapshot.Jobs.Select(j=>$"{j.Kind}: {j.State} · {j.Message}")));panel.Children.Add(_jobStatus);
        panel.Children.Add(Action("Cancel pending import","import-cancel",async()=>{var jobs=await _client.RequestAsync<JobSummary[]>("Jobs");foreach(var job in jobs.Where(j=>j.State is "queued" or "running" or "parsing" or "waiting_for_game"))await _client.RequestAsync<MutationResult>("CancelImport",job.Id);await RefreshSnapshot();}));
        panel.Children.Add(Action("Export history backup…","backup-history",async()=>
        {var file=await StorageProvider.SaveFilePickerAsync(new(){Title="Export verified history backup",SuggestedFileName=$"SWTOR-Recap-{DateTime.Now:yyyy-MM-dd}.db"});if(file?.TryGetLocalPath() is { } path)_notice.Text=(await _client.RequestAsync<MutationResult>("Backup",path)).Message;}));
        panel.Children.Add(Action("Restore backup…","restore-history",async()=>
        {
            var files=await StorageProvider.OpenFilePickerAsync(new(){Title="Choose history backup",AllowMultiple=false});if(files.Count==0||files[0].TryGetLocalPath() is not { } path)return;
            var confirm=new Window{Title="Replace recorded history?",Width=470,Height=220,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            var replace=new Button{Content="Replace history",HorizontalAlignment=HorizontalAlignment.Left};var cancel=new Button{Content="Cancel"};
            replace.Click+=(_,_)=>confirm.Close(true);cancel.Click+=(_,_)=>confirm.Close(false);
            confirm.Content=new Border{Padding=new Thickness(24),Child=Stack(Text("Restore "+Path.GetFileName(path)+"? Current history will be replaced. A verified recovery backup is saved first."),replace,cancel)};
            if(await confirm.ShowDialog<bool>(this)){_notice.Text=(await _client.RequestAsync<MutationResult>("Restore",path)).Message;await RefreshSnapshot();}
        }));
        var reporting=new TextBox{Text=settings.ReportingZone};AutomationProperties.SetName(reporting,"Reporting timezone");
        panel.Children.Add(new Expander{Header="Reprocess saved history",Content=Stack(Text("Apply parser fixes to previous recordings using their original logs. Close SWTOR first. Existing history is kept if source files are missing."),Text("Reporting timezone",13),reporting,
            Action("Reprocess saved logs","rebuild-history",async()=>{_notice.Text="Reprocessing history…";_notice.Text=(await _client.RequestAsync<MutationResult>("Rebuild",reporting.Text??settings.ReportingZone)).Message;await RefreshSnapshot();}))});
        panel.Children.Add(new Expander{Header="Catalog support and diagnostics",Content=Stack(Text($"Parser {snapshot.ParserVersion} · Catalog {snapshot.CatalogVersion} · SQLite {snapshot.SqliteVersion}"),Action("Show supported content","catalog-coverage",async()=>
        {var catalog=await _client.RequestAsync<CatalogDocument>("Coverage");var text=string.Join("\n",catalog.Activities.Select(a=>$"{a.Name}: {a.Encounters.Count(e=>e.Status is "terminal-tested" or "full-run-tested")} fixture-verified / {a.Encounters.Length} candidate encounters"));_body.Content=Stack(Action("Back to settings","coverage-back",()=>{Navigate("Settings");return Task.CompletedTask;}),Text(text));}))});
        _body.Content=panel;
    }
    private async Task ExportRecap(RecapSnapshot recap)
    {
        var file=await StorageProvider.SaveFilePickerAsync(new(){Title="Export recap image",SuggestedFileName=$"SWTOR-Recap-{recap.Year}.png",FileTypeChoices=[new("PNG image"){Patterns=["*.png"]}]});
        if(file?.TryGetLocalPath() is not { } path)return;
        // Render immutable values from the same DTO/revision. Names are omitted by default.
        RecapImageRenderer.Save(recap,path);
        _notice.Text="Recap image exported with player names hidden.";
    }
}
