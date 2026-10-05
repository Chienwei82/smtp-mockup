using System.Net;
using Microsoft.Extensions.Options;

namespace SmtpMockup.Core.Options;

/// <summary>
/// Modo de resolución del certificado del listener STARTTLS (SPEC §6.2).
/// </summary>
public enum CertificateMode
{
    /// <summary>Certificado de desarrollo de .NET; si no existe, se genera y persiste uno autofirmado.</summary>
    Auto = 0,

    /// <summary>Carga estricta del PFX en <see cref="CertificateOptions.Path"/>; falla si falta.</summary>
    File = 1,
}

/// <summary>
/// Nivel de cifrado de un listener SMTP (SPEC §6.3).
/// </summary>
public enum SmtpSecurityMode
{
    /// <summary>Sin TLS: el diálogo viaja en claro. Es lo del listener 8025.</summary>
    None = 0,

    /// <summary>El listener anuncia <c>STARTTLS</c> y el cliente decide si actualizar.</summary>
    StartTls = 1,

    /// <summary>
    /// TLS implícito (SMTPS): el socket ya está cifrado en el momento del <c>220</c>,
    /// sin comando <c>STARTTLS</c>. Es lo del listener 8443.
    /// </summary>
    Implicit = 2,
}

/// <summary>
/// Opciones de un listener SMTP individual (puerto, habilitación y cifrado).
/// </summary>
public sealed class SmtpEndpointOptions
{
    /// <summary>Si es <see langword="false"/>, el listener no se arranca.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Puerto TCP del listener. Debe estar entre 1 y 65535.</summary>
    public int Port { get; set; }

    /// <summary>
    /// Dirección de binding del listener. Por defecto <c>127.0.0.1</c>: el mockup acepta
    /// cualquier correo sin autenticación, así que exponerlo a la red local es una decisión
    /// explícita, no un default. Fuera de loopback ⇒ <b>warning</b> en el log de arranque
    /// (no error: levantar el SMTP en claro en una red es un caso legítimo en CI y
    /// contenedores, a diferencia de exponer la UI, SPEC §6.1).
    /// </summary>
    /// <remarks>
    /// Sin esta clave, <c>SmtpServer.Port(port)</c> resuelve a <see cref="IPAddress.Any"/> y el
    /// listener aceptaba correo de toda la red sin decir nada. Ver <see cref="ParsedBindAddress"/>.
    /// </remarks>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// <see cref="BindAddress"/> ya convertido a <see cref="IPAddress"/>. Cae en
    /// <see cref="IPAddress.Loopback"/> si el valor está vacío, porque el validador corre antes
    /// y ya garantiza que sea un literal válido.
    /// </summary>
    public IPAddress ParsedBindAddress
        => IPAddress.TryParse(BindAddress, out var address) ? address : IPAddress.Loopback;

    /// <summary>
    /// Cifrado del listener: <c>None</c>, <c>StartTls</c> o <c>Implicit</c>. Se mantiene como
    /// texto para que un valor desconocido produzca un mensaje de validación propio en vez
    /// del error genérico del binder de configuración.
    /// </summary>
    public string Security { get; set; } = nameof(SmtpSecurityMode.None);

    /// <summary>
    /// <see cref="Security"/> ya convertido a enum. Lanza <see cref="OptionsValidationException"/>
    /// si el valor no es válido; la validación de opciones corre antes, al arrancar.
    /// </summary>
    /// <exception cref="OptionsValidationException">El modo no es conocido.</exception>
    public SmtpSecurityMode ParsedSecurity => Enum.TryParse<SmtpSecurityMode>(
            Security,
            ignoreCase: true,
            out var mode)
        && Enum.IsDefined(mode)
            ? mode
            : throw new OptionsValidationException(
                InvalidSecurityModeMessage(Security),
                typeof(SmtpEndpointOptions),
                [InvalidSecurityModeMessage(Security)]);

    /// <summary>
    /// Mensaje de <c>Security</c> inválida, compartido por el validador y la opción. El
    /// <paramref name="key"/> es el prefijo de configuración (<c>Smtp:StartTls</c>); sin él se
    /// nombra la clave a secas, que es lo que ve quien lee <c>appsettings.json</c>.
    /// </summary>
    internal static string InvalidSecurityModeMessage(string? value, string? key = null)
        => $"'{key}:Security' must be 'None', 'StartTls' or 'Implicit' (current value: '{value}').";
}

/// <summary>
/// Opciones de <c>Certificate:*</c>.
/// </summary>
public sealed class CertificateOptions
{
    /// <summary>
    /// Modo del certificado: <c>Auto</c> o <c>File</c>. Se mantiene como texto para que un
    /// valor desconocido produzca un mensaje de validación propio en vez del error genérico
    /// del binder de configuración.
    /// </summary>
    public string Mode { get; set; } = nameof(CertificateMode.Auto);

