using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Hosting;
using SmtpMockup.Core.Options;

namespace SmtpMockup.Core.Tests;

/// <summary>
/// Marca las clases que tocan el directorio de trabajo para que no corran en paralelo con nada:
/// el CWD es estado global del proceso y otro test podría assertar sobre él a la vez.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
    public const string Name = "current-directory";
}

/// <summary>
/// Detección del modo de ejecución (D-10). Se prueba la lógica con el SO y la detección del SCM
/// inyectados, porque no se puede levantar un Windows Service en un runner Linux: lo que importa
/// es que los cuatro casos queden decididos sin ambigüedad.
/// </summary>
public sealed class HostingModeResolverTests
{
    [Fact]
    public void Auto_on_windows_started_by_the_service_manager_is_a_windows_service()
        => Assert.Equal(
            RunningMode.WindowsService,
            HostingModeResolver.Resolve(HostingMode.Auto, isWindows: true, wasStartedByServiceManager: true));

    [Fact]
    public void Auto_on_windows_started_from_a_console_is_a_console_application()
        => Assert.Equal(
            RunningMode.Console,
            HostingModeResolver.Resolve(HostingMode.Auto, isWindows: true, wasStartedByServiceManager: false));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Auto_outside_windows_is_always_a_console_application(bool startedByServiceManager)
        => Assert.Equal(
            RunningMode.Console,
            HostingModeResolver.Resolve(HostingMode.Auto, isWindows: false, wasStartedByServiceManager: startedByServiceManager));

    [Fact]
    public void Console_wins_even_if_the_service_manager_started_the_process()
        => Assert.Equal(
            RunningMode.Console,
            HostingModeResolver.Resolve(HostingMode.Console, isWindows: true, wasStartedByServiceManager: true));

    [Fact]
    public void WindowsService_is_forced_on_windows_even_when_started_from_a_console()
        => Assert.Equal(
            RunningMode.WindowsService,
            HostingModeResolver.Resolve(HostingMode.WindowsService, isWindows: true, wasStartedByServiceManager: false));

    [Fact]
    public void WindowsService_degrades_to_console_outside_windows()
    {
        // No es un error: en Linux no hay SCM, y morir sería peor que arrancar como consola.
        Assert.Equal(
            RunningMode.Console,
            HostingModeResolver.Resolve(HostingMode.WindowsService, isWindows: false, wasStartedByServiceManager: false));
    }

    [Fact]
    public void Forcing_console_under_the_service_manager_is_reported_as_contradictory()
    {
        Assert.True(HostingModeResolver.IsContradictory(HostingMode.Console, wasStartedByServiceManager: true));
        Assert.False(HostingModeResolver.IsContradictory(HostingMode.Console, wasStartedByServiceManager: false));
        Assert.False(HostingModeResolver.IsContradictory(HostingMode.Auto, wasStartedByServiceManager: true));
        Assert.False(HostingModeResolver.IsContradictory(HostingMode.WindowsService, wasStartedByServiceManager: true));
    }

    [Fact]
    public void Only_the_service_mode_writes_to_a_file_or_the_event_log()
    {
        Assert.True(HostingModeResolver.UsesFileLog(RunningMode.WindowsService));
        Assert.True(HostingModeResolver.UsesEventLog(RunningMode.WindowsService));

        Assert.False(HostingModeResolver.UsesFileLog(RunningMode.Console));
        Assert.False(HostingModeResolver.UsesEventLog(RunningMode.Console));
    }
}

/// <summary>
/// Resolución de rutas relativas contra el directorio del ejecutable. Este es el comportamiento
/// que hace que el mismo binario funcione con doble clic y como Windows Service.
/// </summary>
[Collection(CurrentDirectoryCollection.Name)]
public sealed class HostPathTests : IDisposable
{
    private readonly string _originalWorkingDirectory = Directory.GetCurrentDirectory();

    public HostPathTests()
    {
        // vstest arranca los tests con el CWD en la carpeta de salida del ejecutable, así que
        // "resolver contra el ejecutable" y "resolver contra el CWD" darían el mismo resultado y
        // este test no probaría nada. Se mueve el CWD para reproducir el caso del servicio.
        Directory.SetCurrentDirectory(Path.GetTempPath());
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalWorkingDirectory);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_relative_path_is_resolved_next_to_the_executable_not_the_working_directory()
    {
        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "messages")),
            HostPath.Resolve(Path.Combine("data", "messages")));
    }

    [Fact]
    public void An_absolute_path_is_used_as_is()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "smtp-mockup", "messages");

        Assert.Equal(Path.GetFullPath(absolute), HostPath.Resolve(absolute));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_path_stays_empty(string? path)
        => Assert.Equal(string.Empty, HostPath.Resolve(path));
/// <summary>
/// Nombres de archivo de log y retención. Sin esto, un servicio que corre semanas llenaría el
/// disco con un archivo por día.
/// </summary>
public sealed class FileLogLayoutTests
{
    private static readonly DateOnly Day = new(2026, 2, 10);

    private static string LogPath(string day)
        => FileLogLayout.ResolveFile(new HostingOptions(), DateOnly.Parse(day));

    [Fact]
    public void The_date_token_becomes_the_day()
        => Assert.EndsWith(
            Path.Combine("logs", "smtp-mockup-2026-02-10.log"),
            FileLogLayout.ResolveFile(new HostingOptions(), Day));

