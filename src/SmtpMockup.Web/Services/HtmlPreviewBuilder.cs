using System.Text;

namespace SmtpMockup.Web.Services;

/// <summary>
/// El HTML de un cuerpo, ya sea previsualizable en el <c>iframe sandbox</c> o demasiado grande
/// para renderizarlo.
/// </summary>
/// <param name="Content">El HTML tal cual, para el atributo <c>srcdoc</c>.</param>
/// <param name="ByteCount">Tamaño en UTF-8 del HTML.</param>
/// <param name="TooLarge">
/// <see langword="true"/> si supera el tope: el componente lo muestra entonces como texto
/// escapado en un <c>&lt;pre&gt;</c> en vez de cargarlo en el iframe.
/// </param>
public sealed record HtmlPreviewResult(string Content, int ByteCount, bool TooLarge)
{
    /// <summary>Una vista vacía: el mensaje no trae cuerpo HTML.</summary>
    public static HtmlPreviewResult Empty { get; } = new(string.Empty, 0, false);
}

/// <summary>
/// Decide si el HTML de un correo puede renderizarse en el iframe o debe mostrarse como texto
/// (SPEC §9.4). El límite no es decorativo: un correo de 20 MB de HTML sobrevive a la escritura
/// del JSON y hundiría el circuito de Blazor al pedirlo por <c>srcdoc</c>.
/// </summary>
public static class HtmlPreviewBuilder
{
    /// <summary>
    /// Mide el HTML y decide cómo mostrarlo. Nunca lanza: un cuerpo ausente es una vista vacía.
    /// </summary>
    /// <param name="html">Cuerpo HTML del mensaje; puede ser <see langword="null"/>.</param>
    /// <param name="maxBytes">Tope configurable en <c>Web:MaxHtmlPreviewBytes</c>.</param>
    public static HtmlPreviewResult Build(string? html, int maxBytes)
    {
        if (string.IsNullOrEmpty(html))
        {
            return HtmlPreviewResult.Empty;
        }

        // Se mide en UTF-8 porque es lo que viaja por el circuito y lo que cuenta el iframe.
        var byteCount = Encoding.UTF8.GetByteCount(html);

        return new HtmlPreviewResult(html, byteCount, TooLarge: byteCount > maxBytes);
    }
}