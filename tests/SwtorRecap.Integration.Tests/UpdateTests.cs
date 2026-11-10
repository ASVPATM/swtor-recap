using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwtorRecap.Engine;
namespace SwtorRecap.Integration.Tests;
public sealed class UpdateTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"swtor-updates-"+Guid.NewGuid().ToString("N"));
    private static readonly byte[] Installer=Encoding.UTF8.GetBytes("synthetic installer payload");
    private static readonly ReleaseUpdate Release=new("1.2.3",ReleaseUpdater.Repository+"/releases/download/v1.2.3/SwtorRecap-1.2.3-Setup.exe",Convert.ToHexString(SHA256.HashData(Installer)),Installer.Length);
    private static string Metadata(ReleaseUpdate? release=null,bool draft=false,bool prerelease=false)
    {var r=release??Release;return JsonSerializer.Serialize(new{tag_name="v"+r.Version,draft,prerelease,assets=new[]{new{name=$"SwtorRecap-{r.Version}-Setup.exe",state="uploaded",browser_download_url=r.Url,digest="sha256:"+r.Sha256,size=r.Size}}});}
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> handler):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(handler(request));}
    private static HttpResponseMessage Response(byte[] body)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(body)};
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
    [Fact]public void StableNewerVersionIsRequired()
    {
        Assert.NotNull(ReleaseUpdater.ReadRelease(Metadata(),"1.2.2").Release);
        Assert.Null(ReleaseUpdater.ReadRelease(Metadata(),"1.2.3").Release);Assert.Null(ReleaseUpdater.ReadRelease(Metadata(),"9.0.0").Release);
        Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.ReadRelease(Metadata(draft:true),"1.0.0"));Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.ReadRelease(Metadata(prerelease:true),"1.0.0"));
    }
    [Theory][InlineData("http://github.com/ASVPATM/swtor-recap/x")][InlineData("https://github.com/Other/repo/x")][InlineData("file:///x")]
    public void WrongAssetOriginIsRejected(string url)=>Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.ReadRelease(Metadata(Release with{Url=url}),"1.0.0"));
    [Fact]public void MissingDigestAndHugeOrAmbiguousAssetsAreRejected()
    {
        Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.ReadRelease(Metadata(Release with{Sha256=""}),"1.0.0"));
        Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.ReadRelease(Metadata(Release with{Size=ReleaseUpdater.MaxInstallerBytes+1}),"1.0.0"));
        using var doc=JsonDocument.Parse(Metadata());var a=doc.RootElement.GetProperty("assets")[0];
        var duplicate=JsonSerializer.Serialize(new{tag_name="v1.2.3",draft=false,prerelease=false,assets=new[]{a,a}});
        Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.ReadRelease(duplicate,"1.0.0"));
    }
    [Fact]public async Task PrivateRepositoryDoesNotClaimUpToDate()
    {
        using var client=new HttpClient(new Handler(_=>new(HttpStatusCode.NotFound)));
        var check=await new ReleaseUpdater(_root,client,"1.0.0").CheckAsync(default);Assert.Null(check.Release);Assert.Contains("No public stable",check.Message);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task DownloadChecksMetadataAndDigestBeforeReturningFile(bool corrupt)
    {
        using var client=new HttpClient(new Handler(r=>r.RequestUri!.AbsoluteUri==ReleaseUpdater.LatestApi?Response(Encoding.UTF8.GetBytes(Metadata())):Response(corrupt?new byte[Installer.Length]:Installer)));
        var updater=new ReleaseUpdater(_root,client,"1.0.0");
        if(corrupt){await Assert.ThrowsAsync<InvalidDataException>(()=>updater.DownloadAsync(Release,null,default));Assert.Empty(Directory.GetFiles(_root,"*",SearchOption.AllDirectories));}
        else{var path=await updater.DownloadAsync(Release,null,default);Assert.Equal(Installer,File.ReadAllBytes(path));File.WriteAllBytes(path,new byte[Installer.Length]);Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.VerifyFile(path,Release));}
    }
    [Fact]public async Task WithdrawnOrChangedReleaseDoesNotDownload()
    {
        using var client=new HttpClient(new Handler(_=>Response(Encoding.UTF8.GetBytes(Metadata(Release with{Sha256=new string('a',64)})))));
        await Assert.ThrowsAsync<IOException>(()=>new ReleaseUpdater(_root,client,"1.0.0").DownloadAsync(Release,null,default));Assert.False(Directory.Exists(_root));
    }
    [Theory][InlineData("https://evil.example/file")][InlineData("http://release-assets.githubusercontent.com/file")][InlineData("https://github.com/Other/file")]
    public async Task UnexpectedDownloadRedirectIsRejected(string url)
    {
        using var client=new HttpClient(new Handler(r=>r.RequestUri!.AbsoluteUri==ReleaseUpdater.LatestApi?Response(Encoding.UTF8.GetBytes(Metadata())):new(HttpStatusCode.Redirect){Headers={Location=new Uri(url)}}));
        await Assert.ThrowsAsync<InvalidDataException>(()=>new ReleaseUpdater(_root,client,"1.0.0").DownloadAsync(Release,null,default));
    }
    [Fact]public async Task CancelledDownloadLeavesNoExecutable()
    {
        using var cancel=new CancellationTokenSource();using var client=new HttpClient(new Handler(r=>{if(r.RequestUri!.AbsoluteUri==ReleaseUpdater.LatestApi)return Response(Encoding.UTF8.GetBytes(Metadata()));cancel.Cancel();return Response(Installer);}));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ReleaseUpdater(_root,client,"1.0.0").DownloadAsync(Release,null,cancel.Token));
        Assert.Empty(Directory.GetFiles(_root,"*",SearchOption.AllDirectories));
    }
}
