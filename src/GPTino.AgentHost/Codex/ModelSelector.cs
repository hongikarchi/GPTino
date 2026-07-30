using GPTino.AgentHost.Api;
using GPTino.Contracts;

namespace GPTino.AgentHost.Codex;

public interface IModelCatalog
{
    Task<IReadOnlyList<ModelView>> ListModelsAsync(CancellationToken cancellationToken = default);
}

public sealed record ModelSelection(
    string? Model,
    string? Effort,
    ModelProfile EffectiveProfile,
    string Rationale);

/// <summary>
/// Resolves the concrete Codex model + reasoning effort for a turn. Adaptive per-message routing and
/// the fast/standard/deep capability profiles were removed: the session's effort is manual and used
/// directly (see <see cref="ResolveDirectAsync"/>), only clamped to what the chosen model advertises.
/// </summary>
public sealed class ModelSelector
{
    // Reasoning-effort levels in ascending strength, used only to clamp a requested effort to a model's
    // advertised set. The operator's chosen effort is authoritative — no floor, no escalation.
    private static readonly string[] EffortOrder = ["low", "medium", "high", "xhigh", "max", "ultra"];

    private readonly IModelCatalog _catalog;
    private readonly ILogger<ModelSelector> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<ModelView>? _cached;

    public ModelSelector(IModelCatalog catalog, ILogger<ModelSelector> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>
    /// Direct effort selection: pick the pinned model (or the catalog default) and clamp the requested
    /// reasoning effort to that model's advertised set — the manual "effort slider" path.
    /// </summary>
    public async Task<ModelSelection> ResolveDirectAsync(
        string? effort,
        string? pinnedModel,
        CancellationToken cancellationToken)
    {
        var requested = string.IsNullOrWhiteSpace(effort) ? "xhigh" : effort.Trim().ToLowerInvariant();
        var models = await ReadModelsAsync(cancellationToken).ConfigureAwait(false);
        ModelView? model = null;
        if (!string.IsNullOrWhiteSpace(pinnedModel))
        {
            model = FindExplicit(models, pinnedModel);
        }
        model ??= models.FirstOrDefault(item => item.IsDefault) ?? models.FirstOrDefault();

        var resolved = model is { ReasoningEfforts.Count: > 0 }
            ? ClampEffort(requested, model.ReasoningEfforts)
            : requested;
        var chosenModel = model?.Model ?? pinnedModel;
        var rationale = model is null
            ? $"Model catalog unavailable; requested reasoning effort '{resolved}'."
            : $"Model '{model.Model}' with reasoning effort '{resolved}'"
              + (string.Equals(resolved, requested, StringComparison.OrdinalIgnoreCase)
                  ? "."
                  : $" (clamped from '{requested}' to the model's supported range).");
        return new ModelSelection(chosenModel, resolved, ModelProfile.Standard, rationale);
    }

    private static string ClampEffort(string requested, IReadOnlyList<string> supported)
    {
        var exact = supported.FirstOrDefault(e => string.Equals(e, requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }
        var reqRank = EffortRank(requested);
        var atOrBelow = supported
            .Where(e => EffortRank(e) <= reqRank)
            .OrderByDescending(EffortRank)
            .FirstOrDefault();
        return atOrBelow ?? supported.OrderBy(EffortRank).First();
    }

    private static int EffortRank(string effort)
    {
        var normalized = effort.Trim().ToLowerInvariant() switch
        {
            "minimal" or "none" => "low",
            "extra-high" => "xhigh",
            "maximum" => "max",
            var other => other
        };
        var index = Array.IndexOf(EffortOrder, normalized);
        return index < 0 ? EffortOrder.Length : index;
    }

    public Task<IReadOnlyList<ModelView>> ReadModelsAsync(CancellationToken cancellationToken) =>
        ReadModelsCoreAsync(cancellationToken);

    private async Task<IReadOnlyList<ModelView>> ReadModelsCoreAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }
            try
            {
                var models = await _catalog.ListModelsAsync(cancellationToken).ConfigureAwait(false);
                if (models.Count > 0)
                {
                    _cached = models;
                }
                return models;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Could not read the Codex model catalog; the requested effort is used with no concrete model.");
                return [];
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ModelView? FindExplicit(IEnumerable<ModelView> models, string explicitModel) =>
        models.FirstOrDefault(item =>
            string.Equals(item.Model, explicitModel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Id, explicitModel, StringComparison.OrdinalIgnoreCase));
}
