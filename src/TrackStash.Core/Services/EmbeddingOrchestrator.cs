namespace TrackStash.Core.Services;

public sealed record EmbeddingOrchestratorOptions
{
    public string DefaultModelName { get; init; } = "MiniLM";

    public string DefaultModelVersion { get; init; } = "all-MiniLM-L6-v2";

    public bool AllowPreferredModelOverride { get; init; } = true;
}

/// <summary>
/// Minimal default orchestrator that only queues embedding work.
/// It intentionally does not generate vectors inline, so canonical write paths remain fast.
/// </summary>
public sealed class DefaultEmbeddingOrchestrator : IEmbeddingOrchestrator
{
    private readonly IEmbeddingWorkItemStore _workItemStore;
    private readonly EmbeddingOrchestratorOptions _options;

    public DefaultEmbeddingOrchestrator(IEmbeddingWorkItemStore workItemStore, EmbeddingOrchestratorOptions? options = null)
    {
        _workItemStore = workItemStore ?? throw new ArgumentNullException(nameof(workItemStore));
        _options = options ?? new EmbeddingOrchestratorOptions();
    }

    public ValueTask<EmbeddingEnqueueResult> QueueEntityChangedAsync(
        EmbeddingEntityChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.EntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.EntityType);

        var model = ResolveModel(change.PreferredModel);
        return _workItemStore.EnqueueAsync(change, model, cancellationToken);
    }

    private EmbeddingModelIdentity ResolveModel(EmbeddingModelIdentity? preferredModel)
    {
        if (_options.AllowPreferredModelOverride && preferredModel is not null)
        {
            if (!string.IsNullOrWhiteSpace(preferredModel.ModelName) &&
                !string.IsNullOrWhiteSpace(preferredModel.ModelVersion))
            {
                return preferredModel;
            }
        }

        return new EmbeddingModelIdentity(_options.DefaultModelName, _options.DefaultModelVersion);
    }
}
