using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor;
using MudBlazor.Services;

namespace SmtpMockup.Web.Services;

/// <summary>
/// Registro de DI de la UI. Los componentes no construyen nada: reciben estos servicios.
/// </summary>
public static class WebServiceCollectionExtensions
{
    /// <summary>
    /// Registra los servicios de la UI: MudBlazor, la paginación del listado, el notificador de
    /// cambios en vivo y, si <paramref name="watchDirectory"/> está activo, el vigilante del
    /// directorio de mensajes.
    /// </summary>
    /// <remarks>
    /// El notificador es <em>singleton</em> a propósito: es el puente entre el hilo del listener
    /// SMTP y los circuitos de Blazor, y tiene que ser el mismo para todos. El servicio de
    /// listado también, porque no guarda estado de circuito y así las pruebas pueden reutilizarlo.
    /// </remarks>
    /// <param name="services">Colección de servicios.</param>
    /// <param name="watchDirectory">Valor de <c>Web:WatchDirectory</c>.</param>
    public static IServiceCollection AddSmtpMockupWeb(
        this IServiceCollection services,
        bool watchDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = true);

        services.TryAddSingleton<IMessageChangeNotifier, MessageChangeNotifier>();
        services.TryAddSingleton<MessageListService>();
        services.TryAddSingleton<MessageDirectoryWatcher>();

        if (watchDirectory)
        {
            services.AddHostedService(provider => provider.GetRequiredService<MessageDirectoryWatcher>());
        }

        return services;
    }
}