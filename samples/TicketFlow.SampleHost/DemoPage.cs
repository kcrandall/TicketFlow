using System.Net;
using System.Text.Json;

namespace TicketFlow.SampleHost;

/// <summary>The widget demo page, mounted against this host (widget bundle served from <c>/widget</c>).</summary>
internal static class DemoPage
{
    public static string Render(HttpContext http, SampleSettings settings)
    {
        var origin = $"{http.Request.Scheme}://{http.Request.Host}";
        var config = JsonSerializer.Serialize(new
        {
            hubUrl = origin + "/ticketflow/realtime/visitor",
            uploadUrl = origin + "/ticketflow/livechat/attachments",
            widgetKey = SampleSeeder.WidgetKey,
            title = "Acme support",
        });
        var portal = WebUtility.HtmlEncode($"{origin}/ticketflow/public/workspaces/{settings.Workspace.Value}/portals/{SampleSeeder.PortalSlug}/en/articles");
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>TicketFlow sample</title>
              <style>body { font-family: system-ui, sans-serif; margin: 2rem; max-width: 48rem; } #ticketflow-chat { --tf-accent: #8250df; --tf-accent-text: #fff; }</style>
            </head>
            <body>
              <h1>Acme Shop (TicketFlow sample)</h1>
              <p>The chat widget below talks to this host's visitor hub. Agents use the REST API (<code>/ticketflow/openapi/ticketflow.json</code>)
              with the development header <code>X-Dev-User: agent1</code>. Public help center: <a href="{{portal}}">{{portal}}</a>.</p>
              <div id="ticketflow-chat"></div>
              <script src="/widget/ticketflow-chat.js"></script>
              <script>
                const config = {{config}};
                if (window.TicketFlowChat) TicketFlowChat.mount({ container: document.getElementById("ticketflow-chat"), ...config });
                else document.getElementById("ticketflow-chat").textContent = "Build the widget (cd clients/widget && npm ci && npm run build) to see it here.";
              </script>
            </body>
            </html>
            """;
    }
}
