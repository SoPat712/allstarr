using allstarr.Services.Common;

namespace allstarr.Tests.Api;

public sealed class ProviderTryMediaReservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "provider-try-reservation-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _user = Guid.NewGuid();
    private readonly TestClock _clock = new();
    private string OwnedDirectory => Path.Combine(_root, "owned");

    [Fact]
    public void Default_reservation_caps_downloads_at_two_gibibytes()
    {
        using var store = new ProviderTryMediaStore(_clock, OwnedDirectory);
        using var reservation = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        Assert.Equal(2L * 1024 * 1024 * 1024, reservation.MaximumBytes);
    }

    [Fact]
    public async Task Full_storage_cannot_reserve_another_download()
    {
        using var store = Store();
        await store.CreateFileAsync(_user, Source(100), "audio/flac", "existing.flac");
        Assert.Null(store.TryReserveDownload());
    }

    [Fact]
    public async Task Reservation_blocks_concurrent_downloads_and_copied_storage()
    {
        using var store = Store();
        var reservations = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(store.TryReserveDownload)));
        using var reservation = Assert.Single(reservations, item => item != null)!;
        Assert.Equal(100, reservation.MaximumBytes);
        Assert.Null(store.TryReserveDownload());
        var source = Source(10);
        await Assert.ThrowsAsync<IOException>(() => store.CreateFileAsync(_user, source, "audio/flac", "sample.flac"));
        Assert.Empty(Directory.GetFiles(OwnedDirectory));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Disposing_unused_reservation_releases_capacity_once()
    {
        using var store = Store();
        var reservation = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        reservation.Dispose();
        reservation.Dispose();
        using var next = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        Assert.Equal(100, next.MaximumBytes);
        Assert.Throws<ObjectDisposedException>(() => reservation.CreateFileTicket(
            _user, Source(10), "audio/flac", "sample.flac", () => { }));
    }

    [Fact]
    public async Task Reservation_uses_remaining_capacity_after_copied_ticket_files()
    {
        using var store = Store();
        await store.CreateFileAsync(_user, Source(30), "audio/flac", "existing.flac");
        using var reservation = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        Assert.Equal(70, reservation.MaximumBytes);
    }

    [Fact]
    public void Adoption_keeps_the_original_file_and_transfers_only_its_actual_size()
    {
        using var store = Store();
        using var reservation = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        var source = Source(40);
        var cleaned = 0;
        var ticket = reservation.CreateFileTicket(_user, source, "audio/flac", "sample.flac", () =>
        {
            cleaned++;
            File.Delete(source);
        });
        reservation.Dispose();
        var entry = store.Find(_user, ticket.Token);
        Assert.NotNull(entry);
        Assert.Equal(source, entry.Path);
        Assert.Equal(40, entry.Length);
        Assert.Empty(Directory.GetFiles(OwnedDirectory));
        Assert.True(File.Exists(source));
        Assert.Equal(0, cleaned);
        using var next = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        Assert.Equal(60, next.MaximumBytes);
        Assert.Throws<ObjectDisposedException>(() => reservation.CreateFileTicket(
            _user, source, "audio/flac", "sample.flac", () => { }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Invalid_artifact_length_does_not_transfer_ownership_or_release_active_reservation(int length)
    {
        using var store = Store();
        var reservation = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        var source = Source(length);
        var cleaned = 0;
        Assert.Throws<IOException>(() => reservation.CreateFileTicket(
            _user, source, "audio/flac", "sample.flac", () => cleaned++));
        Assert.Equal(0, cleaned);
        Assert.True(File.Exists(source));
        Assert.Null(store.TryReserveDownload());
        reservation.Dispose();
        using var next = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        Assert.Equal(100, next.MaximumBytes);
    }

    [Fact]
    public void Missing_artifact_does_not_consume_the_reservation()
    {
        using var store = Store();
        using var reservation = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        Assert.Throws<FileNotFoundException>(() => reservation.CreateFileTicket(
            _user, Path.Combine(_root, "missing.flac"), "audio/flac", "sample.flac", () => { }));
        var source = Source(20);
        var ticket = reservation.CreateFileTicket(_user, source, "audio/flac", "sample.flac", () => File.Delete(source));
        Assert.NotNull(store.Find(_user, ticket.Token));
        using var next = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        Assert.Equal(80, next.MaximumBytes);
    }

    [Fact]
    public void Expiry_runs_external_cleanup_and_restores_capacity()
    {
        using var store = Store();
        var (ticket, source, cleanupCount) = Adopt(store, 40);
        _clock.Now += TimeSpan.FromMinutes(15);
        store.PruneExpired();
        Assert.Null(store.Find(_user, ticket.Token));
        Assert.False(File.Exists(source));
        Assert.Equal(1, cleanupCount());
        using var next = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        Assert.Equal(100, next.MaximumBytes);
    }

    [Fact]
    public void Oldest_ticket_eviction_runs_external_cleanup()
    {
        using var store = Store();
        var oldest = Adopt(store, 10);
        for (var index = 0; index < 4; index++) Adopt(store, 10);
        Assert.Null(store.Find(_user, oldest.Ticket.Token));
        Assert.False(File.Exists(oldest.Source));
        Assert.Equal(1, oldest.CleanupCount());
        using var next = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        Assert.Equal(60, next.MaximumBytes);
    }

    [Fact]
    public void Disposal_cleans_adopted_files_once_and_invalidates_active_reservations()
    {
        var store = Store();
        var adopted = Adopt(store, 10);
        var reservation = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        store.Dispose();
        store.Dispose();
        reservation.Dispose();
        Assert.False(File.Exists(adopted.Source));
        Assert.Equal(1, adopted.CleanupCount());
        Assert.Throws<ObjectDisposedException>(() => reservation.CreateFileTicket(
            _user, Source(10), "audio/flac", "sample.flac", () => { }));
    }

    [Fact]
    public void Failed_cleanup_keeps_bytes_reserved_until_a_later_retry_succeeds()
    {
        using var store = Store();
        using var reservation = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        var source = Source(40);
        var fail = true;
        var calls = 0;
        var ticket = reservation.CreateFileTicket(_user, source, "audio/flac", "sample.flac", () =>
        {
            calls++;
            if (fail) throw new IOException("Storage is busy");
            File.Delete(source);
        });
        _clock.Now += TimeSpan.FromMinutes(15);
        store.PruneExpired();
        Assert.Null(store.Find(_user, ticket.Token));
        Assert.True(File.Exists(source));
        using (var next = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload()))
            Assert.Equal(60, next.MaximumBytes);
        fail = false;
        store.PruneExpired();
        Assert.False(File.Exists(source));
        Assert.True(calls >= 2);
        using var restored = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        Assert.Equal(100, restored.MaximumBytes);
    }

    [Fact]
    public void Abandon_releases_the_whole_reservation_after_successful_cleanup()
    {
        using var store = Store();
        var reservation = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        var calls = 0;
        reservation.Abandon(() => calls++);
        reservation.Abandon(() => throw new InvalidOperationException("Cleanup was already transferred"));
        reservation.Dispose();
        Assert.Equal(1, calls);
        using var restored = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        Assert.Equal(100, restored.MaximumBytes);
    }

    [Fact]
    public void Abandon_keeps_the_full_reservation_until_failed_cleanup_can_be_retried()
    {
        using var store = Store();
        var reservation = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        var fail = true;
        var calls = 0;
        reservation.Abandon(() =>
        {
            calls++;
            if (fail) throw new IOException("Storage is busy");
        });
        Assert.Equal(1, calls);
        Assert.Null(store.TryReserveDownload());
        fail = false;
        store.PruneExpired();
        Assert.True(calls >= 3);
        using var restored = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        Assert.Equal(100, restored.MaximumBytes);
    }

    [Fact]
    public void Disposal_after_abandon_does_not_release_pending_cleanup_bytes()
    {
        using var store = Store();
        var reservation = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        var fail = true;
        var secondCleanup = false;
        reservation.Abandon(() =>
        {
            if (fail) throw new IOException("Storage is busy");
        });
        reservation.Dispose();
        reservation.Dispose();
        reservation.Abandon(() => secondCleanup = true);
        Assert.False(secondCleanup);
        Assert.Null(store.TryReserveDownload());
        fail = false;
        store.PruneExpired();
        using var restored = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        Assert.Equal(100, restored.MaximumBytes);
    }

    private (ProviderTryMediaTicket Ticket, string Source, Func<int> CleanupCount) Adopt(ProviderTryMediaStore store, int size)
    {
        using var reservation = Assert.IsType<ProviderTryDownloadReservation>(store.TryReserveDownload());
        var source = Source(size);
        var count = 0;
        var ticket = reservation.CreateFileTicket(_user, source, "audio/flac", "sample.flac", () =>
        {
            count++;
            File.Delete(source);
        });
        return (ticket, source, () => count);
    }

    private ProviderTryMediaStore Store() => new(_clock, OwnedDirectory, 100);

    private string Source(int size)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".flac");
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
