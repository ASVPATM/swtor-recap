using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwtorRecap.Core;

namespace SwtorRecap.Engine;

public sealed record ReleaseUpdate(string Version, string Url, string Sha256, long Size);
public sealed record UpdateCheck(ReleaseUpdate? Release, string Message);
public interface IReleaseUpdater
{
    Task<UpdateCheck> CheckAsync(CancellationToken token);
    Task<string> DownloadAsync(ReleaseUpdate release, IProgress<int>? progress, CancellationToken token);
}

/// <summary>User-initiated, anonymous access to one repository. Never accepts a feed URL or token from settings.</summary>
public sealed partial class ReleaseUpdater(string stagingRoot, HttpClient client, string currentVersion) : IReleaseUpdater
{
    public const string Repository = "https://github.com/ASVPATM/swtor-recap";
    public const string LatestApi = "https://api.github.com/repos/ASVPATM/swtor-recap/releases/latest";
    public const long MaxInstallerBytes = 256L * 1024 * 1024;
    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)] private static partial Regex VersionPattern();
    [GeneratedRegex(@"^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex HashPattern();
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    private static Version ParseVersion(string value)
    {
        if (!VersionPattern().IsMatch(value) || !Version.TryParse(value, out var v)) throw new InvalidDataException("The release has an invalid version.");
        return v;
    }
    public static void Validate(ReleaseUpdate release, string installedVersion)
    {
        if (ParseVersion(release.Version) <= ParseVersion(installedVersion)) throw new InvalidDataException("Only a newer stable release can be installed.");
        var expected = $"{Repository}/releases/download/v{release.Version}/SwtorRecap-{release.Version}-Setup.exe";
        if (release.Url != expected || !HashPattern().IsMatch(release.Sha256) || release.Size is <= 0 or > MaxInstallerBytes)
            throw new InvalidDataException("The release installer, checksum or size is invalid. Nothing was installed.");
    }
    public static UpdateCheck ReadRelease(string json, string installedVersion)
    {
        using var doc = JsonDocument.Parse(json);var r = doc.RootElement;
        if (r.GetProperty("draft").GetBoolean() || r.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("Only published stable releases are supported.");
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v')) throw new InvalidDataException("Invalid release tag.");
        var version = tag[1..]; var latest = ParseVersion(version);
        if (latest <= ParseVersion(installedVersion)) return new(null, $"SWTOR Recap {installedVersion} is up to date.");
        var name = $"SwtorRecap-{version}-Setup.exe";
        var assets = r.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("This release does not have one complete Windows installer.");
        var asset = assets[0];var digest = asset.GetProperty("digest").GetString() ?? "";
        if (asset.GetProperty("state").GetString() != "uploaded" || !digest.StartsWith("sha256:",StringComparison.Ordinal))
            throw new InvalidDataException("GitHub has not provided a SHA-256 checksum for this installer.");
        var release = new ReleaseUpdate(version, asset.GetProperty("browser_download_url").GetString() ?? "", digest[7..], asset.GetProperty("size").GetInt64());
        Validate(release, installedVersion);
        return new(release, $"Version {version} is available ({release.Size / 1024 / 1024} MB).");
    }
    private static HttpRequestMessage Request(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("SwtorRecap/" + Versions.App);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }
    public async Task<UpdateCheck> CheckAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = Request(LatestApi);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(null, "No public stable release is available yet. Private previews must be installed from the supplied download.");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new IOException("GitHub temporarily limited update checks. Try again later.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();await CopyBounded(stream,buffer,1024*1024,null,timeout.Token);
        return ReadRelease(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), currentVersion);
    }
    public static bool AllowedRedirect(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        (uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com");
    public async Task<string> DownloadAsync(ReleaseUpdate release, IProgress<int>? progress, CancellationToken token)
    {
        Validate(release, currentVersion);
        // Refresh metadata immediately before download; a withdrawn/replaced release is not installed.
        var check = await CheckAsync(token);
        if (check.Release != release) throw new IOException("The release changed. Check for updates again.");
        Directory.CreateDirectory(stagingRoot);
        var directory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var path = Path.Combine(directory,$"SwtorRecap-{release.Version}-Setup.exe");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var url = release.Url;
            for (var redirects = 0; ; redirects++)
            {
                using var request = Request(url);request.Headers.Accept.Clear();
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    if (redirects >= 3 || location is null || !location.IsAbsoluteUri || !AllowedRedirect(location)) throw new InvalidDataException("GitHub returned an unexpected download redirect.");
                    url = location.AbsoluteUri;continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } size && size != release.Size) throw new InvalidDataException("Installer length does not match the release.");
                await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
                await using (var output = new FileStream(path + ".part", FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await CopyBounded(input,output,release.Size,progress,timeout.Token);
                VerifyFile(path + ".part", release);
                File.Move(path + ".part",path);
                // Preserve Windows' Internet-zone provenance. Never alter SmartScreen or Defender settings.
                if (OperatingSystem.IsWindows()) await File.WriteAllTextAsync(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n",timeout.Token);
                progress?.Report(100);return path;
            }
        }
        catch { Directory.Delete(directory,true);throw; }
    }
    private static async Task CopyBounded(Stream input, Stream output, long maximum, IProgress<int>? progress, CancellationToken token)
    {
        var buffer = new byte[65536];long total=0;var last=-1;
        while (true)
        {
            var count=await input.ReadAsync(buffer,token);if(count==0)break;total+=count;
            if(total>maximum)throw new InvalidDataException("The download exceeded its declared size.");
            await output.WriteAsync(buffer.AsMemory(0,count),token);
            var percent=(int)(total*100/maximum);if(percent!=last){last=percent;progress?.Report(percent);}
        }
    }
    public static void VerifyFile(string path, ReleaseUpdate release)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != release.Size || !Convert.ToHexString(SHA256.HashData(file)).Equals(release.Sha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installer verification failed. Nothing was installed; retry the download.");
    }
}
