using Bunit;
using Microsoft.Extensions.Options;
using MudBlazor;
using SmtpMockup.Core.Options;
using SmtpMockup.Web.Components;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// El cuerpo HTML de un correo es contenido no confiable: nunca puede inyectarse en el DOM de la
/// aplicación (SPEC §9.4). Estos tests vigilan exactamente eso.
/// </summary>
public sealed class HtmlPreviewTests : WebTestContext
{
    private const string Hostile = "<p>hola</p><script>alert('xss')</script>";

    [Fact]
    public void The_html_body_is_rendered_inside_a_sandboxed_iframe()
    {
        var component = Render<HtmlPreview>(parameters => parameters.Add(p => p.Html, Hostile));

        var frame = component.Find("iframe");

        Assert.NotNull(frame.GetAttribute("sandbox"));
        Assert.Equal(Hostile, frame.GetAttribute("srcdoc"));
        Assert.Equal("no-referrer", frame.GetAttribute("referrerpolicy"));
    }

    [Fact]
    public void The_sandbox_is_empty_so_the_frame_cannot_run_scripts_or_navigate()
    {
        // sandbox="" (sin allow-scripts) es lo que impide la ejecución; con "allow-scripts"
        // el correo podría llamar al circuito de Blazor.
        var component = Render<HtmlPreview>(parameters => parameters.Add(p => p.Html, Hostile));

        Assert.Equal(string.Empty, component.Find("iframe").GetAttribute("sandbox"));
    }

    [Fact]
    public void The_html_never_reaches_the_body_of_the_page()
    {
        var component = Render<HtmlPreview>(parameters => parameters.Add(p => p.Html, Hostile));

        // La única forma de que el script apareciera en el DOM principal sería inyectado como
        // marcado. Dentro del atributo srcdoc es sólo texto, así que no existe tal elemento.
        Assert.Empty(component.FindAll("script"));
        Assert.Empty(component.FindAll("p"));
    }

    [Fact]
    public void A_message_without_html_shows_an_explanatory_message()
    {
        var component = Render<HtmlPreview>(parameters => parameters.Add(p => p.Html, null));

        Assert.Empty(component.FindAll("iframe"));
        Assert.Contains("no trae cuerpo HTML", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_over_the_configured_limit_is_shown_as_text_instead_of_in_the_frame()
    {
        WebOptions.MaxHtmlPreviewBytes = 64;

        var component = Render<HtmlPreview>(
            parameters => parameters.Add(p => p.Html, new string('a', 65)));

        Assert.Empty(component.FindAll("iframe"));
        Assert.NotNull(component.Find("[data-testid='html-plain']"));
        Assert.NotNull(component.Find("[data-testid='html-too-large']"));
    }
}
