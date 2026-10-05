using SmtpMockup.Core.Options;

namespace SmtpMockup.Smtp;

/// <summary>
/// Decide qué listeners SMTP hay que registrar en el contenedor (RF-05).
/// </summary>
/// <remarks>
/// Vive aquí, y no en <c>Core</c>, porque devuelve <see cref="SmtpEndpointKind"/>, que es un tipo de
/// este proyecto: <c>Core</c> no debe depender de la infraestructura SMTP (D-05).
/// <para>
/// Existe como función pura y testeable por un motivo concreto. La decisión de qué puertos se abren
/// estaba escrita en línea en <c>Program.cs</c>, el composition root que ninguna prueba levanta, así
/// que <c>Smtp:Plain:Enabled=false</c> se anunciaba apagado en el log y abría 8025 igual, con los 223
/// tests en verde. La regla ahora es una lista y esta función la calcula: un test la cubre sin abrir
/// un socket, y volver a escribir la decisión en línea ya no es lo natural.
/// </para>
/// </remarks>
public static class SmtpListenerSelection
{
    /// <summary>
    /// Devuelve los listeners habilitados, en orden fijo (plano primero, cifrado después) para que
    /// dos llamadas con la misma configuración den siempre la misma lista.
    /// </summary>
    /// <param name="options">Configuración ya validada.</param>
    public static IReadOnlyList<SmtpEndpointKind> GetEnabled(SmtpMockupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<SmtpEndpointKind> enabled = [];

        if (options.Smtp.Plain.Enabled)
        {
            enabled.Add(SmtpEndpointKind.Plain);
        }

        if (options.Smtp.StartTls.Enabled)
        {
            enabled.Add(SmtpEndpointKind.StartTls);
        }

        return enabled;
    }
}