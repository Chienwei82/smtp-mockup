using System.Security.Cryptography;
using MimeKit;
using SmtpMockup.Core.Models;

namespace SmtpMockup.Smtp;

internal static partial class ReceivedMessageFactory
{
    /// <summary>
    /// Calcula el Bcc comparando los <c>RCPT TO</c> reales contra las cabeceras
    /// visibles: es la única fuente fiable, porque los clientes borran <c>Bcc</c> antes
    /// de enviar (SPEC §7).
    /// </summary>
    private static IReadOnlyList<MailAddressInfo> ComputeBcc(
        IReadOnlyList<string> envelopeRecipients,
        MimeMessage message)
    {
        if (envelopeRecipients.Count == 0)
        {
            return [];
        }

        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mailbox in message.To.Mailboxes)
        {
            visible.Add(mailbox.Address);
        }

        foreach (var mailbox in message.Cc.Mailboxes)
        {
            visible.Add(mailbox.Address);
        }

        var bcc = new List<MailAddressInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var recipient in envelopeRecipients)
        {
            var address = recipient.Trim().ToLowerInvariant();

            if (address.Length == 0 || visible.Contains(address) || !seen.Add(address))
            {
                continue;
            }

            bcc.Add(new MailAddressInfo(Name: null, address));
        }

        return bcc;
    }

    /// <summary>Lee los headers en el orden original, con el valor crudo.</summary>
    private static IReadOnlyList<HeaderInfo> ReadHeaders(MimeMessage message)
    {
        var headers = new List<HeaderInfo>(message.Headers.Count);

        foreach (var header in message.Headers)
        {
            headers.Add(new HeaderInfo(header.Field, header.Value ?? string.Empty));
        }

        return headers;
    }

    /// <summary>Convierte una lista de direcciones MimeKit al modelo del SPEC.</summary>
    private static IReadOnlyList<MailAddressInfo> ReadAddresses(InternetAddressList list)
    {
        var addresses = new List<MailAddressInfo>(list.Count);

        foreach (var mailbox in list.Mailboxes)
        {
            var name = string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name;
            addresses.Add(new MailAddressInfo(name, mailbox.Address.ToLowerInvariant()));
        }

        return addresses;
    }

    /// <summary>
    /// Quita los ángulos de <c>Message-ID</c>. Si el cliente no lo envió, se genera uno
    /// determinista a partir del id (SPEC §10.1).
    /// </summary>
    private static string NormalizeMessageId(string? messageId, string id)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return $"{id}@smtp-mockup";
        }

        var trimmed = messageId.Trim();
        return trimmed.StartsWith('<') && trimmed.EndsWith('>')
            ? trimmed[1..^1]
            : trimmed;
    }

    /// <summary>
    /// Construye <c>raw</c> según <c>Storage:KeepRawMime</c> y <c>MaxRawMimeBytes</c>.
    /// Si hay que recortar, sólo se guarda el principio del MIME.
    /// </summary>
    private static RawMimeInfo BuildRaw(ReadOnlyMemory<byte> rawBytes, StorageLimits limits)
    {
        if (!limits.KeepRawMime)
        {
            return new RawMimeInfo(rawBytes.Length, Truncated: false, ContentBase64: null);
        }

        var truncated = limits.MaxRawMimeBytes > 0 && rawBytes.Length > limits.MaxRawMimeBytes;
        var slice = truncated ? rawBytes[..limits.MaxRawMimeBytes] : rawBytes;

        return new RawMimeInfo(
            SizeBytes: rawBytes.Length,
            Truncated: truncated,
            ContentBase64: Convert.ToBase64String(slice.Span));
    }
}
