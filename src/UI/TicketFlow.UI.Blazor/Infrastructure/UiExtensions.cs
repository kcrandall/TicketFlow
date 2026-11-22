using Microsoft.AspNetCore.Http;

namespace TicketFlow.UI.Blazor.Infrastructure;

internal static class UiExtensions
{
    public static bool HasValue(this IFormCollection? form) => form is not null;
}
