using Microsoft.Extensions.Options;

namespace TicketFlow.Assist;

/// <summary>The default <see cref="IAssistModelRegistry"/> over <see cref="AssistOptions.Models"/> and <see cref="AssistOptions.FeatureModels"/>.</summary>
public sealed class AssistModelRegistry : IAssistModelRegistry
{
    private readonly Dictionary<string, AssistModelDefinition> models;
    private readonly Dictionary<AssistFeature, string> features;

    public AssistModelRegistry(IOptions<AssistOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        models = new(StringComparer.Ordinal);
        foreach (var model in value.Models)
            if (!models.TryAdd(model.Id, model) && models[model.Id] != model)
                throw new InvalidOperationException($"Model '{model.Id}' is registered twice with different definitions.");
        features = new(value.FeatureModels);
        foreach (var (feature, id) in features)
            if (!models.ContainsKey(id))
                throw new InvalidOperationException($"The default model '{id}' for {feature} is not registered.");
    }

    public IReadOnlyCollection<AssistModelDefinition> All => models.Values;

    public bool TryGet(string id, out AssistModelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(id);
        return models.TryGetValue(id, out definition!);
    }

    public string DefaultFor(AssistFeature feature) =>
        features.TryGetValue(feature, out var id) ? id : throw new InvalidOperationException($"No default model is configured for {feature}; register a provider such as AddClaudeAssist().");
}
