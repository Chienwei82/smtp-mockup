using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using SmtpMockup.Core.Options;
using SmtpMockup.Core.Storage;
using SmtpMockup.Web.Services;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// Contexto bUnit con el mismo cableado que el Host: un store, las opciones de <c>Web:*</c> y los
/// servicios de la UI. Los componentes se prueban con sus dependencias reales, no con dobles
/// propios, para que un cambio en el registro de DI rompa las pruebas.
/// </summary>
public abstract class WebTestContext : BunitContext, IAsyncLifetime
{
    protected WebTestContext(WebOptions? webOptions = null, SmtpMockupOptions? smtpOptions = null)
    {
        WebOptions = webOptions ?? new WebOptions();
        SmtpOptions = smtpOptions ?? new SmtpMockupOptions { Web = WebOptions };
        Store = new FakeMessageStore();
        Notifier = new MessageChangeNotifier(Store, Options.Create(WebOptions), NullLogger<MessageChangeNotifier>.Instance);

        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddLogging();
        Services.AddSingleton<IOptions<WebOptions>>(Options.Create(WebOptions));
        Services.AddSingleton<IOptions<StorageOptions>>(Options.Create(new StorageOptions()));

        // Las páginas leen IOptions<SmtpMockupOptions> para mostrar puertos y direcciones (que
        // son configurables): sin esto, el puerto mostrado en la UI sería siempre el default y
        // ningún test podría comprobar que la UI honra la configuración.
        Services.AddSingleton<IOptions<SmtpMockupOptions>>(Options.Create(SmtpOptions));

        Services.AddSingleton<IMessageStore>(Store);
        Services.AddSingleton<IMessageChangeNotifier>(Notifier);
        Services.AddSingleton<MessageListService>();
        Services.AddMudServices();
    }

    /// <summary>
    /// MudBlazor 9 registra servicios que sólo implementan <see cref="IAsyncDisposable"/>, y
    /// xUnit por defecto libera el contexto de forma síncrona, lo que revienta con
    /// "use DisposeAsync". Implementar IAsyncLifetime hace que xUnit llame a DisposeAsync.
    /// </summary>
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    protected WebOptions WebOptions { get; }

    /// <summary>
    /// Opciones tipadas de la aplicación, para los tests que necesitan puertos o direcciones
    /// distintos de los de producción.
    /// </summary>
    protected SmtpMockupOptions SmtpOptions { get; }

    protected FakeMessageStore Store { get; }

    protected MessageChangeNotifier Notifier { get; }

    /// <summary>Publica un cambio como si el store lo hubiera hecho, desde el hilo que usa en producción.</summary>
    public void RaiseStoredChange()
        => Store.Raise(new MessageStoreChangedEventArgs(MessageStoreChangeKind.Stored, TestData.Summary()));
}
