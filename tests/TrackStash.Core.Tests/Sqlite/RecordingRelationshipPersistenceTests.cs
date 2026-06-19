using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;
using Xunit;

namespace TrackStash.Core.Tests.Sqlite;

public sealed class RecordingRelationshipPersistenceTests
{
    [Fact]
    public async Task UpsertAsync_PersistsReleaseLinksAndRecordingRelationships()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"trackstash-core-recording-{Guid.NewGuid():N}.db");
        var provider = new SqliteStorageProvider(dbPath);

        try
        {
            await provider.Migrations.ApplyPendingMigrationsAsync();

            await using var uow = await provider.BeginUnitOfWorkAsync();

            await uow.Releases.UpsertAsync(new Release
            {
                Id = "release-1",
                Title = "Synthetic Release",
                NormalizedName = "syntheticrelease",
                CreatedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow,
            });

            await uow.Recordings.UpsertAsync(new Recording
            {
                Id = "recording-2",
                Title = "Source Recording",
                NormalizedName = "sourcerecording",
                Isrc = "TST000000002",
                CreatedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow,
            });

            await uow.Recordings.UpsertAsync(new Recording
            {
                Id = "recording-1",
                Title = "Synthetic Recording",
                NormalizedName = "syntheticrecording",
                Isrc = "TST000000001",
                CreatedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow,
                ReleaseLinks =
                [
                    new RecordingReleaseLink
                    {
                        ReleaseId = "release-1",
                        DiscNumber = 1,
                        TrackNumber = 2,
                    },
                ],
                Relationships =
                [
                    new RecordingRelationship
                    {
                        RelatedRecordingId = "recording-2",
                        RelationshipType = "remix_of",
                        Source = "synthetic",
                        Confidence = 0.93m,
                        Notes = "fixture",
                        CreatedUtc = DateTimeOffset.UtcNow,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                    },
                ],
            });

            await uow.CommitAsync();

            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();

            Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM release_recording WHERE release_id = 'release-1' AND recording_id = 'recording-1'"));
            Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM recording_relationship WHERE from_recording_id = 'recording-1' AND to_recording_id = 'recording-2' AND relationship_type = 'remix_of'"));
        }
        finally
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }
}
