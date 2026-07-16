using GPTino.AgentHost.Api;
using GPTino.Contracts;
using GPTino.Core;

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

public sealed class ModelSelector
{
    private static readonly string[] SmallestModelMarkers = ["nano", "tiny", "small"];
    private static readonly string[] WeakModelMarkers = ["mini", "nano", "tiny", "small", "spark", "luna", "flash", "fast"];
    private static readonly string[] HighReasoningEfforts = ["xhigh", "extra-high", "maximum", "ultra", "high"];

    private readonly IModelCatalog _catalog;
    private readonly ILogger<ModelSelector> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<ModelView>? _cached;

    public ModelSelector(IModelCatalog catalog, ILogger<ModelSelector> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<ModelSelection> SelectAsync(
        MessageRoute route,
        string? explicitModel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        var models = await ReadModelsAsync(cancellationToken).ConfigureAwait(false);
        if (models.Count == 0)
        {
            if (RequiresStrongCatalog(route.EffectiveProfile))
            {
                throw new ModelRoutingException(
                    $"The Codex model catalog is unavailable; {route.EffectiveProfile} routing fails closed.");
            }

            var fallbackEffort = EffortPreferences(route.EffectiveProfile)[0];
            return new ModelSelection(
                explicitModel,
                fallbackEffort,
                route.EffectiveProfile,
                $"{route.Rationale} The catalog was unavailable, so the App Server default is used only for this non-high-assurance route.");
        }

        ModelView? model;
        if (!string.IsNullOrWhiteSpace(explicitModel))
        {
            model = FindExplicit(models, explicitModel) ?? throw new ModelRoutingException(
                $"Pinned model '{explicitModel}' is unavailable in the Codex model catalog.");
            EnsureEligible(model, route.EffectiveProfile);
        }
        else
        {
            model = SelectAutomatic(models, route.EffectiveProfile) ?? throw new ModelRoutingException(
                $"No available Codex model satisfies the {route.EffectiveProfile} capability floor.");
        }

        var effort = SelectEffort(model, route.EffectiveProfile);
        var rationale = $"{route.Rationale} Selected catalog model '{model.Model}' with reasoning effort '{effort}'.";
        return new ModelSelection(model.Model, effort, route.EffectiveProfile, rationale);
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
                _logger.LogWarning(exception,
                    "Could not read the Codex model catalog; high-assurance and recovery routes will fail closed.");
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

    private static ModelView? SelectAutomatic(IReadOnlyList<ModelView> models, ModelProfile profile) => profile switch
    {
        ModelProfile.ReadFast => models
            .OrderByDescending(IsCompact)
            .ThenByDescending(model => model.IsDefault)
            .ThenBy(model => model.Model, StringComparer.Ordinal)
            .FirstOrDefault(),
        ModelProfile.FastSafe => models
            .Where(model => !ContainsIdentityMarker(model, SmallestModelMarkers))
            .OrderByDescending(IsCompact)
            .ThenByDescending(model => model.IsDefault)
            .ThenBy(model => model.Model, StringComparer.Ordinal)
            .FirstOrDefault(),
        ModelProfile.Standard => models
            .Where(model => !ContainsIdentityMarker(model, SmallestModelMarkers))
            .OrderByDescending(model => model.IsDefault)
            .ThenBy(model => IsCompact(model) ? 1 : 0)
            .ThenBy(model => model.Model, StringComparer.Ordinal)
            .FirstOrDefault(),
        ModelProfile.HighAssurance => StrongCandidates(models)
            .OrderByDescending(model => model.IsDefault)
            .ThenByDescending(ReasoningStrength)
            .ThenBy(model => model.Model, StringComparer.Ordinal)
            .FirstOrDefault(),
        ModelProfile.Recovery => StrongCandidates(models)
            .OrderByDescending(ReasoningStrength)
            .ThenByDescending(model => model.IsDefault)
            .ThenBy(model => model.Model, StringComparer.Ordinal)
            .FirstOrDefault(),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
    };

    private static IEnumerable<ModelView> StrongCandidates(IEnumerable<ModelView> models) =>
        models.Where(model => !IsWeak(model) && SupportsHighReasoning(model));

    private static void EnsureEligible(ModelView model, ModelProfile profile)
    {
        if (RequiresStrongCatalog(profile) && (IsWeak(model) || !SupportsHighReasoning(model)))
        {
            throw new ModelRoutingException(
                $"Pinned model '{model.Model}' is below the {profile} capability floor.");
        }

        if (profile == ModelProfile.Standard && ContainsIdentityMarker(model, SmallestModelMarkers))
        {
            throw new ModelRoutingException(
                $"Pinned model '{model.Model}' is below the Standard capability floor.");
        }

        if (profile == ModelProfile.FastSafe && ContainsIdentityMarker(model, SmallestModelMarkers))
        {
            throw new ModelRoutingException(
                $"Pinned model '{model.Model}' is not qualified for fast live writes.");
        }
    }

    private static string SelectEffort(ModelView model, ModelProfile profile)
    {
        var preferences = EffortPreferences(profile);
        if (model.ReasoningEfforts.Count == 0)
        {
            if (RequiresStrongCatalog(profile))
            {
                throw new ModelRoutingException(
                    $"Model '{model.Model}' does not advertise a high reasoning effort required by {profile}.");
            }
            return preferences[0];
        }

        var selected = preferences.FirstOrDefault(candidate =>
            model.ReasoningEfforts.Contains(candidate, StringComparer.OrdinalIgnoreCase));
        if (selected is null && RequiresStrongCatalog(profile))
        {
            throw new ModelRoutingException(
                $"Model '{model.Model}' does not support a high reasoning effort required by {profile}.");
        }
        return selected ?? model.ReasoningEfforts[0];
    }

    private static string[] EffortPreferences(ModelProfile profile) => profile switch
    {
        ModelProfile.ReadFast => ["minimal", "low", "medium"],
        ModelProfile.FastSafe => ["low", "medium", "high"],
        ModelProfile.Standard => ["medium", "high", "low"],
        ModelProfile.HighAssurance => ["xhigh", "extra-high", "maximum", "ultra", "high"],
        ModelProfile.Recovery => ["xhigh", "maximum", "ultra", "extra-high", "high"],
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
    };

    private static bool RequiresStrongCatalog(ModelProfile profile) =>
        profile is ModelProfile.HighAssurance or ModelProfile.Recovery;

    private static bool SupportsHighReasoning(ModelView model) =>
        model.ReasoningEfforts.Any(effort =>
            HighReasoningEfforts.Contains(effort, StringComparer.OrdinalIgnoreCase));

    private static int ReasoningStrength(ModelView model)
    {
        if (model.ReasoningEfforts.Contains("xhigh", StringComparer.OrdinalIgnoreCase) ||
            model.ReasoningEfforts.Contains("maximum", StringComparer.OrdinalIgnoreCase) ||
            model.ReasoningEfforts.Contains("ultra", StringComparer.OrdinalIgnoreCase))
        {
            return 2;
        }
        return model.ReasoningEfforts.Contains("high", StringComparer.OrdinalIgnoreCase) ? 1 : 0;
    }

    private static bool IsCompact(ModelView model) => ContainsIdentityMarker(model, WeakModelMarkers);

    private static bool IsWeak(ModelView model) => ContainsIdentityMarker(model, WeakModelMarkers);

    private static bool ContainsIdentityMarker(ModelView model, IEnumerable<string> values)
    {
        var searchable = $"{model.Id} {model.Model} {model.DisplayName}";
        return values.Any(value => searchable.Contains(value, StringComparison.OrdinalIgnoreCase));
    }
}
