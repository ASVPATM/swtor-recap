using SwtorRecap.Core;

namespace SwtorRecap.Engine;

public static class DataPaths
{
    public static string Production()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Use --preview or replay with an explicit isolated data directory on this platform.");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwtorRecap");
    }
    public static string Isolated(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Preview, replay, and tests require an explicit isolated data directory.");
        path = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && path.Equals(Production(), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("An isolated run cannot use production history.");
        return path;
    }
    public static string PipeName(string root) => "SwtorRecap.v1." + Keys.Stable(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())[..24];
}

public sealed class WriterGuard : IDisposable
{
    private readonly FileStream _stream;
    public WriterGuard(string root)
    {
        Directory.CreateDirectory(root);
        try { _stream = new FileStream(Path.Combine(root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("Another tracker owns this data directory, possibly in another Windows login session. Use that tracker or quit it first.", ex); }
    }
    public void Dispose() => _stream.Dispose();
}
