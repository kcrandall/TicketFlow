using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.HelpCenter;

namespace TicketFlow.Assist;

/// <summary>
/// Default <see cref="IAssistService"/>. Flow per call: build context through <see cref="AssistContextBuilder"/> (authorization and
/// masking), resolve the workspace settings and model, reserve estimated tokens atomically in <see cref="IAssistStore"/> (quota and
/// rate limit), call the model through <see cref="IAssistChatClientFactory"/> with a JSON schema, settle actual usage, validate the
/// output and record <c>assist.*</c> metadata on the timeline. Model output is untrusted: it is parsed into bounded, validated values
/// and never executed. Prompts and outputs are never logged or persisted.
/// </summary>
public sealed class AssistService : IAssistService
{
    private readonly AssistContextBuilder contextBuilder;
    private readonly IAssistChatClientFactory clients;
    private readonly IAssistModelRegistry registry;
    private readonly IAssistStore store;
    private readonly IConversationService conversations;
    private readonly AssistOptions options;
    private readonly TimeProvider time;

    public AssistService(AssistContextBuilder contextBuilder, IAssistChatClientFactory clients, IAssistModelRegistry registry, IAssistStore store,
        IConversationService conversations, IOptions<AssistOptions> options, TimeProvider time)
    {
        this.contextBuilder = contextBuilder ?? throw new ArgumentNullException(nameof(contextBuilder));
        this.clients = clients ?? throw new ArgumentNullException(nameof(clients));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<AssistResult<AssistSummary>> SummarizeAsync(PrincipalRef actor, AssistSummaryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await RunAsync(actor, request.Conversation, new AssistContextRequest(), new Call<AssistSummary>(
            AssistFeature.ConversationSummary, "summary", AssistPrompts.SummarySchema, 4096, null,
            _ => "Summarize the conversation in <support_data> for the agent.", null, (root, _) => ParseSummary(root),
            CachedFormat: AssistPrompts.SummaryFormat), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssistResult<AssistDraft>> DraftReplyAsync(PrincipalRef actor, AssistDraftRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await RunAsync(actor, request.Conversation, new AssistContextRequest(Grounding: request.Grounding), new Call<AssistDraft>(
            AssistFeature.ReplyDraft, "reply_draft", AssistPrompts.DraftSchema, 8192, null,
            context =>
            {
                var task = new StringBuilder("Draft the agent's next reply to the customer.");
                if (context.Snippets.Count == 0)
                    task.Append(" No help center articles are available; leave citations empty.");
                if (request.Instructions is { } instructions)
                    task.Append("\n<agent_instructions>").Append(AssistContextBuilder.Escape(instructions)).Append("</agent_instructions>");
                return task.ToString();
            }, null, ParseDraft, CachedFormat: AssistPrompts.DraftFormat), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssistResult<AssistTriage>> TriageAsync(PrincipalRef actor, AssistTriageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var labels = new string[request.Labels.Count];
        for (var i = 0; i < labels.Length; i++)
            labels[i] = request.Labels[i].Label.Value;
        var teams = new string[request.Teams.Count];
        for (var i = 0; i < teams.Length; i++)
            teams[i] = request.Teams[i].Team.Value;
        return await RunAsync(actor, request.Conversation, new AssistContextRequest(IncludeContact: false), new Call<AssistTriage>(
            AssistFeature.Triage, "triage", AssistPrompts.TriageSchema(labels, teams), 2048, AssistThinkingLevel.Low,
            _ =>
            {
                var task = new StringBuilder("Triage the conversation.\n<label_candidates>\n");
                foreach (var option in request.Labels)
                    task.Append("<label id=\"").Append(AssistContextBuilder.Escape(option.Label.Value)).Append("\">").Append(AssistContextBuilder.Escape(option.Description ?? "")).Append("</label>\n");
                task.Append("</label_candidates>\n<team_candidates>\n");
                foreach (var option in request.Teams)
                    task.Append("<team id=\"").Append(AssistContextBuilder.Escape(option.Team.Value)).Append("\">").Append(AssistContextBuilder.Escape(option.Description ?? "")).Append("</team>\n");
                task.Append("</team_candidates>\n").Append(request.SuggestPriority ? "Suggest a priority." : "Do not suggest a priority; use \"unknown\".");
                return task.ToString();
            }, null, (root, _) => ParseTriage(root, request)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssistResult<AssistSentimentAndIntent>> AnalyzeSentimentAsync(PrincipalRef actor, AssistSentimentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await RunAsync(actor, request.Conversation, new AssistContextRequest(IncludeContact: false), new Call<AssistSentimentAndIntent>(
            AssistFeature.SentimentAndIntent, "sentiment_intent", AssistPrompts.SentimentSchema(request.Intents), 1024, AssistThinkingLevel.Low,
            _ => request.Intents.Count == 0
                ? "Classify the customer's sentiment and intent."
                : "Classify the customer's sentiment and intent. Intents: " + string.Join(", ", request.Intents) + ".",
            null, (root, _) => ParseSentiment(root, request)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssistResult<AssistTranslation>> TranslateAsync(PrincipalRef actor, AssistTranslateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hasMessage = request.Message is not null;
        return await RunAsync(actor, request.Conversation, new AssistContextRequest(IncludeTimeline: hasMessage, IncludeContact: false, BuildSupportData: hasMessage), new Call<AssistTranslation>(
            AssistFeature.Translate, "translation", AssistPrompts.TranslateSchema, 16_000, AssistThinkingLevel.Low,
            _ => "Translate the text into the target locale \"" + request.TargetLocale.Value + "\".",
            context =>
            {
                var source = request.Text;
                if (request.Message is { } id)
                    source = context.Events.Select(view => view.Event.Event.Value switch
                    {
                        MessageEvent message when message.Id == id => message.Body,
                        NoteEvent note when note.Id == id && context.NotesIncluded => note.Body,
                        _ => null,
                    }).LastOrDefault(body => body is not null);
                return source is null ? null : "<text_to_translate>" + AssistContextBuilder.Escape(source) + "</text_to_translate>\n";
            }, (root, _) => ParseTranslation(root, request.TargetLocale), SupportDataIncluded: hasMessage, CachedFormat: AssistPrompts.TranslateFormat), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssistResult<AssistToneRewrite>> RewriteToneAsync(PrincipalRef actor, AssistToneRewriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await RunAsync(actor, request.Conversation, new AssistContextRequest(IncludeTimeline: false, IncludeContact: false, BuildSupportData: false), new Call<AssistToneRewrite>(
            AssistFeature.ToneRewrite, "tone_rewrite", AssistPrompts.ToneSchema, 8192, AssistThinkingLevel.Low,
            _ => "Rewrite the text in a " + request.Tone.ToString().ToLowerInvariant() + " tone.",
            _ => "<text_to_rewrite>" + AssistContextBuilder.Escape(request.Text) + "</text_to_rewrite>\n",
            (root, _) => ParseText(root, AssistDraft.MaximumBodyLength) is { } text ? (new AssistToneRewrite(text, request.Tone), []) : null,
            SupportDataIncluded: false, CachedFormat: AssistPrompts.ToneFormat), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One feature call: schema, output budget, thinking level, the task text, optional extra data (null means the referenced source
    /// is not visible) and the output parser returning the value and cited articles (null when the output is invalid).
    /// </summary>
    private sealed record Call<T>(
        AssistFeature Feature,
        string SchemaName,
        string Schema,
        int MaxOutputTokens,
        AssistThinkingLevel? Thinking,
        Func<AssistConversationContext, string> Task,
        Func<AssistConversationContext, string?>? Extra,
        Func<JsonElement, AssistConversationContext, (T Value, IReadOnlyList<ArticleId> Citations)?> Parse,
        bool SupportDataIncluded = true,
        ChatResponseFormatJson? CachedFormat = null);

    private async Task<AssistResult<T>> RunAsync<T>(PrincipalRef actor, ConversationId conversation, AssistContextRequest contextRequest, Call<T> call, CancellationToken cancellationToken)
    {
        if (actor.Value is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
        var built = await contextBuilder.BuildAsync(actor, conversation, contextRequest, cancellationToken).ConfigureAwait(false);
        var context = built switch
        {
            AssistConversationContext value => value,
            AssistRefused => null,
        };
        if (context is null)
            return (AssistRefused)built.Value!;

        var settings = await AssistSettingsResolver.ResolveAsync(store, options, actor.Scope, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
            return new AssistRefused(AssistRefusal.Disabled, ["The assistant is disabled for this workspace."]);
        var modelId = settings.Models.TryGetValue(call.Feature, out var configured) ? configured : registry.DefaultFor(call.Feature);
        if (!registry.TryGet(modelId, out _))
            return new AssistRefused(AssistRefusal.Invalid, [$"Model '{modelId}' is not registered."]);

        string? extra = null;
        if (call.Extra is not null)
        {
            extra = call.Extra(context);
            if (extra is null)
                return new AssistRefused(AssistRefusal.NotFound, ["The message does not exist or is not visible."]);
        }
        var system = AssistPrompts.System(call.Feature);
        var taskText = call.Task(context);
        var supportData = call.SupportDataIncluded ? context.SupportData : "";
        var user = new StringBuilder(taskText.Length + supportData.Length + (extra?.Length ?? 0) + 64)
            .Append("<task>\n").Append(taskText).Append("\n</task>\n<support_data>\n")
            .Append(supportData)
            .Append(extra)
            .Append("</support_data>")
            .ToString();
        var estimatedInput = (long)Math.Ceiling((system.Length + user.Length + call.Schema.Length) / 3.0);
        var estimate = estimatedInput + call.MaxOutputTokens;
        var estimatedUsage = new AssistUsage(estimatedInput, call.MaxOutputTokens);

        var now = time.GetUtcNow();
        var reservation = new AssistUsageRecord(Guid.CreateVersion7(now), call.Feature, modelId, AccessActor.From(actor), conversation, AssistUsageState.Reserved,
            estimate, AssistUsage.Zero, now, null);
        switch (await store.ReserveAsync(actor.Scope, reservation, settings.DailyTokenLimit, settings.RequestsPerMinute, cancellationToken).ConfigureAwait(false))
        {
            case AssistUsageReserved:
                break;
            case AssistQuotaExceeded exceeded:
                var retryAfter = exceeded.RetryAt > now ? exceeded.RetryAt - now : TimeSpan.Zero;
                return exceeded.Kind switch
                {
                    AssistQuotaKind.DailyTokens => new AssistRefused(AssistRefusal.QuotaExceeded, ["The workspace's daily assistant token quota is exhausted."], retryAfter),
                    AssistQuotaKind.RequestsPerMinute => new AssistRefused(AssistRefusal.RateLimited, ["The workspace's assistant request limit is reached."], retryAfter),
                    _ => throw new InvalidOperationException("Unknown quota kind."),
                };
        }

        // Accounting rule: release the reservation only when the provider provably consumed nothing (never sent, or answered with an
        // error status). Every ambiguous outcome — timeouts, cancellation or network errors after sending, unknown exceptions, or a
        // response without usage — is charged at the reserved estimate.
        IChatClient client;
        try
        {
            client = clients.GetChatClient(new AssistChatRequest(modelId, call.Feature, call.Thinking));
        }
        catch
        {
            await SettleAsync(actor.Scope, reservation.Id, AssistUsageState.Failed, AssistUsage.Zero, estimated: false).ConfigureAwait(false);
            throw;
        }
        ChatResponse response;
        try
        {
            var chatOptions = new ChatOptions
            {
                Instructions = system,
                MaxOutputTokens = call.MaxOutputTokens,
                ResponseFormat = call.CachedFormat ?? ChatResponseFormat.ForJsonSchema(AssistPrompts.Element(call.Schema), call.SchemaName),
            };
            response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, user)], chatOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (AssistProviderException error)
        {
            await SettleAsync(actor.Scope, reservation.Id, AssistUsageState.Failed, AssistUsage.Zero, estimated: error.TokensMayHaveBeenConsumed).ConfigureAwait(false);
            return new AssistProviderFailed(error.Failure, error.Retryable, error.RetryAfter);
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            await SettleAsync(actor.Scope, reservation.Id, AssistUsageState.Failed, AssistUsage.Zero, estimated: true).ConfigureAwait(false);
            return new AssistProviderFailed(error is HttpRequestException ? AssistProviderFailure.Network : AssistProviderFailure.Timeout, true);
        }
        catch
        {
            await SettleAsync(actor.Scope, reservation.Id, AssistUsageState.Failed, AssistUsage.Zero, estimated: true).ConfigureAwait(false);
            throw;
        }

        var reported = response.Usage is { InputTokenCount: not null } or { OutputTokenCount: not null };
        var usage = reported ? AssistUsage.From(response.Usage) : estimatedUsage;
        await SettleAsync(actor.Scope, reservation.Id, AssistUsageState.Completed, reported ? usage : AssistUsage.Zero, estimated: !reported).ConfigureAwait(false);
        if (response.FinishReason == ChatFinishReason.ContentFilter)
            return new AssistRefused(AssistRefusal.ModelRefused, ["The model declined the request."]);
        if (response.FinishReason == ChatFinishReason.Length)
            return new AssistProviderFailed(AssistProviderFailure.OutputTruncated, false);

        var parsed = Parse(response.Text, context, call.Parse);
        if (parsed is null)
            return new AssistProviderFailed(AssistProviderFailure.InvalidResponse, true);

        var recording = await RecordAsync(actor, conversation, call.Feature, modelId, reservation.Id, usage, parsed.Value.Citations, cancellationToken).ConfigureAwait(false);
        return new AssistSucceeded<T>(parsed.Value.Value, usage, modelId, reservation.Id, recording);
    }

    private static (T Value, IReadOnlyList<ArticleId> Citations)? Parse<T>(string? text, AssistConversationContext context,
        Func<JsonElement, AssistConversationContext, (T Value, IReadOnlyList<ArticleId> Citations)?> parse)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256 * 1024)
            return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object ? parse(document.RootElement, context) : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException)
        {
            return null;
        }
    }

    private Task SettleAsync(TicketScope scope, Guid id, AssistUsageState state, AssistUsage usage, bool estimated) =>
        // Settlement must happen even when the caller cancelled, so the reservation does not stay unsettled.
        store.SettleAsync(scope, id, state, usage, time.GetUtcNow(), estimated, CancellationToken.None);

    private async Task<AssistRecording> RecordAsync(PrincipalRef actor, ConversationId conversation, AssistFeature feature, string model, Guid usageId, AssistUsage usage,
        IReadOnlyList<ArticleId> citations, CancellationToken cancellationToken)
    {
        if (!options.RecordTimelineEvents || AssistTimelineKinds.For(feature) is not { } kind)
            return AssistRecording.NotApplicable;
        var payload = AssistJsonCodec.EncodeTimelinePayload(new AssistTimelinePayload(feature, model, usageId, usage, citations));
        var result = await conversations.AppendExtensionAsync(actor, conversation, new ExtensionEvent(kind, AssistTimelineKinds.PayloadVersion, payload), null, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            ConversationApplied => AssistRecording.Recorded,
            ConversationRefused { Reason: ConversationRefusal.Forbidden } => AssistRecording.Forbidden,
            ConversationRefused => AssistRecording.Failed,
            ConversationNotFound => AssistRecording.Failed,
            ConversationConflict => AssistRecording.Failed,
        };
    }

    // ------------------------------------------------------------------ output parsing (model output is untrusted)

    private static (AssistSummary, IReadOnlyList<ArticleId>)? ParseSummary(JsonElement root)
    {
        var summary = ParseText(root, AssistSummary.MaximumSummaryLength, "summary");
        var points = Strings(root, "key_points", AssistSummary.MaximumKeyPoints, AssistSummary.MaximumKeyPointLength);
        var next = OptionalText(root, "next_step", AssistSummary.MaximumKeyPointLength);
        if (summary is null || points is null || next is null)
            return null;
        return (new AssistSummary(summary, points, next.Length == 0 ? null : next), []);
    }

    private static (AssistDraft, IReadOnlyList<ArticleId>)? ParseDraft(JsonElement root, AssistConversationContext context)
    {
        var body = ParseText(root, AssistDraft.MaximumBodyLength, "body");
        var keys = Strings(root, "citations", 50, 16);
        if (body is null || keys is null)
            return null;
        var byKey = context.Snippets.ToDictionary(snippet => snippet.Key, StringComparer.Ordinal);
        var citations = keys.Distinct(StringComparer.Ordinal)
            .Where(byKey.ContainsKey)
            .Select(key => byKey[key].Article)
            .DistinctBy(article => article.Article)
            .Select(article => new AssistCitation(article.Article, article.Portal, article.Locale, article.Title, article.Slug, article.RevisionNumber))
            .ToList();
        return (new AssistDraft(body, citations, context.Snippets.Count > 0), [.. citations.Select(citation => citation.Article)]);
    }

    private static (AssistTriage, IReadOnlyList<ArticleId>)? ParseTriage(JsonElement root, AssistTriageRequest request)
    {
        var allowedLabels = request.Labels.ToDictionary(option => option.Label.Value, option => option.Label, StringComparer.Ordinal);
        var allowedTeams = request.Teams.ToDictionary(option => option.Team.Value, option => option.Team, StringComparer.Ordinal);
        if (root.GetProperty("labels") is not { ValueKind: JsonValueKind.Array } labelArray || labelArray.GetArrayLength() > AssistTriageRequest.MaximumOptions)
            return null;
        var labels = new List<AssistLabelSuggestion>();
        foreach (var item in labelArray.EnumerateArray())
        {
            var confidence = Confidence(item.GetProperty("confidence"));
            if (confidence is null)
                return null;
            if (item.GetProperty("label").GetString() is { } label && allowedLabels.TryGetValue(label, out var id) && labels.All(existing => existing.Label != id))
                labels.Add(new AssistLabelSuggestion(id, confidence.Value));
        }
        var teamElement = root.GetProperty("team");
        var teamConfidence = Confidence(teamElement.GetProperty("confidence"));
        var teamValue = teamElement.GetProperty("team").GetString();
        if (teamConfidence is null || teamValue is null || (teamValue.Length > 0 && !allowedTeams.ContainsKey(teamValue)))
            return null;
        var priorityElement = root.GetProperty("priority");
        var priorityConfidence = Confidence(priorityElement.GetProperty("confidence"));
        var priorityValue = priorityElement.GetProperty("priority").GetString();
        if (priorityConfidence is null || priorityValue is null || !AssistPrompts.Priorities.Contains(priorityValue))
            return null;
        AssistPrioritySuggestion? priority = !request.SuggestPriority || priorityValue == "unknown" ? null : new(priorityValue switch
        {
            "none" => ConversationPriority.None,
            "low" => ConversationPriority.Low,
            "medium" => ConversationPriority.Medium,
            "high" => ConversationPriority.High,
            _ => ConversationPriority.Urgent,
        }, priorityConfidence.Value);
        var rationale = OptionalText(root, "rationale", AssistTriage.MaximumRationaleLength);
        if (rationale is null)
            return null;
        return (new AssistTriage(labels, teamValue.Length == 0 ? null : new AssistTeamSuggestion(allowedTeams[teamValue], teamConfidence.Value), priority,
            rationale.Length == 0 ? null : rationale), []);
    }

    private static (AssistSentimentAndIntent, IReadOnlyList<ArticleId>)? ParseSentiment(JsonElement root, AssistSentimentRequest request)
    {
        var sentiment = root.GetProperty("sentiment").GetString() switch
        {
            "negative" => AssistSentiment.Negative,
            "neutral" => AssistSentiment.Neutral,
            "positive" => AssistSentiment.Positive,
            "mixed" => AssistSentiment.Mixed,
            _ => (AssistSentiment?)null,
        };
        var sentimentConfidence = Confidence(root.GetProperty("sentiment_confidence"));
        var intent = root.GetProperty("intent").GetString();
        var intentConfidence = Confidence(root.GetProperty("intent_confidence"));
        if (sentiment is null || sentimentConfidence is null || intentConfidence is null || intent is null
            || !AssistText.IsToken(intent, AssistSentimentRequest.MaximumIntentLength) || (request.Intents.Count > 0 && !request.Intents.Contains(intent)))
            return null;
        return (new AssistSentimentAndIntent(sentiment.Value, sentimentConfidence.Value, intent, intentConfidence.Value), []);
    }

    private static (AssistTranslation, IReadOnlyList<ArticleId>)? ParseTranslation(JsonElement root, HelpCenterLocale target)
    {
        var text = ParseText(root, 64 * 1024, "text");
        var source = OptionalText(root, "source_locale", HelpCenterLocale.MaximumLength);
        if (text is null || source is null)
            return null;
        return (new AssistTranslation(text, target, HelpCenterLocale.TryParse(source, out var locale) ? locale : null), []);
    }

    private static string? ParseText(JsonElement root, int maximumLength, string property = "text") =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String && element.GetString() is { } value
            && !string.IsNullOrWhiteSpace(value) && AssistText.IsText(value, maximumLength) ? value : null;

    /// <summary>A bounded string that may be empty; null when missing or invalid.</summary>
    private static string? OptionalText(JsonElement root, string property, int maximumLength) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String && element.GetString() is { } value && AssistText.IsText(value, maximumLength)
            ? value.Trim() : null;

    private static IReadOnlyList<string>? Strings(JsonElement root, string property, int maximumCount, int maximumLength)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maximumCount)
            return null;
        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value || !AssistText.IsText(value, maximumLength))
                return null;
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value.Trim());
        }
        return values;
    }

    private static double? Confidence(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && double.IsFinite(value) && value is >= 0 and <= 1 ? value : null;
}

/// <summary>Resolves the effective workspace settings: the stored record or the application defaults.</summary>
internal static class AssistSettingsResolver
{
    public static async Task<AssistSettings> ResolveAsync(IAssistStore store, AssistOptions options, TicketScope scope, CancellationToken cancellationToken) =>
        (await store.GetSettingsAsync(scope, cancellationToken).ConfigureAwait(false))?.Settings ?? options.DefaultSettings();
}
