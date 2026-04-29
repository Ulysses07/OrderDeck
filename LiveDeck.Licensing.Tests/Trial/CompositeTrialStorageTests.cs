using FluentAssertions;
using LiveDeck.Licensing.Trial;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveDeck.Licensing.Tests.Trial;

public class CompositeTrialStorageTests
{
    private sealed class FakeStorage : ITrialStorage
    {
        public string Name { get; }
        public TrialRecord? Stored { get; set; }
        public bool ThrowOnWrite { get; set; }
        public bool ThrowOnRead { get; set; }
        public int WriteCount { get; private set; }

        public FakeStorage(string name) => Name = name;

        public TrialRecord? TryRead()
        {
            if (ThrowOnRead) throw new InvalidOperationException("read fail");
            return Stored;
        }
        public void Write(TrialRecord r)
        {
            WriteCount++;
            if (ThrowOnWrite) throw new InvalidOperationException("write fail");
            Stored = r;
        }
        public void Clear() { Stored = null; }
    }

    private static TrialRecord Sample(DateTimeOffset expiresAt) => new(
        StartedAt: expiresAt.AddDays(-14),
        ExpiresAt: expiresAt,
        HardwareFingerprint: "fp",
        Version: 1);

    [Fact]
    public void Read_returns_null_when_all_storages_empty()
    {
        var a = new FakeStorage("a");
        var b = new FakeStorage("b");
        var c = new FakeStorage("c");
        var composite = new CompositeTrialStorage(a, b, c, NullLogger<CompositeTrialStorage>.Instance);

        composite.TryRead().Should().BeNull();
    }

    [Fact]
    public void Read_returns_record_with_latest_ExpiresAt_when_storages_disagree()
    {
        var earliest = Sample(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var middle = Sample(new DateTimeOffset(2026, 5, 7, 0, 0, 0, TimeSpan.Zero));
        var latest = Sample(new DateTimeOffset(2026, 5, 13, 0, 0, 0, TimeSpan.Zero));
        var a = new FakeStorage("a") { Stored = earliest };
        var b = new FakeStorage("b") { Stored = latest };
        var c = new FakeStorage("c") { Stored = middle };
        var composite = new CompositeTrialStorage(a, b, c, NullLogger<CompositeTrialStorage>.Instance);

        var loaded = composite.TryRead();
        loaded.Should().NotBeNull();
        loaded!.ExpiresAt.Should().Be(latest.ExpiresAt);
    }

    [Fact]
    public void Read_skips_throwing_storages_and_returns_from_others()
    {
        var record = Sample(DateTimeOffset.UtcNow.AddDays(7));
        var a = new FakeStorage("a") { ThrowOnRead = true };
        var b = new FakeStorage("b") { Stored = record };
        var c = new FakeStorage("c") { ThrowOnRead = true };
        var composite = new CompositeTrialStorage(a, b, c, NullLogger<CompositeTrialStorage>.Instance);

        composite.TryRead().Should().NotBeNull();
    }

    [Fact]
    public void Write_fans_out_to_all_storages()
    {
        var a = new FakeStorage("a");
        var b = new FakeStorage("b");
        var c = new FakeStorage("c");
        var composite = new CompositeTrialStorage(a, b, c, NullLogger<CompositeTrialStorage>.Instance);

        composite.Write(Sample(DateTimeOffset.UtcNow.AddDays(14)));

        a.WriteCount.Should().Be(1);
        b.WriteCount.Should().Be(1);
        c.WriteCount.Should().Be(1);
    }

    [Fact]
    public void Write_tolerates_partial_failure()
    {
        var a = new FakeStorage("a") { ThrowOnWrite = true };
        var b = new FakeStorage("b");
        var c = new FakeStorage("c") { ThrowOnWrite = true };
        var composite = new CompositeTrialStorage(a, b, c, NullLogger<CompositeTrialStorage>.Instance);

        var act = () => composite.Write(Sample(DateTimeOffset.UtcNow.AddDays(14)));
        act.Should().NotThrow();
        b.Stored.Should().NotBeNull();
    }

    [Fact]
    public void Write_throws_when_all_storages_fail()
    {
        var a = new FakeStorage("a") { ThrowOnWrite = true };
        var b = new FakeStorage("b") { ThrowOnWrite = true };
        var c = new FakeStorage("c") { ThrowOnWrite = true };
        var composite = new CompositeTrialStorage(a, b, c, NullLogger<CompositeTrialStorage>.Instance);

        var act = () => composite.Write(Sample(DateTimeOffset.UtcNow.AddDays(14)));
        act.Should().Throw<InvalidOperationException>().WithMessage("*could not be persisted*");
    }

    [Fact]
    public void Clear_invokes_all_storages()
    {
        var a = new FakeStorage("a") { Stored = Sample(DateTimeOffset.UtcNow) };
        var b = new FakeStorage("b") { Stored = Sample(DateTimeOffset.UtcNow) };
        var c = new FakeStorage("c") { Stored = Sample(DateTimeOffset.UtcNow) };
        var composite = new CompositeTrialStorage(a, b, c, NullLogger<CompositeTrialStorage>.Instance);

        composite.Clear();

        a.Stored.Should().BeNull();
        b.Stored.Should().BeNull();
        c.Stored.Should().BeNull();
    }
}
