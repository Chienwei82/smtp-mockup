using System.Text;
using Microsoft.Extensions.Logging;
using SmtpMockup.Core.Hosting;
using SmtpMockup.Core.Options;

namespace SmtpMockup.Host.Logging;

/// <summary>
/// Provider de logging a archivo, con un archivo por día. Existe porque en modo Windows Service no
/// hay consola: sin él, el único destino sería el Event Log, que tiene límites de tamaño y no
/// guarda trazas <c>Debug</c>.
/// </summary>
/// <remarks>
/// <para>
/// Es un provider propio y deliberadamente simple (un append con lock) en lugar de un paquete
/// externo: el formato de línea no es el contrato de nadie, y
/// <c>Microsoft.Extensions.Logging</c> no trae un provider de archivo.
/// </para>
/// <para>
/// La rotación es por día y el borrado por retención ocurre al arrancar (ver
/// <see cref="PruneExpired"/>): un proceso que corre semanas no necesita un temporizador, y así el
/// borrado no compite con el arranque por el lock del archivo.
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly HostingOptions _options;
    private readonly LogLevel _minimumLevel;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _writerFailed;

    /// <summary>Crea el provider con la configuración de <c>Hosting:*</c>.</summary>
    /// <param name="options">Opciones de hosting (carpeta, patrón y retención).</param>
    /// <param name="minimumLevel">Nivel mínimo que se escribe.</param>
    public FileLoggerProvider(HostingOptions options, LogLevel minimumLevel)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _minimumLevel = minimumLevel;
    }

    /// <summary>La ruta del archivo de hoy, o cadena vacía si el log en archivo está desactivado.</summary>
    public string CurrentFilePath { get; private set; } = string.Empty;

    /// <summary>
    /// Borra los archivos que superaron la retención. Se llama una vez al arrancar, con lo que el
    /// archivo del día vigente nunca se borra por estar viejo.
    /// </summary>
    /// <returns>Las rutas que se pudieron borrar.</returns>
    public IReadOnlyList<string> PruneExpired(DateOnly today)
    {
        var directory = HostPath.Resolve(_options.LogDirectory);

        if (_options.LogRetentionDays <= 0 || !Directory.Exists(directory))
        {
            return [];
        }

        var candidates = Directory.EnumerateFiles(directory, "*.log");
        var deleted = new List<string>();

        foreach (var path in FileLogLayout.SelectExpired(_options, candidates, today))
        {
            if (string.Equals(path, CurrentFilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(path);
                deleted.Add(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Un log que no se puede borrar (abierto por un antivirus, sin permisos) no puede
                // impedir que el servicio arranque: es una limpieza, no un requisito.
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimumLevel;

    private void Write(string line, Exception? exception)
    {
        lock (_gate)
        {
            if (_writerFailed)
            {
                return;
            }

            try
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var path = FileLogLayout.ResolveFile(_options, today);

                if (path.Length == 0)
                {
                    return;
                }

                if (_writer is null || _writerDate != today || !string.Equals(path, CurrentFilePath, StringComparison.Ordinal))
                {
                    _writer?.Dispose();
                    _writer = null;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    _writer = new StreamWriter(
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    _writerDate = today;
                    CurrentFilePath = path;
                }

                _writer.WriteLine(line);

                if (exception is not null)
                {
                    _writer.WriteLine(exception.ToString());
                }
            }
            catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException)
            {
                // Perder el log no puede tumbar el servicio. Se deja de reintentar hasta el
                // próximo arranque, en vez de escribir una excepción por cada línea perdida.
                _writerFailed = true;
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);

            var message = formatter(state, exception);
            var line = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [UTC] [{Abbreviate(logLevel)}] {category}: {message}");

            provider.Write(line, exception);
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "NON",
        };
    }
}