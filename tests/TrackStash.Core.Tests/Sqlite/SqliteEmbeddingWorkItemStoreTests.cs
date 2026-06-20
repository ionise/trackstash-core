using TrackStash.Core.Services;
using TrackStash.Core.Sqlite;
using Xunit;

namespace TrackStash.Core.Tests.Sqlite;

public sealed class SqliteEmbeddingWorkItemStoreTests
{
    [Fact]
    public async Task EnqueueAsync_DeduplicatesActiveWorkItem_ForSameEntityAndModel()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"trackstash-core-embed-queue-{Guid.NewGuid():N}.db");
        var provider = new SqliteStorageProvider(dbPath);
        var store = new SqliteEmbeddingWorkItemStore(dbPath);

        try
        {
            await provider.Migrations.ApplyPendingMigrationsAsync();

            var model = new EmbeddingModelIdentity("MiniLM", "dev-v1");
            var change = new EmbeddingEntityChange(
                EntityId: "label-1",
                EntityType: "Label",
                Operation: EmbeddingChangeOperation.Updated,
                OccurredUtc: DateTimeOffset.UtcNow);

            var first = await store.EnqueueAsync(change, model);
            var second = await store.EnqueueAsync(change, model);

            Assert.True(first.Enqueued);
            Assert.False(first.Deduplicated);

            Assert.False(second.Enqueued);
            Assert.True(second.Deduplicated);
            Assert.Equal(first.WorkItemId, second.WorkItemId);
        }
        finally
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task LeaseAndCompletion_Flow_TransitionsWorkItemOutOfQueue()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"trackstash-core-embed-queue-{Guid.NewGuid():N}.db");
        var provider = new SqliteStorageProvider(dbPath);
        var store = new SqliteEmbeddingWorkItemStore(dbPath);

        try
        {
            await provider.Migrations.ApplyPendingMigrationsAsync();

            var model = new EmbeddingModelIdentity("MiniLM", "dev-v1");
            var change = new EmbeddingEntityChange(
                EntityId: "artist-1",
                EntityType: "Artist",
                Operation: EmbeddingChangeOperation.Created,
                OccurredUtc: DateTimeOffset.UtcNow);

            await store.EnqueueAsync(change, model);

            var now = DateTimeOffset.UtcNow;
            var leased = await store.LeasePendingAsync(10, TimeSpan.FromMinutes(1), now);
            Assert.Single(leased);

            await store.MarkCompletedAsync(leased[0].WorkItemId, now.AddSeconds(5));

            var afterComplete = await store.LeasePendingAsync(10, TimeSpan.FromMinutes(1), now.AddMinutes(1));
            Assert.Empty(afterComplete);
        }
        finally
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task MarkFailedAsync_MakesItemReleasableAfterRetryDelay()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"trackstash-core-embed-queue-{Guid.NewGuid():N}.db");
        var provider = new SqliteStorageProvider(dbPath);
        var store = new SqliteEmbeddingWorkItemStore(dbPath);

        try
        {
            await provider.Migrations.ApplyPendingMigrationsAsync();

            var model = new EmbeddingModelIdentity("MiniLM", "dev-v1");
            var change = new EmbeddingEntityChange(
                EntityId: "release-1",
                EntityType: "Release",
                Operation: EmbeddingChangeOperation.Updated,
                OccurredUtc: DateTimeOffset.UtcNow);

            await store.EnqueueAsync(change, model);

            var now = DateTimeOffset.UtcNow;
            var leased = await store.LeasePendingAsync(1, TimeSpan.FromMinutes(1), now);
            Assert.Single(leased);

            await store.MarkFailedAsync(leased[0].WorkItemId, "temporary failure", 1, now);

            var immediate = await store.LeasePendingAsync(1, TimeSpan.FromMinutes(1), now.AddMilliseconds(100));
            Assert.Empty(immediate);

            var retry = await store.LeasePendingAsync(1, TimeSpan.FromMinutes(1), now.AddSeconds(2));
            Assert.Single(retry);
            Assert.Equal(leased[0].WorkItemId, retry[0].WorkItemId);
        }
        finally
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }
}
