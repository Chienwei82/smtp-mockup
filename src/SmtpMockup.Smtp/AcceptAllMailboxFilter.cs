using SmtpServer;
using SmtpServer.ComponentModel;
using SmtpServer.Mail;
using LibraryMailboxFilter = SmtpServer.Storage.IMailboxFilter;

namespace SmtpMockup.Smtp;

/// <summary>
/// Acepta cualquier remitente y cualquier destinatario, sin validación y sin relay
/// (RF-01): el mockup existe precisamente para no ser un servidor de correo real.
/// </summary>
internal sealed class AcceptAllMailboxFilter : LibraryMailboxFilter, ISessionContextInstanceFactory<LibraryMailboxFilter>
{
    /// <inheritdoc />
    public Task<bool> CanAcceptFromAsync(
        ISessionContext context,
        IMailbox from,
        int senderCount,
        CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    /// <inheritdoc />
    public Task<bool> CanDeliverToAsync(
        ISessionContext context,
        IMailbox to,
        IMailbox from,
        CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    /// <inheritdoc />
    public LibraryMailboxFilter CreateInstance(ISessionContext context) => this;
}

