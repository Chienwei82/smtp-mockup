using SmtpMockup.Core.Options;

namespace SmtpMockup.Core.Hosting;

/// <summary>
/// Modo de ejecución <b>efectivamente</b> adoptado por el proceso, ya resuelto a partir de
/// <see cref="HostingMode"/> y de lo que el sistema operativo dice.
/// </summary>
public enum RunningMode
{
    /// <summary>Aplicación de consola: el logging va a la consola.</summary>
    Console = 0,

    /// <summary>Windows Service: el logging va a archivo y al Event Log.</summary>
    WindowsService = 1,
}

/// <summary>
/// Decide en qué modo corre el proceso (D-10). La lógica vive en <c>Core</c> y es <b>pura</b>
/// (recibe el SO y la detección del SCM como parámetros) para poder testear los cuatro casos sin
/// necesidad de una máquina Windows ni de un servicio instalado.
/// </summary>
public static class HostingModeResolver
{
    /// <summary>
    /// Resuelve el modo efectivo.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>Auto</c>: servicio sólo si estamos en Windows <b>y</b> el SCM nos
    /// arrancó. Fuera de Windows el resultado es siempre consola.</description></item>
    /// <item><description><c>Console</c>: consola, aunque el SCM nos haya arrancado. Es un error
    /// de configuración y por eso devuelve <c>false</c> en <paramref name="wasStartedByServiceManager"/>
    /// para que el Host pueda avisar.</description></item>
    /// <item><description><c>WindowsService</c>: servicio. En Linux o macOS degrada a consola
    /// (no hay SCM) sin fallar.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="configuredMode">Modo pedido en <c>Hosting:Mode</c>.</param>
    /// <param name="isWindows">Si el proceso corre en Windows.</param>
    /// <param name="wasStartedByServiceManager">
    /// Si el proceso fue arrancado por el Service Control Manager. En producción se obtiene con
    /// <c>WindowsServiceHelpers.IsWindowsService()</c>; aquí se inyecta para poder testearlo.
    /// </param>
    /// <returns>El modo efectivo con el que arranca el proceso.</returns>
    public static RunningMode Resolve(
        HostingMode configuredMode,
        bool isWindows,
        bool wasStartedByServiceManager)
        => configuredMode switch
        {
            // Se enuncia WindowsService antes que Console a propósito: en Windows gana, y fuera de
            // Windows degrada a consola. Cada modo se enumera explícitamente en lugar de agrupar
            // Console con un '_' junto a Auto: un modo explícito nunca debe depender de la
            // detección del SCM, o "Hosting:Mode=Console" acabaría arrancando como servicio.
            HostingMode.WindowsService when isWindows => RunningMode.WindowsService,
            HostingMode.WindowsService => RunningMode.Console,
            HostingMode.Console => RunningMode.Console,
            _ => isWindows && wasStartedByServiceManager
                ? RunningMode.WindowsService
                : RunningMode.Console,
        };

    /// <summary>
    /// Indica si la combinación de modo pedido y entorno es contradictoria y merece un warning:
    /// <c>Hosting:Mode=Console</c> con el proceso arrancado por el SCM.
    /// </summary>
    /// <param name="configuredMode">Modo pedido en <c>Hosting:Mode</c>.</param>
    /// <param name="wasStartedByServiceManager">Si el SCM arrancó el proceso.</param>
    /// <returns><see langword="true"/> si hay que avisar.</returns>
    public static bool IsContradictory(HostingMode configuredMode, bool wasStartedByServiceManager)
        => configuredMode == HostingMode.Console && wasStartedByServiceManager;

    /// <summary>Si el modo efectivo escribe en el Event Log (sólo Windows).</summary>
    /// <param name="mode">Modo efectivo.</param>
    /// <returns><see langword="true"/> en modo servicio.</returns>
    public static bool UsesEventLog(RunningMode mode) => mode == RunningMode.WindowsService;

    /// <summary>
    /// Si el modo efectivo escribe en un archivo de log. Sólo el servicio: en consola el log ya
    /// está en stdout y duplicarlo en un archivo sería ruido.
    /// </summary>
    /// <param name="mode">Modo efectivo.</param>
    /// <returns><see langword="true"/> en modo servicio.</returns>
    public static bool UsesFileLog(RunningMode mode) => mode == RunningMode.WindowsService;
}