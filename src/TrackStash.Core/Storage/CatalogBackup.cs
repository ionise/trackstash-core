using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrackStash.Core.Storage;

public sealed record BackupAsset(string Id, string Hash, long Bytes);
public sealed record CatalogSnapshot(int SchemaVersion, IReadOnlyList<BackupAsset> Assets, IReadOnlyList<string>? Warnings = null);
public interface ICatalogSnapshotSource
{
    string Kind => "catalog";
    string CatalogKey { get; }
    Task CreateSnapshotAsync(string destination, CancellationToken ct = default);
    Task<CatalogSnapshot> InspectSnapshotAsync(string database, CancellationToken ct = default);
}
public sealed record BackupFile(string Path, long Bytes, string Sha256);
public sealed record BackupManifest(int FormatVersion, string Id, string CatalogKey, DateTimeOffset CreatedUtc,
    int SchemaVersion, IReadOnlyList<BackupFile> Files, string Kind = "catalog", IReadOnlyList<string>? Warnings = null);
/// <summary>A completed filesystem ZIP, identified without exposing its server path.</summary>
public sealed record CatalogBackupInfo(string Id, string Filename, DateTimeOffset CreatedUtc, long Bytes);
public sealed record CatalogBackupResult(CatalogBackupInfo Backup, IReadOnlyList<string> Warnings);
public sealed class BackupBusyException() : IOException("Another backup, restore, verification or download is using this backup directory.");
public interface ICatalogBackupService
{
    Task<CatalogBackupResult> CreateAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task<IReadOnlyList<CatalogBackupInfo>> ListAsync(CancellationToken ct = default);
    Task<Stream> OpenDownloadAsync(string id, CancellationToken ct = default);
    Task<BackupManifest> VerifyAsync(string archive, CancellationToken ct = default);
    Task RestoreAsync(string archive, string destination, CancellationToken ct = default);
}

