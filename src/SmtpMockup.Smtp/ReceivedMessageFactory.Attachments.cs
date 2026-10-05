using System.Security.Cryptography;
using MimeKit;
using SmtpMockup.Core.Models;

namespace SmtpMockup.Smtp;

internal static partial class ReceivedMessageFactory
{
    /// <summary>
    /// Extrae adjuntos y recursos inline recorriendo <see cref="MimeMessage.BodyParts"/>,
    /// que aplana multipart anidados y así cubre adjuntos sueltos e incrustados.
    /// </summary>
    /// <remarks>
    /// Importante: <c>BodyParts</c> incluye también las partes de cuerpo
    /// (<c>text/plain</c>, <c>text/html</c>). Sin filtrar, un correo de sólo texto
    /// plano se reportaría con un adjunto llamado <c>unnamed</c>. Por eso sólo se toma
    /// como adjunto lo que trae <c>Content-Disposition: attachment</c>, un
    /// <c>filename</c> o un <c>Content-ID</c> (recurso inline) — y además nunca una
    /// parte <c>text/*</c> sin disposición de adjunto.
    /// </remarks>
    private static IReadOnlyList<AttachmentInfo> ReadAttachments(MimeMessage message, int maxInlineBytes)
    {
        var attachments = new List<AttachmentInfo>();

        foreach (var entity in message.BodyParts)
        {
            if (entity is not MimePart part)
            {
                continue;
            }

            var contentId = part.ContentId;
            var isInline = !part.IsAttachment && !string.IsNullOrEmpty(contentId);
            var hasFileName = !string.IsNullOrWhiteSpace(part.FileName);

            // Una parte sólo es adjunto si se declara como tal (Content-Disposition,
            // filename) o si es un recurso inline con Content-ID. Sin esto, las partes
            // text/plain y text/html del cuerpo se contarían como adjuntos "unnamed".
            var looksLikeAttachment = part.IsAttachment || hasFileName || !string.IsNullOrEmpty(contentId);

            if (!looksLikeAttachment)
            {
                continue;
            }

            // Una parte sin contenido (boundary vacío, parte truncada) no aporta nada.
            if (part.Content is not { } content)
            {
                continue;
            }

            using var buffer = new MemoryStream();
            content.DecodeTo(buffer);

            var bytes = buffer.ToArray();

            // 0 significa "sin tope", igual que MaxRawMimeBytes. Sin esta comprobación, un 0
            // haría que `length > 0` fuese cierto para todo adjunto no vacío y se descartaría
            // el contenido de todos ellos: un tope puesto para desactivarlo, eliminándolos.
            var omitted = maxInlineBytes > 0 && bytes.LongLength > maxInlineBytes;

            attachments.Add(new AttachmentInfo(
                FileName: SanitizeFileName(part.FileName),
                ContentType: part.ContentType.MimeType,
                SizeBytes: bytes.LongLength,
                ContentId: string.IsNullOrEmpty(contentId) ? null : contentId,
                IsInline: isInline,
                ChecksumSha256: Sha256Hex(bytes),
                Omitted: omitted,
                ContentBase64: omitted ? null : Convert.ToBase64String(bytes)));
        }

        return attachments;
    }

    /// <summary>
    /// Sanea el nombre del adjunto: se queda sólo con el nombre de archivo, para que un
    /// <c>../../etc/passwd</c> no llegue a la descarga (SPEC §10.3).
    /// </summary>
    private static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return UnnamedAttachment;
        }

        var sanitized = Path.GetFileName(fileName.Replace('\\', '/'));

        return string.IsNullOrWhiteSpace(sanitized) ? UnnamedAttachment : sanitized;
    }

    /// <summary>SHA-256 en hexadecimal minúsculas del contenido decodificado.</summary>
    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
