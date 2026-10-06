using Microsoft.Extensions.Options;

namespace SmtpMockup.Core.Options;

/// <summary>
/// Modo de ejecución del proceso (D-10). El mismo binario es a la vez una aplicación de consola
/// y un Windows Service; el modo se detecta al arrancar.
/// </summary>
public enum HostingMode
{
    /// <summary>
    /// Detecta el modo: <see cref="WindowsService"/> si el proceso fue arrancado por el SCM de
    /// Windows, <see cref="Console"/> en cualquier otro caso. Es el valor por defecto.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Fuerza el modo consola. En Windows con el proceso ya arrancado por el SCM esto es un error
    /// de configuración: el token del proceso no corresponde al de un usuario interactivo.
    /// </summary>
    Console = 1,

    /// <summary>
    /// Fuerza el modo servicio. Sólo tiene sentido en Windows; en Linux el proceso arranca como
    /// consola (el sistema no tiene un SCM equivalente).
    /// </summary>
    WindowsService = 2,
}

/// <summary>
/// Opciones de <c>Hosting:*</c>: modo de ejecución y destino del logging.
/// </summary>
/// <remarks>
/// Los sinks de logging se derivan del modo resuelto: consola → <c>Console</c>; servicio →
/// archivo + Event Log. Las rutas relativas de <see cref="LogDirectory"/> cuelgan del directorio
/// del ejecutable (<see cref="SmtpMockup.Core.Hosting.HostPath"/>), no del directorio de trabajo.
/// </remarks>
public sealed class HostingOptions
{
    /// <summary>
    /// Modo de ejecución. Se mantiene como texto para que un valor desconocido produzca un
    /// mensaje de validación propio en vez del error genérico del binder de configuración.
    /// </summary>
    public string Mode { get; set; } = nameof(HostingMode.Auto);

    /// <summary>
    /// <see cref="Mode"/> ya convertido a enum.
    /// </summary>
    /// <exception cref="OptionsValidationException">El modo no es conocido.</exception>
    public HostingMode ParsedMode => Enum.TryParse<HostingMode>(Mode, ignoreCase: true, out var mode)
        && Enum.IsDefined(mode)
            ? mode
            : throw new OptionsValidationException(
                InvalidModeMessage(Mode),
                typeof(HostingOptions),
                [InvalidModeMessage(Mode)]);

    /// <summary>
    /// Carpeta de los archivos de log en modo servicio. Relativa ⇒ bajo el directorio del
    /// ejecutable. Vacía desactiva el log en archivo.
    /// </summary>
    public string LogDirectory { get; set; } = "logs";

    /// <summary>
    /// Patrón del nombre de archivo de log. <c>{Date}</c> se sustituye por <c>yyyy-MM-dd</c>, lo
    /// que da un archivo por día y evita tener que rotar a mano.
    /// </summary>
    public string LogFileName { get; set; } = "smtp-mockup-{Date}.log";

    /// <summary>Días que se conservan los archivos de log antes de borrarlos al arrancar.</summary>
    public int LogRetentionDays { get; set; } = 7;

    /// <summary>
    /// Origen registrado en el Event Log de Windows. Es la clave por la que
    /// <c>install-service.ps1</c> registra la fuente y por la que se filtra después.
    /// </summary>
    public string EventLogSource { get; set; } = "smtp-mockup";

    /// <summary>
    /// Nivel mínimo que se envía al Event Log. Por defecto <c>Information</c>: el Event Log es un
    /// destino caro y con límites de tamaño, así que trazas <c>Debug</c> se quedan en el archivo.
    /// </summary>
    public string EventLogLevel { get; set; } = "Information";

    /// <summary>
    /// Si hay un destino de archivo configurado. En modo servicio es obligatorio: sin archivo el
    /// proceso loguea al Event Log pero las trazas <c>Debug</c> se pierden y no hay histórico que
    /// depurar cuando el servicio ya no está.
    /// </summary>
    public bool HasFileLog
        => !string.IsNullOrWhiteSpace(LogDirectory) && !string.IsNullOrWhiteSpace(LogFileName);

    /// <summary>Mensaje de <see cref="Mode"/> inválida, compartido por el validador y la opción.</summary>
    internal static string InvalidModeMessage(string? value)
        => $"'Hosting:Mode' must be 'Auto', 'Console' or 'WindowsService' (current value: '{value}').";
}