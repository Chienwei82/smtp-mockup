using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmtpMockup.Core.Options;
using SmtpMockup.Core.Storage;
using SmtpMockup.Web.Services;

namespace SmtpMockup.Web.Tests;

/// <summary>
/// El mecanismo de actualización en vivo (SPEC §9.6): el evento del store se agrupa con un
/// debounce y llega a la UI por un único evento, sin importar cuántos correos entraron.
/// </summary>
public sealed class MessageChangeNotifierTests
{
    /// <summary>
    /// Margen generoso: con las cuatro suites de test corriendo en paralelo, una máquina muy
    /// cargada puede tardar más de lo razonable en disparar un temporizador. Un timeout corto
    /// convertiría una prueba lenta en un fallo rojo y falso.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly FakeMessageStore _store = new();
    private readonly WebOptions _options = new() { LiveUpdateDebounceMilliseconds = 20 };

    private MessageChangeNotifier CreateNotifier()
        => new(_store, Options.Create(_options), NullLogger<MessageChangeNotifier>.Instance);

    [Fact]
    public async Task A_stored_message_publishes_a_notification_with_its_summary()
    {
        using var notifier = CreateNotifier();
        var received = new TaskCompletionSource<MessageChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        notifier.Changed += (_, notification) => received.TrySetResult(notification);

        _store.Seed(TestData.Message());
        await _store.SaveAsync(TestData.Message());

        var notification = await received.Task.WaitAsync(Timeout);

        Assert.Single(notification.Stored);
        Assert.Equal(TestData.Id, notification.Stored[0].Id);
        Assert.False(notification.External);
    }

    [Fact]
    public async Task A_burst_of_messages_is_published_as_a_single_notification()
    {
        using var notifier = CreateNotifier();
        var received = new TaskCompletionSource<MessageChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        notifier.Changed += (_, notification) =>
        {
            Interlocked.Increment(ref count);
            received.TrySetResult(notification);
        };

        for (var index = 0; index < 10; index++)
        {
            await _store.SaveAsync(TestData.Message(id: $"01JQ8Z3K7F9A2B3C4D5E6F7G{index:D2}"));
        }

        var notification = await received.Task.WaitAsync(Timeout);
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref count));
        Assert.Equal(10, notification.Stored.Count);
    }

    [Fact]
    public async Task Deletes_are_summed_in_the_same_notification()
    {
        using var notifier = CreateNotifier();
        var received = new TaskCompletionSource<MessageChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        notifier.Changed += (_, notification) => received.TrySetResult(notification);

        await _store.SaveAsync(TestData.Message(id: TestData.Id));
        await _store.SaveAsync(TestData.Message(id: TestData.OtherId));
        await _store.DeleteManyAsync(new Core.Storage.MailQuery());

        var notification = await received.Task.WaitAsync(Timeout);

        Assert.Equal(2, notification.DeletedCount);
    }

    [Fact]
    public async Task An_external_change_is_flagged_as_external()
    {
        using var notifier = CreateNotifier();
        var received = new TaskCompletionSource<MessageChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        notifier.Changed += (_, notification) => received.TrySetResult(notification);

        notifier.NotifyExternalChange();

        Assert.True((await received.Task.WaitAsync(Timeout)).External);
    }

    [Fact]
    public async Task Nothing_is_published_when_nothing_changed()
    {
        _options.LiveUpdateDebounceMilliseconds = 0;
        using var notifier = CreateNotifier();
        var published = 0;
        notifier.Changed += (_, _) => Interlocked.Increment(ref published);

        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref published));
    }

    [Fact]
    public void A_subscriber_that_throws_does_not_break_the_others()
    {
        _options.LiveUpdateDebounceMilliseconds = 0;
        using var notifier = CreateNotifier();
        var reached = false;
        notifier.Changed += (_, _) => throw new InvalidOperationException("suscriptor roto");
        notifier.Changed += (_, _) => reached = true;

        _store.Raise(new MessageStoreChangedEventArgs(MessageStoreChangeKind.Stored, TestData.Summary()));

        Assert.True(reached);
    }

    [Fact]
    public void Disposing_unsubscribes_from_the_store_so_a_dead_page_does_not_leak()
    {
        _options.LiveUpdateDebounceMilliseconds = 0;
        var notifier = CreateNotifier();
        var published = 0;
        notifier.Changed += (_, _) => Interlocked.Increment(ref published);

        notifier.Dispose();
        _store.Raise(new MessageStoreChangedEventArgs(MessageStoreChangeKind.Stored, TestData.Summary()));

        Assert.Equal(0, Volatile.Read(ref published));
    }
}
