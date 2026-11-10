using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace SwtorRecap.Desktop;

public static class AppTheme
{
    public static void FitInitialWindow(Window window)
    {
        if(window.Screens.Primary is not { } screen)return;
        var width=Math.Min(window.Width,screen.WorkingArea.Width/screen.Scaling-32);
        var height=Math.Min(window.Height,screen.WorkingArea.Height/screen.Scaling-64);
        if(width<=0||height<=0)return;
        window.MinWidth=Math.Min(window.MinWidth,width);window.MinHeight=Math.Min(window.MinHeight,height);
        window.Width=width;window.Height=height;
        window.WindowStartupLocation=WindowStartupLocation.CenterScreen;
    }
    public static readonly IBrush Background=Brush("#101720"),Surface=Brush("#192430"),Muted=Brush("#AAB9C8"),Accent=Brush("#72DEC8"),Foreground=Brush("#E7EEF4");
    public static IBrush Brush(string hex)=>new SolidColorBrush(Color.Parse(hex));
    public static void Apply(Window window)
    {
        window.Background=Background;window.Foreground=Foreground;window.FontSize=14;
        window.Styles.Add(new Style(x=>x.OfType<Button>()) {Setters={
            new Setter(TemplatedControl.BackgroundProperty,Brush("#263443")),
            new Setter(TemplatedControl.CornerRadiusProperty,new CornerRadius(7)),
            new Setter(TemplatedControl.BorderThicknessProperty,new Thickness(0)),
            new Setter(TemplatedControl.MinHeightProperty,40d)}});
        window.Styles.Add(new Style(x=>x.OfType<TextBox>()) {Setters={new Setter(TemplatedControl.CornerRadiusProperty,new CornerRadius(7)),new Setter(TemplatedControl.MinHeightProperty,40d)}});
        window.Styles.Add(new Style(x=>x.OfType<ComboBox>()) {Setters={new Setter(TemplatedControl.CornerRadiusProperty,new CornerRadius(7)),new Setter(TemplatedControl.MinHeightProperty,40d)}});
    }
}
