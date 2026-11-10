using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SwtorRecap.Core;

namespace SwtorRecap.Desktop;

public static class RecapImageRenderer
{
    public static Control CreateCard(RecapSnapshot recap,bool showNames=false)
    {
        static TextBlock Label(string text,double size)=>new(){Text=text,FontSize=size,Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap};
        var content=new StackPanel{Spacing=22};
        var brand=new StackPanel{Orientation=Orientation.Horizontal,Spacing=14};
        brand.Children.Add(new Image{Source=new Bitmap(AssetLoader.Open(new Uri("avares://SwtorRecap/Assets/logo.png"))),Width=42,Height=42});
        var brandText=Label("SWTOR Recap",22);brandText.VerticalAlignment=VerticalAlignment.Center;brandText.Foreground=AppTheme.Accent;brand.Children.Add(brandText);content.Children.Add(brand);
        content.Children.Add(Label($"Your recorded {recap.Year}"+(recap.Month is { } month?$" · {new DateTime(recap.Year,month,1):MMMM}":""),44));
        content.Children.Add(Label($"Damage dealt   {recap.Totals.Amounts?.Damage.ToString("N0")??"Unavailable"}",30));
        content.Children.Add(Label($"Healing done   {recap.Totals.Amounts?.Healing.ToString("N0")??"Unavailable"}",30));
        content.Children.Add(Label($"NPC kills   {recap.Totals.Kills:N0}"+(recap.Totals.KillsNeedReprocess?"+ · older logs need reprocessing":""),30));
        content.Children.Add(Label("Game-open time   "+MainWindow.GameTimeLabel(recap.Totals,recap.CharacterFilter is not null),24));
        content.Children.Add(Label("Combat time   "+MainWindow.Duration(recap.Totals.CombatMilliseconds),24));
        content.Children.Add(Label($"{recap.Totals.Bosses:N0} boss encounters won    {recap.Totals.Flashpoints:N0} flashpoint clears    {recap.Totals.Operations:N0} operation clears",22));
        if(showNames)foreach(var ch in recap.Characters)content.Children.Add(Label(ch.Name+" · "+MainWindow.Duration(ch.CombatMilliseconds)+" combat",18));
        content.Children.Add(Label($"{recap.Points.Length} recorded days · {recap.Totals.Sessions} observed sessions · {recap.Totals.Imports} imported captures",18));
        content.Children.Add(Label((recap.PeriodOpen?"Period in progress · ":"")+"Recorded history only · "+recap.ReportingZone,15));
        content.Children.Add(Label("Raw personal amounts · Clears record the final boss, not mission turn-in.",14));
        return new Border{Width=1100,Padding=new Thickness(48),Background=AppTheme.Background,Child=content};
    }
    public static void Save(RecapSnapshot recap,string path,bool showNames=false)
    {
        var card=CreateCard(recap,showNames);card.Measure(new Size(1100,double.PositiveInfinity));card.Arrange(new Rect(card.DesiredSize));
        using var bitmap=new RenderTargetBitmap(new PixelSize(1100,(int)Math.Ceiling(card.DesiredSize.Height)),new Vector(96,96));bitmap.Render(card);bitmap.Save(path,PngBitmapEncoderOptions.Default);
    }
}
