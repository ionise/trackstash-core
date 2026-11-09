using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests.Sqlite;

public sealed class MediaLinkPersistenceTests
{
    [Fact]
    public async Task MediaLinks_OnlyAcceptedCopiesAreListedAndAllHashOwnersAreChecked()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trackstash-core-media-{Guid.NewGuid():N}.db");
        try
        {
            var provider = new SqliteStorageProvider(path);
            await provider.Migrations.ApplyPendingMigrationsAsync();
            await using var unit = await provider.BeginUnitOfWorkAsync();
            await unit.Recordings.UpsertAsync(new Recording { Id = "r1", Title = "First", NormalizedName = "first" });
            await unit.Recordings.UpsertAsync(new Recording { Id = "r2", Title = "Second", NormalizedName = "second" });
            foreach (var (id, state, recording) in new[] {
                ("a", MatchOverrideState.Accepted, "r1"), ("b", MatchOverrideState.Manual, "r1"),
                ("c", MatchOverrideState.None, "r2"), ("d", MatchOverrideState.Rejected, "r2")
            })
            {
                await unit.MediaFiles.UpsertAsync(new MediaFile { Id = id, Path = "/test/" + id, ContentHash = "AbCd" });
                await unit.Matches.UpsertBestMatchAsync(new MatchRecord { MediaFileId = id, RecordingId = recording, OverrideState = state });
            }
            Assert.Equal(new[] { "a", "b" }, (await unit.MediaFiles.ListByRecordingAsync("r1")).Select(f => f.Id));
            Assert.Empty(await unit.MediaFiles.ListByRecordingAsync("r2"));
            Assert.Equal(2, await unit.MediaFiles.CountByRecordingAsync("r1"));
            Assert.Equal(0, await unit.MediaFiles.CountByRecordingAsync("r2"));
            Assert.False(await unit.MediaFiles.HasConflictingRecordingMatchAsync("abcd", "r1"));
            Assert.True(await unit.MediaFiles.HasConflictingRecordingMatchAsync("abcd", "r2"));
            await unit.Matches.SetUserOverrideAsync("d", "r2", MatchOverrideState.Manual);
            Assert.True(await unit.MediaFiles.HasConflictingRecordingMatchAsync("ABCD", "r1"));
            Assert.False(await unit.MediaFiles.HasConflictingRecordingMatchAsync("unrelated-hash", "r1"));
            await unit.CommitAsync();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task MediaLinks_ListIncludesOneOverflowSentinelBeyondHundredCopies()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trackstash-core-media-limit-{Guid.NewGuid():N}.db");
        try
        {
            var provider = new SqliteStorageProvider(path);
            await provider.Migrations.ApplyPendingMigrationsAsync();
            await using var unit = await provider.BeginUnitOfWorkAsync();
            await unit.Recordings.UpsertAsync(new Recording { Id = "r", Title = "Tune", NormalizedName = "tune" });
            for (var index = 0; index < 102; index++)
            {
                var id = $"copy-{index:D3}";
                await unit.MediaFiles.UpsertAsync(new MediaFile { Id = id, Path = "/test/" + id });
                await unit.Matches.UpsertBestMatchAsync(new MatchRecord { MediaFileId = id, RecordingId = "r", OverrideState = MatchOverrideState.Manual });
            }
            Assert.Equal(101, (await unit.MediaFiles.ListByRecordingAsync("r")).Count);
            Assert.Equal(102, await unit.MediaFiles.CountByRecordingAsync("r"));
            await unit.CommitAsync();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
