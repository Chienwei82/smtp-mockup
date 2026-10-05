using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Models;
using SmtpMockup.Core.Options;
using SmtpMockup.Core.Storage;

namespace SmtpMockup.Web.Services;

/// <summary>
/// Un cambio del store ya agrupado, listo para que los componentes se re-rendericen.
/// </summary>
/// <param name="Stored">Mensajes guardados durante la ráfaga (puede haber varios).</param>
/// <param name="DeletedCount">Mensados borrados durante la ráfaga.</param>
/// <param name="External">
/// <see langword="true"/> si el cambio lo detectó el vigilante del directorio y no el propio store.
/// </param>
public sealed record MessageChangeNotification(IReadOnlyList<MessageSummary> Stored, int DeletedCount, bool External)
{
    /// <summary>Notificación vacía.</summary>
    public static MessageChangeNotification None { get; } = new([], 0, false);
}

/// <summary>
/// Fuente de la actualización en vivo de la UI: se suscribe a <see cref="IMessageStore.Changed"/>
/// y publica un evento propio, agrupado con el debounce de <c>Web:LiveUpdateDebounceMilliseconds</c>
/// (SPEC §9.6). Los componentes se suscriben a esta interfaz, no al store.
/// </summary>
public interface IMessageChangeNotifier
{
    /// <summary>Se dispara en un hilo del pool, fuera del circuito: el componente debe invocar <c>InvokeAsync</c>.</summary>
    event EventHandler<MessageChangeNotification>? Changed;

    /// <summary>
    /// Notifica un cambio detectado por fuera del proceso (vigilancia del directorio). Entra por
    /// el mismo debounce que los cambios del store, así que la UI no distingue el origen.
    /// </summary>
    void NotifyExternalChange();
}

/// <inheritdoc cref="IMessageChangeNotifier"/>
public sealed class MessageChangeNotifier : IMessageChangeNotifier, IDisposable
{
    private readonly IMessageStore _store;
    private readonly ILogger<MessageChangeNotifier> _logger;
    private readonly Lock _gate = new();
    private readonly List<MessageSummary> _stored = [];
    private readonly TimeSpan _debounce;
    private Timer? _timer;
    private int _deletedCount;
    private bool _external;
    private bool _disposed;

    /// <summary>Crea el notificador y se engancha al evento del store.</summary>
    public MessageChangeNotifier(
        IMessageStore store,
        IOptions<WebOptions> webOptions,
        ILogger<MessageChangeNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(webOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
        _debounce = TimeSpan.FromMilliseconds(webOptions.Value.LiveUpdateDebounceMilliseconds);
        _store.Changed += OnStoreChanged;
    }

    /// <inheritdoc />
    public event EventHandler<MessageChangeNotification>? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Changed -= OnStoreChanged;
        _timer?.Dispose();
        _timer = null;
    }

    /// <inheritdoc />
    public void NotifyExternalChange() => Enqueue(external: true);

    /// <summary>
    /// Traduce el evento del store a una notificación agrupada. El evento del store llega en
    /// el hilo del listener SMTP, nunca en el del circuito, así que aquí sólo se encola.
    /// </summary>
    private void OnStoreChanged(object? sender, MessageStoreChangedEventArgs args)
    {
        if (args.Kind == MessageStoreChangeKind.Stored && args.Message is not null)
        {
            Enqueue(stored: args.Message);
            return;
        }

        Enqueue(deletedCount: Math.Max(args.DeletedCount, 1));
    }

    private void Enqueue(MessageSummary? stored = null, int deletedCount = 0, bool external = false)
    {
        lock (_gate)
        {
            if (stored is not null)
            {
                _stored.Add(stored);
            }

            _deletedCount += deletedCount;
            _external |= external;

            // Sin debounce (o con debounce 0) se publica de inmediato: el temporizador sólo
            // sirve para agrupar ráfagas, no para retrasar el primer aviso.
            if (_debounce <= TimeSpan.Zero)
            {
                Publish();
                return;
            }

            _timer ??= new Timer(_ => Publish(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Vuelca la ráfaga acumulada y publica una única notificación. Se ejecuta dentro del lock
    /// para que dos ráfagas simultáneas no se pisen, y publica con cada handler protegido: un
    /// componente mal escrito no puede tumbar al store ni al resto de suscriptores.
    /// </summary>
    private void Publish()
    {
        MessageChangeNotification notification;

        lock (_gate)
        {
            if (_stored.Count == 0 && _deletedCount == 0 && !_external)
            {
                return;
            }

            notification = new MessageChangeNotification([.. _stored], _deletedCount, _external);
            _stored.Clear();
            _deletedCount = 0;
            _external = false;
        }

        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<MessageChangeNotification> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, notification);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "A Changed subscriber of the message change notifier threw and was ignored");
            }
        }
    }
}