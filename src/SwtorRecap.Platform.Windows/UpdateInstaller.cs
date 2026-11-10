using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SwtorRecap.Core;
using SwtorRecap.Engine;

namespace SwtorRecap.Platform.Windows;

public sealed record UpdateInstallPlan(ReleaseUpdate Release, string CurrentVersion, string InstallerPath, string InstallDirectory,
    string PreviousExecutable, int ParentPid, long ParentStartedTicks, string BackupPath, string DataRoot, bool Isolated = false);

[SupportedOSPlatform("windows")]
public static class UpdateInstaller
{
    public const string MutexName = @"Local\SwtorRecap.Update";
    public static bool IsRunning()
    {
        if(!Mutex.TryOpenExisting(MutexName,out var mutex))return false;
        mutex.Dispose();return true;
    }
    public static string InstallDirectory
    {
        get
        {
            using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{86C572DA-56EC-46D4-9FE3-26031AA6251F}_is1");
            return key?.GetValue("InstallLocation") is string path && Path.IsPathFullyQualified(path)
                ? path.TrimEnd(Path.DirectorySeparatorChar) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","SWTOR Recap");
        }
    }
    public static bool IsInstalled => Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)
        .Equals(InstallDirectory,StringComparison.OrdinalIgnoreCase);
    public static string StageHelper(string installerPath)
    {
        var destination=Path.Combine(Path.GetDirectoryName(installerPath)!,"helper");Directory.CreateDirectory(destination);
        var source=Path.Combine(AppContext.BaseDirectory,"agent");
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
        {
            var target=Path.Combine(destination,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,false);
        }
        return Path.Combine(destination,"SwtorRecap.Agent.exe");
    }
    public static void StartHelper(string helper,string planPath,UpdateInstallPlan plan)
    {
        ReleaseUpdater.Validate(plan.Release,plan.CurrentVersion);ReleaseUpdater.VerifyFile(plan.InstallerPath,plan.Release);
        File.WriteAllText(planPath,Json.Write(plan));
        var start=new ProcessStartInfo(helper){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(helper)!};
        start.ArgumentList.Add("--apply-update");start.ArgumentList.Add(planPath);
        using var process=Process.Start(start)??throw new IOException("Could not start the update installer.");
    }
    public static async Task<int> ApplyAsync(string planPath)
    {
        var plan=Json.Read<UpdateInstallPlan>(File.ReadAllText(planPath));
        using var mutex=new Mutex(false,MutexName,out var created);
        if(!created)throw new IOException("An update is already running.");
        var resultPath=Path.Combine(Path.GetDirectoryName(planPath)!,"result.json");
        var success=false;string? error=null;
        try
        {
            ReleaseUpdater.Validate(plan.Release,plan.CurrentVersion);
            if(!Path.IsPathFullyQualified(plan.InstallDirectory)||!Path.IsPathFullyQualified(plan.PreviousExecutable)||!File.Exists(plan.BackupPath))
                throw new InvalidDataException("The update plan or recovery backup is missing.");
            File.WriteAllText(planPath+".ready","ready");
            try
            {
                using var parent=Process.GetProcessById(plan.ParentPid);
                if(parent.StartTime.ToUniversalTime().Ticks==plan.ParentStartedTicks)
                {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));await parent.WaitForExitAsync(timeout.Token);}
            }
            catch(ArgumentException){}
            if(new WindowsGameProcesses().Discover().Count>0)throw new IOException("Close SWTOR before installing this update.");
            // The tracker lock proves the database writer has shut down. Hold it throughout installation.
            WriterGuard? guard=null;
            for(var attempt=0;attempt<40&&guard is null;attempt++)
            {try{guard=new WriterGuard(plan.DataRoot);}catch(IOException){await Task.Delay(250);}}
            if(guard is null)throw new IOException("The tracker did not stop. The update was cancelled.");
            using(guard)
            using(var lockedInstaller=new FileStream(plan.InstallerPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                ReleaseUpdater.VerifyFile(plan.InstallerPath,plan.Release);
                // Shell execution retains Windows' normal executable trust checks and visible installer progress.
                var start=new ProcessStartInfo(plan.InstallerPath){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(plan.InstallerPath)!};
                foreach(var arg in new[]{"/SILENT","/NORESTART","/RESTARTEXITCODE=3010","/SP-","/DIR="+plan.InstallDirectory,"/LOG="+Path.Combine(Path.GetDirectoryName(planPath)!,"install.log")}) start.ArgumentList.Add(arg);
                using var installer=Process.Start(start)??throw new IOException("Windows did not start the installer.");
                await installer.WaitForExitAsync();
                if(installer.ExitCode!=0)throw new IOException($"The installer stopped (code {installer.ExitCode}). History is preserved. Run the installer again to repair the application.");
                var executable=Path.Combine(plan.InstallDirectory,"SwtorRecap.exe");
                if(!File.Exists(executable)||FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Split('+')[0]!=plan.Release.Version)
                    throw new IOException("The installed application version could not be verified. History is preserved; run the installer again.");
                if(!plan.Isolated)
                {
                    using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true);
                    var priorAgent=Path.Combine(Path.GetDirectoryName(plan.PreviousExecutable)!,"agent","SwtorRecap.Agent.exe");
                    if(key?.GetValue("SwtorRecap") is string command&&command.Equals('"'+priorAgent+'"',StringComparison.OrdinalIgnoreCase))
                        key.SetValue("SwtorRecap",'"'+Path.Combine(plan.InstallDirectory,"agent","SwtorRecap.Agent.exe")+'"');
                }
                success=true;
            }
        }
        catch(Exception ex){error=ex is OperationCanceledException?"The dashboard did not close. Nothing was installed.":ex.Message;}
        File.WriteAllText(resultPath,Json.Write(new{success,error,version=plan.Release.Version,backup=plan.BackupPath}));
        mutex.Dispose();
        if(error is not null&&!plan.Isolated)WindowsAppIdentity.ShowError(error+"\nRecovery backup: "+plan.BackupPath);
        var reopen=success?Path.Combine(plan.InstallDirectory,"SwtorRecap.exe"):plan.PreviousExecutable;
        if(File.Exists(reopen))
        {
            var start=new ProcessStartInfo(reopen){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(reopen)!};
            if(plan.Isolated){start.ArgumentList.Add("--data-dir");start.ArgumentList.Add(plan.DataRoot);}
            Process.Start(start)?.Dispose();
        }
        return success?0:1;
    }
}