public sealed partial class CatalogBackupService(
    ICatalogSnapshotSource source, IArtworkContentStore? artwork, string? backupRoot = null) : ICatalogBackupService
{
    public static string DefaultRoot => Environment.GetEnvironmentVariable("TRACKSTASH_BACKUP_ROOT") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackStash", "backups");
    public string Root { get; } = new ManagedArtworkStore(backupRoot ?? DefaultRoot).Root;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int Keep = 10;
    private string DatabaseEntry => source.Kind == "scanner" ? "inventory.db" : "catalog.db";
    private string Filename(DateTimeOffset createdUtc, string id) =>
        $"trackstash-{source.Kind}-{createdUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}-{id}.zip";

    [GeneratedRegex(@"^trackstash-(catalog|scanner)-\d{8}T\d{6}Z-([a-f0-9]{32})\.zip$")]
    private static partial Regex ArchiveName();

    private void CheckRoot()
    {
        if (new ManagedArtworkStore(Root).Root != Root)
            throw new IOException("Backup root was replaced by a symbolic link.");
        if (!Directory.Exists(Root))
        {
            Directory.CreateDirectory(Root);
            PrivateDirectory(Root);
        }
    }

    private FileStream Acquire()
    {
        CheckRoot();
        var path = Path.Combine(Root, ".backup.lock");
        RejectLink(path);
        try { return PrivateFile(path, FileMode.OpenOrCreate); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 11 or 32 or 33 or 35)
        {
            throw new BackupBusyException();
        }
    }

    private static FileStream PrivateFile(string path, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
    private static void PrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException("Backup paths must not be symbolic links.");
    }
    private string WorkDirectory()
    {
        var path = Path.Combine(Root, $".work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        PrivateDirectory(path);
        return path;
    }

    public async Task<CatalogBackupResult> CreateAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await using var lease = Acquire();
        var work = WorkDirectory();
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var created = DateTimeOffset.UtcNow;
            var filename = Filename(created, id);
            var database = Path.Combine(work, DatabaseEntry);
            progress?.Report($"Snapshotting {source.Kind}");
            await source.CreateSnapshotAsync(database, ct);
            var snapshot = await source.InspectSnapshotAsync(database, ct);
            var staged = Path.Combine(work, "backup.zip");
            var files = new List<BackupFile>();
            await using (var output = PrivateFile(staged, FileMode.CreateNew))
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                files.Add(await AddAsync(zip, database, DatabaseEntry, ct));
                if (artwork is not null && new ManagedArtworkStore(artwork.Root).Root != artwork.Root)
                    throw new IOException("Artwork root was replaced by a symbolic link.");
                var assetIndex = 0;
                foreach (var asset in snapshot.Assets)
                {
                    if (artwork is null) throw new InvalidDataException("This backup source requires artwork storage.");
                    ct.ThrowIfCancellationRequested();
                    ArtworkRules.ValidateAssetId(asset.Id);
                    progress?.Report($"Copying artwork {++assetIndex}/{snapshot.Assets.Count}");
                    var directory = Path.Combine(artwork.Root, asset.Id);
                    RejectLink(directory);
                    var original = await AddAsync(zip, Path.Combine(directory, "original"), $"artwork/{asset.Id}/original", ct);
                    if (original.Bytes != asset.Bytes || original.Sha256 != asset.Hash)
                        throw new InvalidDataException($"Artwork original {asset.Id} does not match the catalog checksum/length.");
                    files.Add(original);
                    var thumbnail = Path.Combine(directory, "thumbnail.jpg");
                    RejectLink(thumbnail);
                    if (File.Exists(thumbnail))
                        files.Add(await AddAsync(zip, thumbnail, $"artwork/{asset.Id}/thumbnail.jpg", ct));
                }
                var manifest = new BackupManifest(1, id, source.CatalogKey, created, snapshot.SchemaVersion, files, source.Kind, snapshot.Warnings);
                await using var entry = zip.CreateEntry("manifest.json").Open();
                await JsonSerializer.SerializeAsync(entry, manifest, Json, ct);
            }
            progress?.Report($"Verifying ZIP and {source.Kind}");
            await VerifyArchiveAsync(staged, work, ct);
            ct.ThrowIfCancellationRequested();
            var final = Path.Combine(Root, filename);
            File.Move(staged, final, overwrite: false);
            progress?.Report("Applying newest-10 verified retention");
            IReadOnlyList<string> warnings;
            try { warnings = await PruneAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                warnings = [$"Backup saved and verified, but retention could not run: {ex.Message}"];
            }
            return new(new(id, filename, created, new FileInfo(final).Length), [.. snapshot.Warnings ?? [], .. warnings]);
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    private static async Task<BackupFile> AddAsync(ZipArchive zip, string sourcePath, string name, CancellationToken ct)
    {
        RejectLink(sourcePath);
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        return await CopyHashAsync(input, output, name, ct);
    }
    private static async Task<BackupFile> CopyHashAsync(Stream input, Stream output, string name, CancellationToken ct,
        long maximumBytes = long.MaxValue)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            length = checked(length + read);
            if (length > maximumBytes) throw new InvalidDataException($"File exceeds its declared size: {name}");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return new(name, length, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static BackupManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Backup manifest is missing.");
        if (entry.Length is < 0 or > 16 * 1024 * 1024) throw new InvalidDataException("Backup manifest exceeds 16 MiB.");
        using var stream = entry.Open();
        using var reader = new BinaryReader(stream);
        var bytes = reader.ReadBytes((int)entry.Length + 1);
        if (bytes.Length != entry.Length) throw new InvalidDataException("Manifest length does not match ZIP metadata.");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(bytes, Json) ?? throw new InvalidDataException("Backup manifest is invalid.");
        if (manifest.FormatVersion != 1 || !Regex.IsMatch(manifest.Id ?? "", "^[a-f0-9]{32}$") ||
            !Regex.IsMatch(manifest.CatalogKey ?? "", "^[A-F0-9]{64}$") || manifest.Files is null ||
            manifest.Files.Count is < 1 or > 100_000 ||
            !(manifest.Kind == "catalog" && manifest.SchemaVersion is 6 or 7 or 8 or 9 || manifest.Kind == "scanner" && manifest.SchemaVersion >= 0))
            throw new InvalidDataException("Unsupported or invalid backup manifest.");
        return manifest;
    }

    private async Task<BackupManifest> VerifyArchiveAsync(string archive, string work, CancellationToken ct)
    {
        RejectLink(archive);
        using var zip = ZipFile.OpenRead(archive);
        var manifest = ReadManifest(zip);
        if (manifest.Kind != source.Kind) throw new InvalidDataException("Backup type does not match the selected source.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || file.Path is null || !names.Add(file.Path) || file.Bytes < 0 ||
                !Regex.IsMatch(file.Sha256 ?? "", "^[A-F0-9]{64}$") ||
                (file.Path != DatabaseEntry && (source.Kind != "catalog" || !Regex.IsMatch(file.Path, "^artwork/[a-f0-9]{32}/(original|thumbnail\\.jpg)$"))))
                throw new InvalidDataException("Unsafe or duplicate backup manifest entry.");
            if (file.Bytes > 100L * 1024 * 1024 * 1024 - total) throw new InvalidDataException("Backup exceeds the 100 GiB restore limit.");
            total += file.Bytes;
        }
        if (!names.Contains(DatabaseEntry) || zip.Entries.Count != names.Count + 1 ||
            zip.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != zip.Entries.Count ||
            zip.Entries.Any(e => e.FullName != "manifest.json" && !names.Contains(e.FullName)))
            throw new InvalidDataException("ZIP contents do not match the manifest.");
        var database = Path.Combine(work, $"verify-{Guid.NewGuid():N}.db");
        try
        {
            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var entry = zip.GetEntry(file.Path) ?? throw new InvalidDataException("A manifest file is missing.");
                if (entry.Length != file.Bytes) throw new InvalidDataException($"Length mismatch: {file.Path}");
                await using var input = entry.Open();
                await using var output = file.Path == DatabaseEntry ? PrivateFile(database, FileMode.CreateNew) : null;
                var actual = await CopyHashAsync(input, output ?? Stream.Null, file.Path, ct, file.Bytes);
                if (actual != file) throw new InvalidDataException($"Checksum mismatch: {file.Path}");
            }
            var snapshot = await source.InspectSnapshotAsync(database, ct);
            if (snapshot.SchemaVersion != manifest.SchemaVersion)
                throw new InvalidDataException("Catalog schema does not match manifest.");
            var originals = manifest.Files.Where(f => f.Path.EndsWith("/original", StringComparison.Ordinal)).ToDictionary(f => f.Path);
            if (originals.Count != snapshot.Assets.Count) throw new InvalidDataException("Artwork inventory does not match catalog.");
            foreach (var asset in snapshot.Assets)
            {
                if (!originals.TryGetValue($"artwork/{asset.Id}/original", out var original) ||
                    original.Bytes != asset.Bytes || original.Sha256 != asset.Hash)
                    throw new InvalidDataException($"Artwork {asset.Id} does not match snapshot.");
            }
            if (manifest.Files.Any(f => f.Path.EndsWith("/thumbnail.jpg", StringComparison.Ordinal) &&
                !originals.ContainsKey(f.Path.Replace("/thumbnail.jpg", "/original", StringComparison.Ordinal))))
                throw new InvalidDataException("Thumbnail has no catalog asset.");
            return manifest with { Warnings = snapshot.Warnings };
        }
        finally { File.Delete(database); }
    }

    private CatalogBackupInfo? ReadInfo(string path)
    {
        var filename = Path.GetFileName(path);
        var match = ArchiveName().Match(filename);
        if (!match.Success || match.Groups[1].Value != source.Kind) return null;
        RejectLink(path);
        using var zip = ZipFile.OpenRead(path);
        var manifest = ReadManifest(zip);
        if (manifest.Kind != source.Kind) throw new InvalidDataException("Backup filename type does not match manifest.");
        if (manifest.CatalogKey != source.CatalogKey) return null;
        if (manifest.Id != match.Groups[2].Value ||
            filename != Filename(manifest.CreatedUtc, manifest.Id))
            throw new InvalidDataException("Backup filename does not match manifest.");
        return new(manifest.Id, filename, manifest.CreatedUtc, new FileInfo(path).Length);
    }
    public Task<IReadOnlyList<CatalogBackupInfo>> ListAsync(CancellationToken ct = default)
    {
        CheckRoot();
        var results = new List<CatalogBackupInfo>();
        foreach (var path in Directory.EnumerateFiles(Root, "*.zip"))
        {
            ct.ThrowIfCancellationRequested();
            var info = ReadInfo(path);
            if (info is not null) results.Add(info);
        }
        return Task.FromResult<IReadOnlyList<CatalogBackupInfo>>(results.OrderByDescending(r => r.CreatedUtc).ThenBy(r => r.Id).ToArray());
    }
    private async Task<IReadOnlyList<string>> PruneAsync(CancellationToken ct)
    {
        var warnings = new List<string>();
        var backups = await ListAsync(ct);
        var verified = 0;
        foreach (var backup in backups)
        {
            var work = WorkDirectory();
            try
            {
                var path = Path.Combine(Root, backup.Filename);
                await VerifyArchiveAsync(path, work, ct);
                if (++verified > Keep) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                warnings.Add($"Could not verify/remove older backup {backup.Filename}: {ex.Message}");
            }
            finally { Directory.Delete(work, recursive: true); }
        }
        return warnings;
    }
    public async Task<BackupManifest> VerifyAsync(string archive, CancellationToken ct = default)
    {
        await using var lease = Acquire();
        var work = WorkDirectory();
        try { return await VerifyArchiveAsync(archive, work, ct); }
        finally { Directory.Delete(work, recursive: true); }
    }
    public async Task RestoreAsync(string archive, string destination, CancellationToken ct = default)
    {
        await using var lease = Acquire();
        var target = new ManagedArtworkStore(destination).Root;
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Restore requires a new destination directory.");
        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Restore parent directory must already exist.");
        var staging = Path.Combine(parent, $".restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        PrivateDirectory(staging);
        try
        {
            var manifest = await VerifyArchiveAsync(archive, staging, ct);
            using var zip = ZipFile.OpenRead(archive);
            if (source.Kind == "catalog")
            {
                Directory.CreateDirectory(Path.Combine(staging, "artwork"));
                PrivateDirectory(Path.Combine(staging, "artwork"));
            }
            foreach (var file in manifest.Files)
            {
                var path = Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                PrivateDirectory(Path.GetDirectoryName(path)!);
                await using var input = zip.GetEntry(file.Path)!.Open();
                await using var output = PrivateFile(path, FileMode.CreateNew);
                var actual = await CopyHashAsync(input, output, file.Path, ct, file.Bytes);
                if (actual != file) throw new InvalidDataException("Archive changed during restore.");
            }
            await source.InspectSnapshotAsync(Path.Combine(staging, DatabaseEntry), ct);
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }
    public async Task<Stream> OpenDownloadAsync(string id, CancellationToken ct = default)
    {
        var lease = Acquire();
        try
        {
            var info = (await ListAsync(ct)).SingleOrDefault(b => b.Id == id) ?? throw new FileNotFoundException("Backup not found.");
            var work = WorkDirectory();
            try { await VerifyArchiveAsync(Path.Combine(Root, info.Filename), work, ct); }
            finally { Directory.Delete(work, recursive: true); }
            var stream = new FileStream(Path.Combine(Root, info.Filename), FileMode.Open, FileAccess.Read, FileShare.Read);
            return new BackupDownloadStream(stream, lease);
        }
        catch { lease.Dispose(); throw; }
    }
    private sealed class BackupDownloadStream(Stream content, Stream lease) : Stream
    {
        public override bool CanRead => content.CanRead;
        public override bool CanSeek => content.CanSeek;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => content.Position; set => content.Position = value; }
        public override void Flush() => content.Flush();
        public override int Read(byte[] buffer, int offset, int count) => content.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => content.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => content.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { content.Dispose(); lease.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