    /// <summary>
    /// Ruta del PFX. Obligatoria en modo <see cref="CertificateMode.File"/>;
    /// en modo <c>Auto</c> es la ruta donde se persiste el certificado generado.
    /// </summary>
    public string Path { get; set; } = "certs/dev.pfx";

    /// <summary>Contraseña del PFX. Vacía significa "sin contraseña".</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Si es <see langword="true"/> y no hay ningún certificado disponible (ni dev-certs ni
    /// un PFX persistido legible), se genera uno autofirmado y se persiste en
    /// <see cref="Path"/>. Con <see langword="false"/> la ausencia de certificado es un error
    /// fatal en vez de una generación silenciosa (SPEC §6.2).
    /// </summary>
    public bool AutoGenerateSelfSigned { get; set; } = true;

    /// <summary>
    /// Si es <see langword="true"/> (default) y el modo es <see cref="CertificateMode.Auto"/>,
    /// se prefiere el certificado de desarrollo de <c>dotnet dev-certs https</c> antes de
    /// generar uno propio. Ponerlo en <see langword="false"/> fuerza a usar siempre el PFX de
    /// <see cref="Path"/>, aunque exista un dev-certs en la máquina.
    /// </summary>
    public bool UseDevelopmentCertificate { get; set; } = true;

    /// <summary>
    /// <see cref="Mode"/> ya convertido a enum. Lanza <see cref="OptionsValidationException"/> si
    /// el valor no es válido; la validación de opciones corre antes, al arrancar.
    /// </summary>
    /// <exception cref="OptionsValidationException">El modo no es <c>Auto</c> ni <c>File</c>.</exception>
    public CertificateMode ParsedMode => Enum.TryParse<CertificateMode>(Mode, ignoreCase: true, out var mode)
        && Enum.IsDefined(mode)
            ? mode
            : throw new OptionsValidationException(
                $"'Certificate:Mode' must be 'Auto' or 'File' (current value: '{Mode}').",
                typeof(CertificateOptions),
                null);
}

/// <summary>
/// Opciones de <c>Storage:*</c>.
/// </summary>
public sealed class StorageOptions
{
    /// <summary>Directorio raíz de los JSON; se crea automáticamente si no existe.</summary>
    public string Directory { get; set; } = "data/messages";

    /// <summary>
    /// Adjuntos mayores que este tamaño se guardan con <c>omitted: true</c> y sin
    /// <c>contentBase64</c>, para no escribir JSON gigantes (SPEC §7).
    /// </summary>
    /// <remarks>
    /// El valor <c>0</c> significa "sin tope", igual que en <see cref="MaxRawMimeBytes"/>: las
    /// dos opciones son topes del mismo tipo y se leen igual. Si aquí el 0 significara "omite
    /// todo", un <c>MaxInlineAttachmentBytes=0</c> puesto queriendo desactivar el límite
    /// descartaría silenciosamente <em>cada</em> adjunto, que es justo lo contrario de lo que
    /// se pidió.
    /// </remarks>
    public int MaxInlineAttachmentBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Si es <see langword="false"/>, el MIME crudo no se conserva.</summary>
    public bool KeepRawMime { get; set; } = true;

    /// <summary>
    /// Tope del MIME crudo conservado. Superarlo marca <c>raw.truncated: true</c>.
    /// El valor 0 significa "sin tope".
    /// </summary>
    public int MaxRawMimeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Si el JSON se escribe con sangrado (más legible, algo más grande).</summary>
    public bool WriteIndented { get; set; } = true;
}

/// <summary>
/// Opciones de <c>Web:*</c>. La UI Blazor se mapea solo si <see cref="Enabled"/> es <see langword="true"/>.
/// </summary>
public sealed class WebOptions
{
    /// <summary>Si es <see langword="false"/>, no se mapea la UI; el SMTP sigue activo.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Puerto HTTP de la UI.</summary>
    public int Port { get; set; } = 8080;

    /// <summary>Dirección de binding de la UI. Fuera de loopback ⇒ warning en el log.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Título de la UI: el del navegador y el de la barra superior. Vacío ⇒
    /// <c>smtp-mockup</c>, para que la opción nunca pueda dejar la página sin título.
    /// </summary>
    public string Title { get; set; } = "smtp-mockup";

    /// <summary>Filas por página por defecto en el listado (SPEC §9.3).</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>
    /// Tope de filas por página. Superarlo en la URL o en el selector muestra un aviso de
    /// validación en la propia UI en vez de clamparsear en silencio (SPEC §10.3).
    /// </summary>
    public int MaxPageSize { get; set; } = 200;

    /// <summary>
    /// Tope del HTML que se muestra en el <c>iframe sandbox</c>. Por encima de este tamaño el
    /// correo se muestra como texto escapado en un <c>&lt;pre&gt;</c> (SPEC §9.4).
    /// </summary>
    public int MaxHtmlPreviewBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// Espera de agrupación de las notificaciones de la UI. Ráfagas mayores que un arrival
    /// por segundo se re-renderizan una sola vez tras esta pausa (SPEC §9.6).
    /// </summary>
    public int LiveUpdateDebounceMilliseconds { get; set; } = 250;

