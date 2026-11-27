using System.Collections.Frozen;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using TicketFlow.Core;

namespace TicketFlow.UI.Blazor.Infrastructure;

/// <summary>
/// The result of a form action. Successful outcomes redirect (post/redirect/get) and show <see cref="Outcome"/> once; refused outcomes
/// and actions that return <see cref="PageState"/> (revealed values, previews, dry runs) re-render the page directly with
/// <c>Cache-Control: no-store</c>, keeping the submitted values in the form.
/// </summary>
public sealed record UiActionResult(UiOutcome Outcome, object? PageState = null)
{
    public static implicit operator UiActionResult(UiOutcome outcome) => new(outcome);
}

/// <summary>A posted form for one action.</summary>
public sealed class UiActionContext(TicketFlowUISession session, IFormCollection form)
{
    public TicketFlowUISession Session { get; } = session;
    public IFormCollection Form { get; } = form;
    public PrincipalRef Actor => Session.Principal;
    public TicketScope Scope => Session.Scope;
    public CancellationToken Aborted => Session.Aborted;

    public string? Text(string name) => Form.TryGetValue(name, out var values) && values.Count > 0 && values[0] is { } value && value.Trim().Length > 0 ? value.Trim() : null;

    /// <summary>The raw (untrimmed) value, for bodies and JSON definitions.</summary>
    public string? Raw(string name) => Form.TryGetValue(name, out var values) && values.Count > 0 && values[0] is { Length: > 0 } value ? value : null;

    public IReadOnlyList<string> All(string name) => Form.TryGetValue(name, out var values) ? [.. values.OfType<string>().Where(value => value.Length > 0)] : [];

    public bool Flag(string name) => Text(name) is "on" or "true" or "1";

    public int? Int(string name) => int.TryParse(Text(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>The expected revision posted in the hidden <c>_revision</c> field.</summary>
    public long? Revision => long.TryParse(Text("_revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public long RequireRevision() => Revision ?? throw new UiInputException("The form is missing its revision; reload the page and try again.");

    public string Require(string name, string label) => Text(name) ?? throw new UiInputException($"{label} is required.");

    public string RequireRaw(string name, string label) => Raw(name) ?? throw new UiInputException($"{label} is required.");
}

/// <summary>Invalid form input detected by the UI before calling a service; shown as a validation outcome.</summary>
public sealed class UiInputException(string message) : Exception(message);

public delegate Task<UiActionResult> UiActionHandler(UiActionContext context);

/// <summary>The registry of form actions, keyed by the posted <c>_action</c> value.</summary>
public static partial class UiActions
{
    private static readonly FrozenDictionary<string, UiActionHandler> Handlers = Build();

    public static UiActionHandler? Find(string? name) => name is not null && Handlers.TryGetValue(name, out var handler) ? handler : null;

    public static IReadOnlyCollection<string> Names => Handlers.Keys;

    private static FrozenDictionary<string, UiActionHandler> Build()
    {
        var handlers = new Dictionary<string, UiActionHandler>(StringComparer.Ordinal);
        RegisterConversations(handlers);
        RegisterContacts(handlers);
        RegisterWorkspace(handlers);
        RegisterOperations(handlers);
        RegisterContent(handlers);
        RegisterAccess(handlers);
        return handlers.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Runs <paramref name="handler"/>, mapping input and domain validation exceptions to an invalid outcome (messages only when TicketFlow produced them).</summary>
    public static async Task<UiActionResult> RunAsync(UiActionHandler handler, UiActionContext context)
    {
        try
        {
            return await handler(context);
        }
        catch (UiInputException error)
        {
            return UiOutcome.Invalid(error.Message);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or System.Text.Json.JsonException or OverflowException)
        {
            // Domain constructors and codecs validate input; their messages describe the rule, not the submitted value.
            return UiOutcome.Invalid(error switch
            {
                System.Text.Json.JsonException => "The definition is not valid JSON for this record type.",
                OverflowException => "A numeric value is out of range.",
                _ => FirstLine(error.Message),
            });
        }
    }

    private static string FirstLine(string message)
    {
        var newline = message.IndexOf('\n');
        var line = newline < 0 ? message : message[..newline];
        var parameter = line.IndexOf(" (Parameter", StringComparison.Ordinal);
        return parameter > 0 ? line[..parameter] : line;
    }
}
