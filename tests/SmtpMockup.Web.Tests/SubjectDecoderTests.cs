using SmtpMockup.Web.Services;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// El asunto se guarda crudo en el JSON (SPEC §7) y se decodifica sólo al mostrarlo. Estos
/// tests fijan ese comportamiento: nunca se pierde información y nunca se rompe la vista.
/// </summary>
public sealed class SubjectDecoderTests
{
    [Fact]
    public void Decode_returns_the_text_untouched_when_it_is_not_an_encoded_word()
    {
        var subject = "Re: prueba normal";

        Assert.Equal(subject, SubjectDecoder.Decode(subject));
        Assert.False(SubjectDecoder.HasEncodedWords(subject));
    }

    [Fact]
    public void Decode_reads_a_base64_encoded_word()
    {
        // "Prueba áéíóú" en UTF-8 y base64.
        var subject = "=?UTF-8?B?UHJ1ZWJhIMOhw6nDrcOzw7o=?=";

        Assert.Equal("Prueba áéíóú", SubjectDecoder.Decode(subject));
    }

    [Fact]
    public void Decode_reads_a_quoted_printable_encoded_word_with_underscores_for_spaces()
    {
        var subject = "=?ISO-8859-1?Q?Reunión_a_las_9?=";

        Assert.Equal("Reunión a las 9", SubjectDecoder.Decode(subject));
    }

    [Fact]
    public void Decode_keeps_the_original_when_the_encoded_word_is_corrupt()
    {
        var subject = "=?UTF-8?B?no-es-base64-valido?=";

        Assert.Equal(subject, SubjectDecoder.Decode(subject));
    }

    [Fact]
    public void ForDisplay_uses_a_placeholder_when_the_message_has_no_subject()
    {
        Assert.Equal(SubjectDecoder.EmptySubject, SubjectDecoder.ForDisplay(null));
        Assert.Equal(SubjectDecoder.EmptySubject, SubjectDecoder.ForDisplay("   "));
    }

    [Fact]
    public void ForDisplay_decodes_an_encoded_word()
        => Assert.Equal("Prueba áéíóú", SubjectDecoder.ForDisplay("=?UTF-8?B?UHJ1ZWJhIMOhw6nDrcOzw7o=?="));
}
