using TrackStash.Core.Services;
using Xunit;

namespace TrackStash.Core.Tests.Services;

public sealed class DefaultEmbeddingOrchestratorTests
{
    [Fact]
    public async Task QueueEntityChangedAsync_UsesDefaultModel_WhenPreferredModelMissing()
    {
        var store = new FakeStore();
        var orchestrator = new DefaultEmbeddingOrchestrator(store, new EmbeddingOrchestratorOptions
        {
            DefaultModelName = "MiniLM",
            DefaultModelVersion = "dev-v1",
        });

        var change = new EmbeddingEntityChange(
            EntityId: "artist-1",
            EntityType: "Artist",
            Operation: EmbeddingChangeOperation.Updated,
            OccurredUtc: DateTimeOffset.UtcNow);

        var result = await orchestrator.QueueEntityChangedAsync(change);

        Assert.True(result.Enqueued);
        Assert.NotNull(store.LastModel);
        Assert.Equal("MiniLM", store.LastModel!.ModelName);
        Assert.Equal("dev-v1", store.LastModel.ModelVersion);
    }

    [Fact]
    public async Task QueueEntityChangedAsync_UsesPreferredModel_WhenOverrideEnabled()
    {
        var store = new FakeStore();
        var orchestrator = new DefaultEmbeddingOrchestrator(store, new EmbeddingOrchestratorOptions
        {
            DefaultModelName = "MiniLM",
            DefaultModelVersion = "dev-v1",
            AllowPreferredModelOverride = true,
        });

        var change = new EmbeddingEntityChange(
            EntityId: "release-1",
            EntityType: "Release",
            Operation: EmbeddingChangeOperation.Created,
            OccurredUtc: DateTimeOffset.UtcNow,
            PreferredModel: new EmbeddingModelIdentity("MiniLM", "prod-v2"));

        var result = await orchestrator.QueueEntityChangedAsync(change);

        Assert.True(result.Enqueued);
        Assert.NotNull(store.LastModel);
        Assert.Equal("MiniLM", store.LastModel!.ModelName);
        Assert.Equal("prod-v2", store.LastModel.ModelVersion);
    }

    private sealed class FakeStore : IEmbeddingWorkItemStore
    {
        public EmbeddingModelIdentity? LastModel { get; private set; }

        public ValueTask<EmbeddingEnqueueResult> EnqueueAsync(EmbeddingEntityChange change, EmbeddingModelIdentity model, CancellationToken cancellationToken = default)
        {
            LastModel = model;
            return ValueTask.FromResult(new EmbeddingEnqueueResult(true, false, "work-1"));
        }

        public ValueTask<IReadOnlyList<EmbeddingWorkItem>> LeasePendingAsync(int maxItems, TimeSpan leaseDuration, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<EmbeddingWorkItem>>(Array.Empty<EmbeddingWorkItem>());

        public ValueTask MarkCompletedAsync(string workItemId, DateTimeOffset completedUtc, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask MarkFailedAsync(string workItemId, string error, int nextAttemptInSeconds, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
