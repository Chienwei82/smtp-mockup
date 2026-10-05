namespace SmtpMockup.Core.Storage;

/// <summary>
/// Error de persistencia. El listener SMTP lo traduce a <c>451</c> para no perder al
/// cliente por un problema de disco (SPEC §10.2), y la UI muestra un mensaje en la
/// cabecera en vez de una pantalla en blanco.
/// </summary>
public sealed class MessageStoreException : Exception
{
    /// <summary>Crea la excepción con un mensaje.</summary>
    public MessageStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Crea la excepción con un mensaje y la causa original.</summary>
    public MessageStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Crea la excepción a partir de un error de I/O.</summary>
    public MessageStoreException(string message, string path, Exception innerException)
        : base($"{message} Path: '{path}'.", innerException)
        => Path = path;

    /// <summary>Ruta afectada, si el error se refiere a un archivo concreto.</summary>
    public string? Path { get; }
}