    [Fact]
    public void A_relative_log_directory_hangs_from_the_executable_not_the_working_directory()
        => Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "logs", "smtp-mockup-2026-02-10.log")),
            FileLogLayout.ResolveFile(new HostingOptions(), Day));

    [Fact]
    public void An_empty_log_directory_disables_the_file_log()
    {
        var options = new HostingOptions { LogDirectory = string.Empty };

        Assert.Equal(string.Empty, FileLogLayout.ResolveFile(options, Day));
    }

    [Fact]
    public void An_empty_file_name_disables_the_file_log()
    {
        var options = new HostingOptions { LogFileName = "  " };

        Assert.Equal(string.Empty, FileLogLayout.ResolveFile(options, Day));
    }

    [Fact]
    public void Files_older_than_the_retention_are_selected()
    {
        var options = new HostingOptions { LogRetentionDays = 7 };
        var files = new[]
        {
            LogPath("2026-02-01"),  // 9 días: caduca
            LogPath("2026-02-03"),  // 7 días: en el límite, se conserva
            LogPath("2026-02-10"),  // hoy
        };

        var expired = FileLogLayout.SelectExpired(options, files, Day);

        Assert.Equal([LogPath("2026-02-01")], expired);
    }

    [Fact]
    public void Retention_zero_keeps_everything()
    {
        var options = new HostingOptions { LogRetentionDays = 0 };

        Assert.Empty(FileLogLayout.SelectExpired(options, [LogPath("2020-01-01")], Day));
    }

    [Fact]
    public void Files_that_are_not_ours_are_never_selected()
    {
        // Un log de otra herramienta con un nombre parecido no se puede borrar: 'logs' es un
        // nombre de carpeta genérico y puede estar compartido.
        var options = new HostingOptions { LogRetentionDays = 1 };
        var foreign = Path.Combine(HostPath.Resolve("logs"), "otro-servicio-2020-01-01.log");

        Assert.Empty(FileLogLayout.SelectExpired(options, [foreign], Day));
    }

    [Fact]
    public void A_file_with_no_date_in_its_name_is_never_selected()
    {
        var options = new HostingOptions { LogRetentionDays = 1 };
        var weird = Path.Combine(HostPath.Resolve("logs"), "smtp-mockup-manual.log");

        Assert.Empty(FileLogLayout.SelectExpired(options, [weird], Day));
    }
}

/// <summary>
/// Validación de <c>Hosting:*</c>. El caso que de verdad importa es el servicio sin carpeta de
/// logs: arrancaría sin dejar ningún rastro y nadie se enteraría.
/// </summary>
public sealed class HostingOptionsValidationTests
{
    private static readonly HostingOptionsValidator Validator = new();

    private static string Validate(HostingOptions options)
    {
        var result = Validator.Validate(null, options);
        Assert.True(result.Failed, "Expected the options to be invalid.");
        return result.FailureMessage ?? string.Join("; ", result.Failures ?? []);
    }

    private static void AssertValid(HostingOptions options)
    {
        var result = Validator.Validate(null, options);
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void Defaults_are_valid()
    {
        var options = new HostingOptions();

        AssertValid(options);

        Assert.Equal("Auto", options.Mode);
        Assert.Equal(HostingMode.Auto, options.ParsedMode);
        Assert.Equal("logs", options.LogDirectory);
        Assert.Equal("smtp-mockup-{Date}.log", options.LogFileName);
        Assert.Equal(7, options.LogRetentionDays);
        Assert.Equal("smtp-mockup", options.EventLogSource);
        Assert.Equal("Information", options.EventLogLevel);
    }

    [Fact]
    public void An_unknown_mode_names_the_key()
        => Assert.Contains(
            "'Hosting:Mode'",
            Validate(new HostingOptions { Mode = "Daemon" }),
            StringComparison.Ordinal);

    [Fact]
    public void Mode_parsing_is_case_insensitive()
        => Assert.Equal(HostingMode.WindowsService, new HostingOptions { Mode = "windowsservice" }.ParsedMode);

    [Fact]
    public void A_service_without_a_log_directory_is_rejected()
        => Assert.Contains(
            "'Hosting:LogDirectory'",
            Validate(new HostingOptions { Mode = "WindowsService", LogDirectory = string.Empty }),
            StringComparison.Ordinal);

    [Fact]
    public void A_console_application_may_have_no_log_directory()
        => AssertValid(new HostingOptions { Mode = "Console", LogDirectory = string.Empty });

    [Theory]
    [InlineData(-1)]
    [InlineData(3_651)]
    public void An_out_of_range_retention_names_the_key(int days)
        => Assert.Contains(
            "'Hosting:LogRetentionDays'",
            Validate(new HostingOptions { LogRetentionDays = days }),
            StringComparison.Ordinal);

    [Fact]
    public void An_empty_event_log_source_is_rejected()
        => Assert.Contains(
            "'Hosting:EventLogSource'",
            Validate(new HostingOptions { EventLogSource = " " }),
            StringComparison.Ordinal);

    [Fact]
    public void An_unknown_event_log_level_names_the_key()
        => Assert.Contains(
            "'Hosting:EventLogLevel'",
            Validate(new HostingOptions { EventLogLevel = "Verbose" }),
            StringComparison.Ordinal);

    [Fact]
    public void The_file_log_is_off_when_either_the_directory_or_the_file_name_is_empty()
    {
        Assert.True(new HostingOptions().HasFileLog);

        Assert.False(new HostingOptions { LogDirectory = string.Empty }.HasFileLog);
        Assert.False(new HostingOptions { LogDirectory = "  " }.HasFileLog);
        Assert.False(new HostingOptions { LogFileName = string.Empty }.HasFileLog);
        Assert.False(new HostingOptions { LogFileName = " " }.HasFileLog);
    }
}


    [Fact]
    public void Combine_anchors_every_segment_at_the_executable_directory()
        => Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "logs", "smtp-mockup-2026-02-10.log")),
            HostPath.Combine("logs", "smtp-mockup-2026-02-10.log"));
}
