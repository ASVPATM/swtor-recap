using System.Globalization;
using SwtorRecap.Core;
using SwtorRecap.Storage;

namespace SwtorRecap.Engine;

public static class PreviewSeeder
{
    public static void Seed(RecapStore store,Catalog catalog,int year=2026)
    {
        if(store.SourceIds().Length>0)return;
        var logs=Path.Combine(store.Root,"synthetic-logs");Directory.CreateDirectory(logs);
        var engine=new IngestionEngine(store,catalog);
        for(var month=1;month<=12;month++)
        for(var session=0;session<2;session++)
        {
            var start=new DateTimeOffset(year,month,7+session*12,20,0,0,TimeSpan.Zero);
            var container=store.CreateContainer("session","preview-"+month+"-"+session,start);
            var player=session==0?"@Aster Vale#101":"@Mira Sol#102";
            var path=Path.Combine(logs,$"combat_{year:D4}-{month:D2}-{7+session*12:D2}_20_00_00_000000.txt");
            var lines=new List<string>{Line("20:00:00.000",player,"","",$"AreaEntered {{{EffectIds.Area}}}: Hammer Station {{833571547775682}}", "(preview) <v7.0.0b>"),Line("20:00:01.000",player,"","",$"Event {{{EffectIds.Event}}}: EnterCombat {{{EffectIds.Enter}}}")};
            var damage=40000+month*15000+session*7000;
            for(var i=0;i<25;i++)
            {
                lines.Add(Line($"20:{i:D2}:02.000",player,"Training Target {999}:500|(1/1)","Strike {12}",$"ApplyEffect {{{EffectIds.Apply}}}: Damage {{{EffectIds.Damage}}}",$"({damage} ~{damage-1000} energy {{836045448940874}})"));
                lines.Add(Line($"20:{i:D2}:03.000",player,"=","Heal {13}",$"ApplyEffect {{{EffectIds.Apply}}}: Heal {{{EffectIds.Heal}}}",$"({damage/3} ~{damage/4})"));
            }
            lines.Add(Line("20:26:00.000",player,"","",$"Event {{{EffectIds.Event}}}: ExitCombat {{{EffectIds.Exit}}}"));
            File.WriteAllLines(path,lines,System.Text.Encoding.ASCII);
            var source=engine.Register(container,path,true,DateOnly.FromDateTime(start.DateTime),"UTC",digest:ByteFramer.Digest(path));engine.ReadAll(source);
            var end=start.AddMinutes(45+month*2+session*13);store.RecordAwake(container,start,end,(long)(end-start).TotalMilliseconds,store.Settings.ReportingZone);store.FinalizeSession(container,end,"synthetic_preview");
        }
    }
    public static string Line(string time,string source,string target,string ability,string effect,string tail="")=>$"[{time}] [{source}] [{target}] [{ability}] [{effect}] {tail}".TrimEnd();
}
