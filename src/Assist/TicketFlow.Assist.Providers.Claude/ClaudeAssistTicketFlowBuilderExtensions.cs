using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TicketFlow.Assist;
using TicketFlow.Assist.Providers.Claude;

namespace TicketFlow;

public static class ClaudeAssistTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers the Claude provider (singleton <see cref="ClaudeAssistProvider"/> as an <see cref="IAssistChatProvider"/>), adds the
    /// Claude 5 models (<c>claude-haiku-5-5</c>, <c>claude-sonnet-5-5</c>, <c>claude-opus-5-5</c>) to <see cref="AssistOptions"/> and
    /// fills unset feature defaults: <see cref="ClaudeAssistOptions.ClassificationModel"/> for triage and sentiment,
    /// <see cref="ClaudeAssistOptions.GenerationModel"/> for summary, draft, translation and tone. The API key comes from options or
    /// <see cref="IConfiguration"/>; requests are only sent when a feature runs.
    /// </summary>
    public static TicketFlowBuilder AddClaudeAssist(this TicketFlowBuilder builder, Action<ClaudeAssistOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<ClaudeAssistOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ClaudeMarker)))
        {
            services.AddSingleton<ClaudeMarker>();
            services.AddOptions<AssistOptions>().Configure<IOptions<ClaudeAssistOptions>>((assist, claude) =>
            {
                foreach (var model in ClaudeAssistProvider.DefaultModels)
                    if (!assist.Models.Any(existing => existing.Id == model.Id))
                        assist.Models.Add(model);
                foreach (var feature in Enum.GetValues<AssistFeature>())
                    assist.FeatureModels.TryAdd(feature, feature is AssistFeature.Triage or AssistFeature.SentimentAndIntent
                        ? claude.Value.ClassificationModel
                        : claude.Value.GenerationModel);
            });
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider => new ClaudeAssistProvider(provider.GetRequiredService<IOptions<ClaudeAssistOptions>>(),
            provider.GetService<TimeProvider>(), provider.GetService<IConfiguration>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAssistChatProvider, ClaudeAssistProviderRegistration>());
        return builder;
    }

    private sealed class ClaudeMarker;

    /// <summary>Forwards to the registered singleton (a distinct type so TryAddEnumerable deduplicates).</summary>
    private sealed class ClaudeAssistProviderRegistration(ClaudeAssistProvider provider) : IAssistChatProvider
    {
        public string Name => provider.Name;

        public Microsoft.Extensions.AI.IChatClient CreateClient(AssistModelDefinition model) => provider.CreateClient(model);

        public Microsoft.Extensions.AI.ChatOptions ApplyOptions(AssistModelDefinition model, AssistChatRequest request, Microsoft.Extensions.AI.ChatOptions options) =>
            provider.ApplyOptions(model, request, options);
    }
}
