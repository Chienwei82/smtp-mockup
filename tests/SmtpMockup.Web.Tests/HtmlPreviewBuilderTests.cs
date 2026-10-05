using System.Text;
using SmtpMockup.Web.Services;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// Reglas de la previsualización del cuerpo HTML (SPEC §9.4): dentro del límite va al
/// <c>iframe sandbox</c>, por encima se muestra como texto y nunca se lanza.
/// </summary>
public sealed class HtmlPreviewBuilderTests
{
    [Fact]
    public void Build_returns_an_empty_preview_for_a_message_without_html()
    {
        var preview = HtmlPreviewBuilder.Build(null, 1024);

        Assert.Equal(string.Empty, preview.Content);
        Assert.False(preview.TooLarge);
    }

    [Fact]
    public void Build_allows_html_under_the_limit()
    {
        var preview = HtmlPreviewBuilder.Build("<p>hola</p>", 1024);

        Assert.False(preview.TooLarge);
        Assert.Equal("<p>hola</p>", preview.Content);
        Assert.Equal(Encoding.UTF8.GetByteCount("<p>hola</p>"), preview.ByteCount);
    }

    [Fact]
    public void Build_marks_html_over_the_limit_as_too_large()
    {
        var html = new string('x', 2_048);

        var preview = HtmlPreviewBuilder.Build(html, 1_024);

        Assert.True(preview.TooLarge);
        Assert.Equal(2_048, preview.ByteCount);
    }

    [Fact]
    public void Build_measures_utf8_and_not_the_number_of_characters()
    {
        // Cuatro emojis ocupan 16 bytes: con un límite de 8 hay que marcarlo como grande aunque
        // sean sólo cuatro caracteres.
        const string html = "😀😁😂😃";

        Assert.True(HtmlPreviewBuilder.Build(html, 8).TooLarge);
        Assert.False(HtmlPreviewBuilder.Build(html, 16).TooLarge);
    }
}
