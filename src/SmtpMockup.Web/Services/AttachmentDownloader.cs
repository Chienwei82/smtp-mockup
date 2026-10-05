using System.Text;
using SmtpMockup.Core.Models;

namespace SmtpMockup.Web.Services;

/// <summary>
/// Los bytes a enviar al navegador en una descarga, con el nombre y el tipo ya saneados
/// (SPEC §9.5 y §10.3).
/// </summary>
/// <param name="FileName">Nombre de archivo sin separadores de ruta.</param>
/// <param name="ContentType">Tipo MIME declarado en el adjunto.</param>
/// <param name="Content">Contenido exacto a servir.</param>
public sealed record DownloadPayload(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Prepara las descargas de adjuntos y del MIME crudo. Vive fuera del componente para que sus
/// dos reglas de seguridad — decodificar base64 y sanear el nombre — se prueben sin bUnit.
/// </summary>
public static class AttachmentDownloader
{
    /// <summary>Nombre que se usa cuando el cliente no envió <c>filename</c>.</summary>
    public const string FallbackFileName = "unnamed";

    /// <summary>Tipo MIME con el que se sirve el MIME crudo.</summary>
    public const string RawMimeContentType = "message/rfc822";

    /// <summary>
    /// Prepara la descarga de un adjunto, o devuelve <see langword="null"/> si no se puede
    /// servir: adjunto omitido por tamaño (<c>omitted: true</c>) o sin <c>contentBase64</c>.
    /// </summary>
    public static DownloadPayload? TryCreate(AttachmentInfo attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        if (attachment.Omitted || attachment.ContentBase64 is not { Length: > 0 } base64)
        {
            return null;
        }

        byte[] content;
        try
        {
            content = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            // Un base64 corrupto es un dato almacenado inválido, no un fallo de la UI: se
            // devuelve null y el componente muestra el adjunto como no descargable.
            return null;
        }

        return new DownloadPayload(
            SanitizeFileName(attachment.FileName),
            FallbackContentType(attachment.ContentType),
            content);
    }

    /// <summary>
    /// Prepara la descarga del MIME crudo (<c>.eml</c>) del mensaje. Devuelve
    /// <see langword="null"/> si no se conserva (<c>Storage:KeepRawMime=false</c>), si quedó
    /// truncado o si la opción de UI <c>Web:ShowRawMimeDownload</c> está apagada.
    /// </summary>
    /// <param name="message">Mensaje del que sacar el MIME crudo.</param>
    /// <param name="includeInUi">Valor de <c>Web:ShowRawMimeDownload</c>.</param>
    public static DownloadPayload? TryCreateRawMime(ReceivedMessage message, bool includeInUi)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!includeInUi || message.Raw is not { Truncated: false } raw || raw.ContentBase64 is not { Length: > 0 } base64)
        {
            return null;
        }

        try
        {
            return new DownloadPayload($"{SanitizeFileName(message.Id)}.eml", RawMimeContentType, Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sanea un nombre de archivo para usarlo en la cabecera <c>Content-Disposition</c>.
    /// Un adjunto con <c>../../evil.exe</c> no puede cambiar el destino de la descarga ni
    /// escapar del directorio del navegador (SPEC §10.3).
    /// </summary>
    public static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FallbackFileName;
        }

        // El separador '\' sólo existe en Windows, así que Path.GetFileName no lo quita al
        // ejecutar en Linux y "..\\..\\evil.dll" llegaría entero al navegador. Se corta por
        // el último separador de cualquiera de los dos tipos antes de pasar por GetFileName.
        var normalized = fileName.Trim();
        var lastSeparator = normalized.LastIndexOfAny(['/', '\\']);
        var trimmed = Path.GetFileName(lastSeparator >= 0 ? normalized[(lastSeparator + 1)..] : normalized);
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(trimmed.Where(character => !invalid.Contains(character) && !char.IsControl(character)).ToArray())
            .Trim()
            .Trim('.');

        return cleaned.Length == 0 ? FallbackFileName : cleaned;
    }

    /// <summary>
    /// Tipo MIME del adjunto, o <c>application/octet-stream</c> si el cliente no envió uno
    /// reconocible: un tipo vacío haría que el navegador mostrara el texto en vez de descargar.
    /// </summary>
    private static string FallbackContentType(string? contentType)
        => string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();

    /// <summary>
    /// Codifica texto para un <c>&lt;pre&gt;</c>. Existe sólo para que los tests puedan
    /// comprobar los bytes exactos que se descargan.
    /// </summary>
    internal static byte[] ToBytes(string content) => Encoding.UTF8.GetBytes(content);
}