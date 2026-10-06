using Bunit;
using SmtpMockup.Web.Components.Layout;
using SmtpMockup.Web.Components.Pages;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// Compromisos de accesibilidad y usabilidad de la UI (adopción del estilo de
/// <c>docs/UI-Prototype</c>): landmarks del layout, jerarquía de encabezados, grupos etiquetados y
/// regiones vivas. Son los puntos que un cambio de markup puede romper sin que ningún test de
/// funcionalidad se entere.
/// </summary>
public sealed class AccessibilityTests : WebTestContext
{
    [Fact]
    public void The_layout_has_a_skip_link_a_main_landmark_and_labelled_navigation()
    {
        var markup = Render<MainLayout>().Markup;

        // El primer tabulador salta la navegación; el main es su destino y el nav tiene nombre.
        Assert.Contains("class=\"skip-link\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"main-content\"", markup, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Navegación principal\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_layout_navigation_reaches_the_four_destinations()
    {
        var markup = Render<MainLayout>().Markup;

        Assert.Contains("href=\"\"", markup, StringComparison.Ordinal);
        Assert.Contains("href=\"stats\"", markup, StringComparison.Ordinal);
        Assert.Contains("href=\"settings\"", markup, StringComparison.Ordinal);
        Assert.Contains("href=\"about\"", markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("stats")]
    [InlineData("settings")]
    [InlineData("about")]
    [InlineData("detail")]
    public void Each_page_shows_exactly_one_h1(string page)
    {
        var markup = page switch
        {
            "stats" => Render<StatsPage>().Markup,
            "settings" => Render<SettingsPage>().Markup,
            "about" => Render<AboutPage>().Markup,
            "detail" => RenderDetail().Markup,
            _ => Render<MessageListPage>().Markup,
        };

        // Un único h1 por página: el título. Con más de uno (o con ninguno) el FocusOnNavigate de
        // Routes.razor y los lectores de pantalla pierden el punto de referencia del documento.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(markup, "<h1[ >]"));
    }

    [Fact]
    public void The_list_filters_are_a_group_with_a_name()
    {
        var component = Render<MessageListPage>();

        Assert.Equal("Filtros", component.Find("fieldset.filters legend").TextContent.Trim());
    }

    [Fact]
    public void The_result_summary_announces_changes_to_screen_readers()
    {
        var component = Render<MessageListPage>();

        Assert.Equal(
            "polite",
            component.Find("[data-testid='result-summary']").GetAttribute("aria-live"));
    }

    [Fact]
    public void The_envelope_labels_are_row_headers_not_plain_cells()
    {
        var component = RenderDetail();

        Assert.NotEmpty(component.FindAll("[data-testid='envelope'] th[scope='row']"));
    }

    /// <summary>Renderiza el detalle de <see cref="TestData.Id"/> con el mensaje ya en el store.</summary>
    private IRenderedComponent<MessageDetailPage> RenderDetail()
    {
        Store.Seed(TestData.Message());
        return Render<MessageDetailPage>(parameters => parameters.Add(page => page.Id, TestData.Id));
    }
}
