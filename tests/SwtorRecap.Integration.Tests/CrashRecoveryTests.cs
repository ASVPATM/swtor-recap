using System.Diagnostics;
using SwtorRecap.Core;
using SwtorRecap.Engine;
using SwtorRecap.Storage;

namespace SwtorRecap.Integration.Tests;

public sealed class CrashRecoveryTests
{
    [Theory]
    [InlineData("checkpoint",false)]
    [InlineData("metrics",false)]
    [InlineData("before_commit",false)]
    [InlineData("after_commit",true)]
    public async Task TerminatedChildRecoversAtDurableCommitBoundary(string phase,bool committed)
    {
        var root=Path.Combine(Path.GetTempPath(),"swtor-crash-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var file=Path.Combine(root,"capture.txt");await File.WriteAllLinesAsync(file,["[20:00:00.000] [@Local#101] [] [] [Event {836045448945472}: EnterCombat {836045448945489}]","[20:00:01.000] [@Local#101] [NPC {123}:1] [] [ApplyEffect {836045448945477}: Damage {836045448945501}] (100)"]);
            var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../.."));
            var tool=Path.Combine(repo,"src/SwtorRecap.Tools/bin/Release/net10.0/SwtorRecap.Tools.dll");
            var host=Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??(File.Exists("/private/tmp/swtor-dotnet/dotnet")?"/private/tmp/swtor-dotnet/dotnet":"dotnet");
            var info=new ProcessStartInfo(host){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var argument in new[]{tool,"fault-child","--data-dir",Path.Combine(root,"data"),"--file",file,"--phase",phase})info.ArgumentList.Add(argument);
            using var process=Process.Start(info)!;var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(process.ExitCode==77,await error);
            var store=new RecapStore(Path.Combine(root,"data"));var catalog=Catalog.LoadDefault();var source=Assert.Single(store.SourceIds());
            Assert.Equal(committed?new FileInfo(file).Length:0,store.LoadSource(source).Offset);
            new IngestionEngine(store,catalog).ReadAll(source);Assert.Equal(100,store.Totals(new(),catalog).Amounts!.Damage);store.Verify();
        }
        finally{Directory.Delete(root,true);}
    }
}
