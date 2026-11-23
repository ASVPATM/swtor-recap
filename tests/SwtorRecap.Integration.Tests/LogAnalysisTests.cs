using System.Diagnostics;
using System.Text.Json;
using SwtorRecap.Core;
using SwtorRecap.Engine;

namespace SwtorRecap.Integration.Tests;

public sealed class LogAnalysisTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("changed_input")]
    [InlineData("existing_data")]
    public async Task AnalysisUsesIsolatedStorageAndChecksEvidence(string scenario)
    {
        var root=Path.Combine(Path.GetTempPath(),"swtor-analysis-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var input=Path.Combine(root,"combat_2026-11-01_12_00_00_123456.txt");
            var content="[12:00:00.000] [@PrivateTester#101] [] [] [AreaEntered {836045448953664}: Fleet {137438989504}] (test) <v7.0.0b>\r\n"+
                "[12:00:01.000] [@PrivateTester#101] [Guard {999}:1|(40/100)] [] [ApplyEffect {836045448945477}: Damage {836045448945501}] (80 ~60)\r\n"+
                "[12:00:02.000] [@OtherTester#202] [Guard {999}:1|(0/100)] [] [Event {836045448945472}: Death {836045448945493}]\r\n";
            File.WriteAllText(input,content);var hash=ByteFramer.Digest(input);
            var manifest=Path.Combine(root,"inputs.json");
            File.WriteAllText(manifest,Json.Write(new{files=new[]{new{id="F001",path=input,sha256=hash,dateAnchor="2026-11-01"}},sourceTimezone="UTC"}));
            var data=Path.Combine(root,"data");var output=Path.Combine(root,"result.json");
            if(scenario=="changed_input")File.AppendAllText(input,"changed");
            if(scenario=="existing_data"){Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"sentinel"),"preserve");}
            var before=File.ReadAllBytes(input);var written=File.GetLastWriteTimeUtc(input);
            var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../.."));
            var host=Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??(File.Exists("/private/tmp/swtor-dotnet/dotnet")?"/private/tmp/swtor-dotnet/dotnet":"dotnet");
            var info=new ProcessStartInfo(host){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{Path.Combine(repo,"src/SwtorRecap.Tools/bin/Release/net10.0/SwtorRecap.Tools.dll"),"analyze","--manifest",manifest,"--data-dir",data,"--output",output})info.ArgumentList.Add(arg);
            using var process=Process.Start(info)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));await stdout;
            Assert.Equal(before,File.ReadAllBytes(input));Assert.Equal(written,File.GetLastWriteTimeUtc(input));
            if(scenario!="valid")
            {
                Assert.Equal(1,process.ExitCode);Assert.False(File.Exists(output));
                if(scenario=="existing_data")Assert.Equal("preserve",File.ReadAllText(Path.Combine(data,"sentinel")));
                return;
            }
            Assert.True(process.ExitCode==0,await stderr);
            var json=File.ReadAllText(output);Assert.DoesNotContain("PrivateTester",json);Assert.DoesNotContain("OtherTester",json);Assert.DoesNotContain(input,json);
            using var parsed=JsonDocument.Parse(json);var file=parsed.RootElement.GetProperty("files")[0];
            Assert.Equal(80,file.GetProperty("totals").GetProperty("amounts").GetProperty("damage").GetInt64());
            Assert.Equal(1,file.GetProperty("totals").GetProperty("kills").GetInt64());
            Assert.Equal(3,file.GetProperty("accepted").GetInt64());Assert.Single(file.GetProperty("kills").EnumerateArray());
        }
        finally{Directory.Delete(root,true);}
    }
}
