using Bunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.UI.Blazor.Components;
using TicketFlow.UI.Blazor.Infrastructure;
using TicketFlow.UI.Blazor.Theming;

namespace TicketFlow.UI.Blazor.Tests;

[TestClass]
public sealed class ThemeTests
{
    [TestMethod]
    public void DocumentDefaultsToZincDark()
    {
        using var context = UiTestSupport.Context();
        var session = UiTestSupport.Session(context, []);

        var document = context.Render<TicketFlowDocument>(parameters => parameters.Add(p => p.Session, session));

        var html = document.Find("html");
        Assert.AreEqual("zinc", html.GetAttribute("data-tf-theme"));
        Assert.AreEqual("dark", html.GetAttribute("class"));
        Assert.AreEqual("dark", html.GetAttribute("data-tf-mode"));
    }

    [TestMethod]
    public void ThemeCookieDrivesServerRenderedThemeWithoutScript()
    {
        using var context = UiTestSupport.Context();
        var session = UiTestSupport.Session(context, [], cookie: "tf-theme=rose.light");

        var document = context.Render<TicketFlowDocument>(parameters => parameters.Add(p => p.Session, session));

        var html = document.Find("html");
        Assert.AreEqual("rose", html.GetAttribute("data-tf-theme"));
        Assert.IsFalse(html.ClassList.Contains("dark"), "Light mode renders without the dark class on first paint.");
        Assert.IsFalse(document.Markup.Contains("matchMedia", StringComparison.Ordinal), "Only the system mode needs the pre-paint script.");
    }

    [TestMethod]
    public void SystemModeEmitsPrePaintScript()
    {
        using var context = UiTestSupport.Context();
        var session = UiTestSupport.Session(context, [], cookie: "tf-theme=slate.system");

        var document = context.Render<TicketFlowDocument>(parameters => parameters.Add(p => p.Session, session));

        Assert.AreEqual("slate", document.Find("html").GetAttribute("data-tf-theme"));
        StringAssert.Contains(document.Markup, "prefers-color-scheme: dark");
    }

    [TestMethod]
    public void UnknownOrMalformedCookiesFallBackToTheDefault()
    {
        var options = new TicketFlowBlazorUIOptions();
        foreach (var value in new[] { "nope.dark", "zinc.sepia", "zinc", "", "x".PadRight(200, 'x') + ".dark" })
            Assert.IsNull(TicketFlowThemePreference.Parse(value, options.Themes), value);
        Assert.AreEqual(new TicketFlowThemePreference("blue", TicketFlowThemeMode.Light), TicketFlowThemePreference.Parse("blue.light", options.Themes));
    }

    [TestMethod]
    public void HostThemesRenderValidatedTokenOverrides()
    {
        using var context = UiTestSupport.Context(services => services.Configure<TicketFlowBlazorUIOptions>(options =>
        {
            var acme = new TicketFlowTheme("acme", "Acme", "#7c3aed");
            acme.Dark["primary"] = "oklch(0.62 0.24 293)";
            options.Themes.Add(acme);
            options.DefaultTheme = new TicketFlowThemePreference("acme", TicketFlowThemeMode.Dark);
        }));
        var session = UiTestSupport.Session(context, []);

        var document = context.Render<TicketFlowDocument>(parameters => parameters.Add(p => p.Session, session));

        Assert.AreEqual("acme", document.Find("html").GetAttribute("data-tf-theme"));
        StringAssert.Contains(document.Find("style").TextContent, "--primary:oklch(0.62 0.24 293)");
    }

    [TestMethod]
    public void ThemeTokensRejectCssInjection()
    {
        var theme = new TicketFlowTheme("evil", "Evil", "red");
        theme.Dark["primary"] = "red;}</style><script>";
        var options = new TicketFlowBlazorUIOptions();
        options.Themes.Add(theme);
        Assert.ThrowsExactly<InvalidOperationException>(options.Validate);
    }

    [TestMethod]
    public void ThemeCookieIsWrittenForSsr()
    {
        var http = new DefaultHttpContext();
        UiTheme.Write(http, new TicketFlowBlazorUIOptions(), new TicketFlowThemePreference("violet", TicketFlowThemeMode.Light));
        StringAssert.Contains(http.Response.Headers.SetCookie.ToString(), "tf-theme=violet.light");
        StringAssert.Contains(http.Response.Headers.SetCookie.ToString(), "samesite=lax");
    }

    [TestMethod]
    public void ThemeSwitchFormsDisableEnhancedNavigationForFullDocumentUpdate()
    {
        using var context = UiTestSupport.Context();
        var session = UiTestSupport.Session(context, []);

        var shell = context.Render<TicketFlowShell>(parameters => parameters.AddCascadingValue(session));
        var headerThemeForm = shell.Find("header form");
        Assert.IsFalse(headerThemeForm.HasAttribute("data-enhance"), "Theme toggle form must perform a full reload so <html> theme attributes update.");

        var settingsPage = context.Render<TicketFlow.UI.Blazor.Pages.ThemeSettingsPage>(parameters => parameters.AddCascadingValue(session));
        var settingsThemeForm = settingsPage.Find("form");
        Assert.IsFalse(settingsThemeForm.HasAttribute("data-enhance"), "Theme settings form must perform a full reload so <html> theme attributes update.");
    }
}
