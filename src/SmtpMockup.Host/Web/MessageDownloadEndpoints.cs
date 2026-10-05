using Microsoft.Extensions.Options;
using SmtpMockup.Core.Models;
using SmtpMockup.Core.Options;
using SmtpMockup.Core.Storage;
using SmtpMockup.Web.Services;

namespace SmtpMockup.Host.Web;

/// <summary>
/// Descargas de adjuntos y del MIME crudo.
/// </summary>
/// <remarks>
/// <para>
/// No es una "API de mensajes": la UI lee del <see cref="IMessageStore"/> por el circuito y no
/// expone ningún endpoint de consulta. Lo único que viaja por HTTP son los <em>bytes</em> de un
/// archivo, porque mandar varios megabytes por SignalR serializado como base64 sería peor para
/// el circuito que una petición HTTP normal (SPEC §9.5).
/// </para>
/// <para>
/// Cada descarga vuelve a leer del store y a sanear el nombre en
/// <see cref="AttachmentDownloader"/>: la URL que ve el navegador nunca decide ni el nombre ni
/// el contenido (SPEC §10.3).
/// </para>
/// </remarks>
internal static class MessageDownloadEndpoints
{
    /// <summary>Mapea las rutas de descarga. Sólo se llama si <c>Web:Enabled</c>.</summary>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/download/message/{id}/attachment/{index:int}", DownloadAttachmentAsync)
            .WithName("DownloadAttachment");

        app.MapGet("/download/message/{id}/raw", DownloadRawMimeAsync)
            .WithName("DownloadRawMime");
    }

    private static async Task<IResult> DownloadAttachmentAsync(
        string id,
        int index,
        IMessageStore store,
        HttpContext context)
    {
        var payload = await BuildPayloadAsync(
                store,
                id,
                message => index >= 0 && index < message.Attachments.Count
                    ? AttachmentDownloader.TryCreate(message.Attachments[index])
                    : null)
            .ConfigureAwait(false);

        return payload is null ? NotFound(id, "attachment") : File(payload);
    }

    private static async Task<IResult> DownloadRawMimeAsync(
        string id,
        IMessageStore store,
        IOptions<WebOptions> webOptions,
        HttpContext context)
    {
        var payload = await BuildPayloadAsync(
                store,
                id,
                message => AttachmentDownloader.TryCreateRawMime(message, webOptions.Value.ShowRawMimeDownload))
            .ConfigureAwait(false);

        return payload is null ? NotFound(id, "MIME original") : File(payload);
    }

    /// <summary>
    /// Carga el mensaje y produce la descarga. Un id mal formado se responde 404 y no 500: es
    /// una URL manipulada, no un fallo del servidor.
    /// </summary>
    private static async Task<DownloadPayload?> BuildPayloadAsync(
        IMessageStore store,
        string id,
        Func<ReceivedMessage, DownloadPayload?> select)
    {
        if (!MessageId.IsValid(id))
        {
            return null;
        }

        try
        {
            var message = await store.GetAsync(id).ConfigureAwait(false);
            return message is null ? null : select(message);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Respuesta de descarga: nombre de archivo, tipo MIME y bytes exactos. Se usa el
    /// <c>Results.File</c> del framework en vez de armar cabeceras a mano, para que el nombre
    ///pase la codificación correcta (SPEC §9.5).
    /// </summary>
    private static IResult File(DownloadPayload payload)
        => Results.File(payload.Content, payload.ContentType, payload.FileName);

    private static IResult NotFound(string id, string what)
        => Results.NotFound($"No hay un {what} para el mensaje '{id}'.");
}