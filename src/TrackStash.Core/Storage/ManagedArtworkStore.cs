namespace TrackStash.Core.Storage;

/// <summary>Immutable local content. This directory must be backed up together with the catalog.</summary>
public interface IArtworkContentStore
{
    string Root { get; }
    ValueTask SaveAsync(string id, byte[] original, byte[]? thumbnail, CancellationToken ct = default);
    ValueTask<byte[]> ReadAsync(string id, bool thumbnail, CancellationToken ct = default);
    ValueTask DeleteUncommittedAsync(string id, CancellationToken ct = default);
}

public sealed class ManagedArtworkStore : IArtworkContentStore
{
    public static string DefaultRoot => Environment.GetEnvironmentVariable("TRACKSTASH_ARTWORK_ROOT") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackStash", "artwork-data");
    public string Root { get; }

    public ManagedArtworkStore(string? root = null)
    {
        root ??= DefaultRoot;
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Artwork root must be absolute.", nameof(root));
        var fullPath = Path.GetFullPath(root);
        var resolved = Path.GetPathRoot(fullPath)!;
        foreach (var component in fullPath[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, component);
            var directory = new DirectoryInfo(resolved);
            if (directory.Exists && directory.LinkTarget is not null)
                resolved = directory.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
        }
        Root = resolved;
        CheckPath(Root);
    }

    private static void CheckPath(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Artwork storage paths must not contain symbolic links.");
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
                throw new ArgumentException("Artwork storage must be outside a Git working tree.");
        }
    }

    private string AssetDirectory(string id)
    {
        ArtworkRules.ValidateAssetId(id);
        var path = Path.Combine(Root, id);
        CheckPath(path);
        return path;
    }

    public async ValueTask SaveAsync(string id, byte[] original, byte[]? thumbnail, CancellationToken ct = default)
    {
        var directory = AssetDirectory(id);
        if (Directory.Exists(directory)) throw new IOException("Artwork asset content already exists.");
        var newRoot = !Directory.Exists(Root);
        Directory.CreateDirectory(Root);
        if (newRoot && !OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await WriteAsync(Path.Combine(directory, "original"), original, ct).ConfigureAwait(false);
            if (thumbnail is not null)
                await WriteAsync(Path.Combine(directory, "thumbnail.jpg"), thumbnail, ct).ConfigureAwait(false);
        }
        catch
        {
            await DeleteUncommittedAsync(id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<byte[]> ReadAsync(string id, bool thumbnail, CancellationToken ct = default)
    {
        var file = new FileInfo(Path.Combine(AssetDirectory(id), thumbnail ? "thumbnail.jpg" : "original"));
        if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Artwork content must not be a symbolic link.");
        if (file.Length > ArtworkRules.MaximumBytes) throw new IOException("Stored artwork exceeds the supported size.");
        return await File.ReadAllBytesAsync(file.FullName, ct).ConfigureAwait(false);
    }

    public ValueTask DeleteUncommittedAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var directory = AssetDirectory(id);
        foreach (var filename in new[] { "original", "thumbnail.jpg" })
            File.Delete(Path.Combine(directory, filename));
        if (Directory.Exists(directory)) Directory.Delete(directory);
        return ValueTask.CompletedTask;
    }
}
