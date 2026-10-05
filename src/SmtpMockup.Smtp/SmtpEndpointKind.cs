namespace SmtpMockup.Smtp;

/// <summary>Identifica a qué listener pertenece una sesión SMTP.</summary>
public enum SmtpEndpointKind
{
    /// <summary>Listener en texto plano (8025), que anuncia STARTTLS.</summary>
    Plain = 0,

    /// <summary>Listener con STARTTLS obligatorio (8443).</summary>
    StartTls = 1,
}
