using SmtpMockup.Core.Options;

namespace SmtpMockup.Smtp.Tests;

/// <summary>
/// Qué listeners se registran según <c>Smtp:*:Enabled</c> (RF-05).
/// </summary>
/// <remarks>
/// Estos tests existen por un defecto concreto: la decisión estaba escrita en línea en
/// <c>Program.cs</c>, que ninguna prueba levanta, y <c>Smtp:Plain:Enabled=false</c> abría 8025
/// igualmente con los 223 tests en verde. Al mover la regla a una función pura, el criterio 3 de
/// SPEC §11.1 queda cubierto sin abrir un socket.
/// </remarks>
public sealed class SmtpListenerSelectionTests
{
    [Fact]
    public void Both_enabled_returns_both_listeners_in_a_stable_order()
    {
        var selected = SmtpListenerSelection.GetEnabled(CreateOptions());

        Assert.Equal([SmtpEndpointKind.Plain, SmtpEndpointKind.StartTls], selected);
    }

    [Fact]
    public void Plain_disabled_returns_only_the_tls_listener()
    {
        var options = CreateOptions();
        options.Smtp.Plain.Enabled = false;

        Assert.Equal([SmtpEndpointKind.StartTls], SmtpListenerSelection.GetEnabled(options));
    }

    [Fact]
    public void Tls_disabled_returns_only_the_plain_listener()
    {
        var options = CreateOptions();
        options.Smtp.StartTls.Enabled = false;

        Assert.Equal([SmtpEndpointKind.Plain], SmtpListenerSelection.GetEnabled(options));
    }

    [Fact]
    public void Both_disabled_returns_nothing()
    {
        var options = CreateOptions();
        options.Smtp.Plain.Enabled = false;
        options.Smtp.StartTls.Enabled = false;

        Assert.Empty(SmtpListenerSelection.GetEnabled(options));
    }

    [Fact]
    public void Disabling_one_does_not_change_the_other_port()
    {
        var options = CreateOptions();
        options.Smtp.Plain.Enabled = false;

        // El bug era opens 8025 igual: no basta con mirar la lista, hay que mirar que el puerto
        // del listener que se apaga no aparece en ninguna parte de la decisión.
        Assert.DoesNotContain(SmtpListenerSelection.GetEnabled(options), kind => kind == SmtpEndpointKind.Plain);
        Assert.Equal(8443, options.Smtp.StartTls.Port);
    }

    [Fact]
    public void Null_options_are_rejected()
        => Assert.Throws<ArgumentNullException>(() => SmtpListenerSelection.GetEnabled(null!));

    private static SmtpMockupOptions CreateOptions() => new();
}