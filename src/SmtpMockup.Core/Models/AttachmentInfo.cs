namespace SmtpMockup.Core.Models;

/// <summary>
/// Un adjunto (o recurso inline) con su contenido en base64 (SPEC §7 <c>attachments</c>).
/// </summary>
/// <param name="FileName">Nombre; <c>"unnamed"</c> si el cliente no envió ninguno.</param>
/// <param name="ContentType">Tipo MIME declarado.</param>
/// <param name="SizeBytes">Tamaño del contenido <em>decodificado</em>.</param>
/// <param name="ContentId">Valor de <c>Content-ID</c>, si lo tiene.</param>
/// <param name="IsInline">Si es un recurso inline referenciado desde el HTML.</param>
/// <param name="ChecksumSha256">SHA-256 en hexadecimal minúsculas del contenido decodificado.</param>
/// <param name="Omitted">
/// <see langword="true"/> si supera <c>Storage:MaxInlineAttachmentBytes</c>; entonces
/// <paramref name="ContentBase64"/> es <see langword="null"/>.
/// </param>
/// <param name="ContentBase64">Contenido en base64, o <see langword="null"/> si se omitió.</param>
public sealed record AttachmentInfo(
    string FileName,
    string ContentType,
    long SizeBytes,
    string? ContentId,
    bool IsInline,
    string? ChecksumSha256,
    bool Omitted,
    string? ContentBase64);
