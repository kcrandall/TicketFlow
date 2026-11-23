using TicketFlow.HelpCenter;
using TicketFlow.Surveys;
using TicketFlow.Core;

namespace TicketFlow.UI.Blazor.Infrastructure;

public static partial class UiActions
{
    private static void RegisterContent(Dictionary<string, UiActionHandler> handlers)
    {
        handlers["helpcenter.article.save"] = SaveArticleAsync;
        handlers["helpcenter.article.publish"] = context => TransitionAsync(context, "Article published", (service, id, revision) => service.PublishArticleAsync(context.Actor, id, revision, context.Aborted));
        handlers["helpcenter.article.unpublish"] = context => TransitionAsync(context, "Article unpublished", (service, id, revision) => service.UnpublishArticleAsync(context.Actor, id, revision, context.Aborted));
        handlers["helpcenter.article.archive"] = context => TransitionAsync(context, "Article archived", (service, id, revision) => service.ArchiveArticleAsync(context.Actor, id, revision, context.Aborted));
        handlers["helpcenter.collection.create"] = context => WithAsync<IHelpCenterService>(context, async service =>
        {
            var name = context.Require("name", "Name");
            var slug = Slug(name);
            var collection = new HelpCollection(new CollectionId(slug + "-" + NewId()[..4]), new PortalId(context.Require("portal", "Portal")),
                [new CollectionTranslation(new HelpCenterLocale(context.Require("locale", "Locale")), name, new HelpCenterSlug(slug))]);
            return UiOutcomes.From(await service.CreateCollectionAsync(context.Actor, collection, context.Aborted), _ => UiOutcome.Success("Collection created"));
        });
        handlers["survey.toggle"] = context => WithAsync<ISurveyService>(context, async service =>
        {
            if (await service.GetSurveyAsync(context.Actor, new SurveyId(context.Require("id", "Survey")), context.Aborted) is not TicketValue<StoredSurvey> current)
                return UiOutcome.NotFound();
            var survey = current.Value.Survey;
            var toggled = new SurveyDefinition(survey.Id, survey.Name, survey.Kind, survey.DefaultLocale, survey.Questions, survey.Policy, survey.Inbox, !survey.Enabled);
            return UiOutcomes.From(await service.UpdateSurveyAsync(context.Actor, toggled, context.RequireRevision(), context.Aborted),
                stored => UiOutcome.Success(stored.Survey.Enabled ? "Survey enabled" : "Survey disabled"));
        });
    }

    internal static string Slug(string text)
    {
        var slug = new string([.. text.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')]);
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        return slug.Length == 0 ? "item" : slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }

    private static async Task<UiActionResult> SaveArticleAsync(UiActionContext context)
    {
        if (context.Session.Service<IHelpCenterService>() is not { } service)
            return UiOutcome.NotFound();
        var segments = context.Session.Path.Split('/');
        var existing = segments is ["help-center", "articles", var id] && id != "new" ? Uri.UnescapeDataString(id) : null;
        var title = context.Require("title", "Title");
        var translation = new ArticleTranslation(new HelpCenterLocale(context.Text("locale") ?? "en"), title, new HelpCenterSlug(context.Text("slug") ?? Slug(title)),
            context.RequireRaw("body", "Body"), context.Text("excerpt"), context.Text("tags")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var article = new Article(new ArticleId(existing ?? Slug(title) + "-" + NewId()[..4]), new PortalId(context.Require("portal", "Portal")), [translation],
            context.Text("collection") is { } collection ? new CollectionId(collection) : null);
        return existing is null
            ? UiOutcomes.From(await service.CreateArticleAsync(context.Actor, article, context.Aborted), stored => UiOutcome.Success("Draft created", $"help-center/articles/{stored.Id.Value}"))
            : UiOutcomes.From(await service.UpdateArticleAsync(context.Actor, article, context.RequireRevision(), context.Aborted), _ => UiOutcome.Success("Draft saved"));
    }

    private static async Task<UiActionResult> TransitionAsync(UiActionContext context, string success,
        Func<IHelpCenterService, ArticleId, long, Task<TicketResult<StoredArticle>>> transition)
    {
        if (context.Session.Service<IHelpCenterService>() is not { } service)
            return UiOutcome.NotFound();
        var id = context.Session.Path.Split('/') is ["help-center", "articles", var value] ? new ArticleId(Uri.UnescapeDataString(value)) : throw new UiInputException("The article is missing.");
        return UiOutcomes.From(await transition(service, id, context.RequireRevision()), _ => UiOutcome.Success(success));
    }
}
