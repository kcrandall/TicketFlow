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
        var existing = segments is ["help-center", "articles", var id] && !string.Equals(id, "new", global::System.StringComparison.Ordinal) ? Uri.UnescapeDataString(id) : null;
        var title = context.Require("title", "Title");
        var locale = new HelpCenterLocale(context.Text("locale") ?? "en");
        var translation = new ArticleTranslation(locale, title, new HelpCenterSlug(context.Text("slug") ?? Slug(title)),
            context.RequireRaw("body", "Body"), context.Text("excerpt"), context.Text("tags")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var collection = context.Text("collection") is { } collectionId ? new CollectionId(collectionId) : (CollectionId?)null;
        if (existing is null)
        {
            var created = new Article(new ArticleId(Slug(title) + "-" + NewId()[..4]), new PortalId(context.Require("portal", "Portal")), [translation], collection);
            return UiOutcomes.From(await service.CreateArticleAsync(context.Actor, created, context.Aborted),
                stored => UiOutcome.Success("Draft created", $"help-center/articles/{stored.Id.Value}"));
        }
        var articleId = new ArticleId(existing);
        var current = await service.GetArticleAsync(context.Actor, articleId, context.Aborted);
        if (current is not TicketValue<StoredArticle> found)
            return UiOutcomes.From(current, _ => UiOutcome.Success("Draft saved"));
        var existingArticle = found.Value.Record.Article;
        var translations = new List<ArticleTranslation>(existingArticle.Translations.Count + 1);
        var replaced = false;
        foreach (var item in existingArticle.Translations)
        {
            if (item.Locale == locale)
            {
                translations.Add(translation);
                replaced = true;
            }
            else
                translations.Add(item);
        }
        if (!replaced)
            translations.Add(translation);
        var updated = new Article(articleId, existingArticle.Portal, translations, collection, existingArticle.Position);
        return UiOutcomes.From(await service.UpdateArticleAsync(context.Actor, updated, context.RequireRevision(), context.Aborted), _ => UiOutcome.Success("Draft saved"));
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