    /// <summary>
    /// Vigila el directorio de mensajes con <see cref="System.IO.FileSystemWatcher"/> para
    /// detectar altas y bajas hechas por fuera del proceso (SPEC §9.6).
    /// </summary>
    public bool WatchDirectory { get; set; } = true;

    /// <summary>Muestra el botón de descarga del MIME crudo (<c>.eml</c>) (SPEC §9.5).</summary>
    public bool ShowRawMimeDownload { get; set; } = true;
}

/// <summary>
/// Raíz tipada de la configuración, ligada a las secciones <c>Hosting</c>, <c>Smtp</c>,
/// <c>Certificate</c>, <c>Storage</c> y <c>Web</c> del <c>appsettings.json</c> (SPEC §6).
/// </summary>
public sealed class SmtpMockupOptions
{
    /// <summary>Sección <c>Hosting</c>.</summary>
    public HostingOptions Hosting { get; set; } = new();

    /// <summary>Sección <c>Smtp</c>.</summary>
    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>Sección <c>Certificate</c>.</summary>
    public CertificateOptions Certificate { get; set; } = new();

    /// <summary>Sección <c>Storage</c>.</summary>
    public StorageOptions Storage { get; set; } = new();

    /// <summary>Sección <c>Web</c>.</summary>
    public WebOptions Web { get; set; } = new();

    /// <summary>Atajo a <see cref="Smtp.Plain"/>.</summary>
    public SmtpEndpointOptions Plain => Smtp.Plain;

    /// <summary>Atajo a <see cref="Smtp.StartTls"/>.</summary>
    public SmtpEndpointOptions StartTls => Smtp.StartTls;
}

/// <summary>
/// Opciones de <c>Smtp:*</c>.
/// </summary>
public sealed class SmtpOptions
{
    /// <summary>Listener en texto plano (8025 por defecto), sin TLS.</summary>
    public SmtpEndpointOptions Plain { get; set; } = new()
    {
        Port = 8025,
        Security = nameof(SmtpSecurityMode.None),
    };

    /// <summary>
    /// Listener cifrado de 8443. Anuncia <c>STARTTLS</c> y el cliente decide si actualizar; el
    /// certificado sale de <see cref="CertificateOptions"/>. Poner <c>Security=Implicit</c> lo
    /// convierte en SMTPS (socket ya cifrado, sin comando <c>STARTTLS</c>).
    /// </summary>
    public SmtpEndpointOptions StartTls { get; set; } = new()
    {
        Port = 8443,
        Security = nameof(SmtpSecurityMode.StartTls),
    };

    /// <summary>Tope de tamaño de mensaje en megabytes (25 por defecto, máximo 2047).</summary>
    public int MaxMessageSizeMb { get; set; } = 25;

    /// <summary>
    /// Conexiones SMTP simultáneas admitidas. <c>0</c> (por defecto) significa sin límite.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SmtpServer 11.1.0 <b>no</b> expone ningún límite de concurrencia en
    /// <c>SmtpServerOptionsBuilder</c> (sólo <c>MaxAuthenticationAttempts</c>,
    /// <c>MaxMessageSize</c> y <c>MaxRetryCount</c>). El límite se aplica aquí, contando las
    /// sesiones con los eventos <c>SessionCreated</c>/<c>SessionCompleted</c> y respondiendo
    /// <c>421</c> —el código SMTP estándar para «demasiadas conexiones»— a las que exceedan el
    /// tope, en vez de aceptarlas y dejarlas competir por los recursos.
    /// </para>
    /// <para>
    /// El valor por defecto es <c>0</c> y no un número: el mockup no es un servidor de correo y
    /// poner un tope bajo estorbaría a quien prueba una ráfaga de envíos. Quien lo quiera lo
    /// activa; quien no, no cambia de comportamiento.
    /// </para>
    /// </remarks>
    public int MaxConcurrentConnections { get; set; }

    /// <summary>
    /// <see cref="MaxMessageSizeMb"/> expresado en bytes.
    /// </summary>
    /// <remarks>
    /// El tope del validador es <see cref="SmtpOptionsValidator.MaxMessageSizeMbLimit"/>, que está
    /// deliberadamente por debajo de 2048: <c>2048 * 1024 * 1024</c> es 2.147.483.648, una unidad
    /// por encima de <see cref="int.MaxValue"/>, y en aritmética <c>int</c> sin comprobar eso
    /// desborda a un valor <em>negativo</em> que llegaría al límite de la librería.
    /// </remarks>
    public int MaxMessageSizeBytes => MaxMessageSizeMb * 1024 * 1024;
}