using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.BroadcastPosts;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.LicenseServer.Services.Shoppers;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.Controllers.Customers;
using OrderDeck.LicenseServer.Tests.TestHelpers;

namespace OrderDeck.LicenseServer.Tests.Services.Privacy;

/// <summary>
/// Yayıncı silmesinin (KVKK) lisans medyası görevi (<see cref="LicenseMediaPurgeTask"/>). Silme niyeti lisans satırlarıyla aynı
/// SaveChanges'te kalıcı yazılır; istekteki ilk tur hiç koşmasa (iptal, yeniden başlatma, veritabanı hatası), yarıda kalsa ya da
/// silme sırasında imzalı URL'yle yeni nesne yüklense de iş lisansın dört önekini boşaltır. Görev ancak geç yazım penceresinden
/// sonra dört önek de boş görülünce kapanır.
/// </summary>
public sealed class LicenseMediaPurgeJobTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly LicenseDbContext _db = new(new DbContextOptionsBuilder<LicenseDbContext>()
        .UseInMemoryDatabase($"license-media-purge-{Guid.NewGuid():N}").Options);
    private readonly TestClock _clock = new(T0);
    private readonly CustomerPurgeMediaTests.FlakyPaymentStorage _receipts = new();
    private readonly CustomerPurgeMediaTests.FlakyWhatsAppStore _whatsApp = new();
    private readonly FakeBroadcastMediaStorage _broadcast = new();

    public void Dispose() => _db.Dispose();

    private LicenseMediaPurge Purge()
        => new(_db, _receipts, _whatsApp, _broadcast, NullLogger<LicenseMediaPurge>.Instance, _clock);

    private LicenseMediaPurgeJob Job()
        => new(_db, Purge(), NullLogger<LicenseMediaPurgeJob>.Instance, _clock);

    private static string ReceiptKey(Guid license) => $"{ShopperPaymentKeys.LicensePrefix(license)}{Guid.NewGuid():N}.pdf";
    private static string WaKey(Guid license) => $"{WhatsAppMediaKeys.LicensePrefix(license)}2026/10/{Guid.NewGuid():N}.jpg";
    // Yayın kovasının iki biçimi üretimdeki yazanlarla birebir (CustomerPurgeMediaTests'teki gibi bilerek sabit metin).
    private static string PostKey(Guid license) => $"{license}/{Guid.NewGuid()}/media.bin";
    private static string PhotoKey(Guid license) => $"{license:N}/products/{Guid.NewGuid():N}/{Guid.NewGuid():N}.img";

    private async Task PutAsync(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (key.StartsWith(WhatsAppMediaKeys.Root, StringComparison.Ordinal))
                await _whatsApp.SaveAsync(key, new byte[] { 1 }, "image/jpeg");
            else if (key.StartsWith("payments/", StringComparison.Ordinal))
                await _receipts.UploadAsync(key, new byte[] { 1 }, "application/pdf");
            else _broadcast.Seed(key, 1, "image/jpeg");
        }
    }

    private bool Exists(string key)
        => key.StartsWith(WhatsAppMediaKeys.Root, StringComparison.Ordinal) ? _whatsApp.Inner.TryGet(key, out _)
            : key.StartsWith("payments/", StringComparison.Ordinal) ? _receipts.Inner.Contains(key)
            : _broadcast.Contains(key);

    private async Task<LicenseMediaPurgeTask> SeedTaskAsync(Guid license, Guid subject, DateTimeOffset createdAt)
    {
        var task = new LicenseMediaPurgeTask
        {
            Id = Guid.NewGuid(), LicenseId = license, SubjectId = subject, CreatedAt = createdAt, NextAttemptAt = createdAt,
        };
        _db.LicenseMediaPurgeTasks.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    private Task<LicenseMediaPurgeTask> TaskAsync(Guid id)
        => _db.LicenseMediaPurgeTasks.AsNoTracking().SingleAsync(t => t.Id == id);

    [Fact]
    public async Task Istekteki_tur_hic_kosmadiysa_is_dort_oneki_siler_kaydeder_sonra_bos_gorunce_kapatir()
    {
        // Lisans satırları ve görev aynı SaveChanges'te kaydedildi, ardından istek iptal edildi ya da konteyner yeniden başladı:
        // listelenmiş anahtar listesi bellekle birlikte gitti. Görev lisansı taşıdığı için iş önekleri yeniden listeler.
        var (license, subject, other) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var task = await SeedTaskAsync(license, subject, T0);
        var mine = new[] { ReceiptKey(license), ReceiptKey(license), WaKey(license), PostKey(license), PhotoKey(license) };
        var others = new[] { ReceiptKey(other), WaKey(other), PostKey(other), PhotoKey(other) };
        await PutAsync(mine.Concat(others).ToArray());
        _clock.Now = T0.AddHours(1);

        (await Job().RunAsync(default)).Should().Be(5);

        mine.Should().OnlyContain(k => !Exists(k));
        others.Should().OnlyContain(k => Exists(k), "başka lisansın nesneleri kalır");
        var records = await _db.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Select(r => (r.Target, r.RowCount)).Should().BeEquivalentTo(new[]
        {
            (ShopperPurgeService.MediaTarget, 2), (LicenseMediaPurge.WhatsAppMediaTarget, 1),
            (LicenseMediaPurge.BroadcastPostMediaTarget, 1), (LicenseMediaPurge.ProductPhotoMediaTarget, 1),
        });
        records.Should().OnlyContain(r => r.LicenseId == license && r.SubjectId == subject
            && r.Action == DataDestruction.Actions.Deleted && r.Trigger == DataDestruction.Triggers.CustomerPurge
            && r.Criterion == LicenseMediaPurge.CustomerPurgeCriterion && r.Actor == DataDestruction.SystemActor);
        (await TaskAsync(task.Id)).CompletedAt.Should().BeNull("bu tur nesne buldu: kapanış, öneki boş gören turda");

        (await Job().RunAsync(default)).Should().Be(0);
        var closed = await TaskAsync(task.Id);
        closed.CompletedAt.Should().Be(_clock.Now);
        closed.LastError.Should().BeNull();

        _clock.Now = _clock.Now.AddDays(1);
        (await Job().RunAsync(default)).Should().Be(0);
        (await TaskAsync(task.Id)).AttemptCount.Should().Be(closed.AttemptCount, "kapanmış görev yeniden ele alınmaz");
        (await _db.DataDestructionRecords.CountAsync()).Should().Be(4);
    }

    [Fact]
    public async Task Gec_yazim_penceresinde_yuklenen_nesneyi_sonraki_tur_siler_pencere_dolmadan_gorev_kapanmaz()
    {
        // İmzalı yükleme URL'si 10 dk yaşar: silme sırasında alınmış bir URL'yle nesne, istekteki tur bittikten sonra yüklenebilir.
        var (license, subject) = (Guid.NewGuid(), Guid.NewGuid());
        var task = await SeedTaskAsync(license, subject, T0);
        var receipt = ReceiptKey(license);
        await PutAsync(receipt);

        var first = await Purge().RunAsync([task], "admin-1", default);

        first.Deleted.Should().Be(1);
        Exists(receipt).Should().BeFalse();
        (await _db.DataDestructionRecords.AsNoTracking().SingleAsync()).Actor.Should().Be("admin-1",
            "istekteki turun kaydını yapan yönetici yazar");
        var open = await TaskAsync(task.Id);
        open.CompletedAt.Should().BeNull();
        open.NextAttemptAt.Should().Be(T0 + LicenseMediaPurge.LateWriteWindow);

        var late = PostKey(license);
        _clock.Now = T0.AddMinutes(9);
        await PutAsync(late);

        _clock.Now = T0.AddMinutes(10);
        (await Job().RunAsync(default)).Should().Be(0, "pencere dolmadı: görev zamanı gelmedi");
        Exists(late).Should().BeTrue();

        _clock.Now = T0 + LicenseMediaPurge.LateWriteWindow;
        (await Job().RunAsync(default)).Should().Be(1);
        Exists(late).Should().BeFalse();
        (await _db.DataDestructionRecords.AsNoTracking().SingleAsync(r => r.Target == LicenseMediaPurge.BroadcastPostMediaTarget))
            .Should().BeEquivalentTo(new { LicenseId = (Guid?)license, SubjectId = (Guid?)subject, RowCount = 1,
                Actor = DataDestruction.SystemActor });
        (await TaskAsync(task.Id)).CompletedAt.Should().BeNull();

        _clock.Now = _clock.Now.AddHours(1);
        await Job().RunAsync(default);
        (await TaskAsync(task.Id)).CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Bos_onekte_bile_gorev_pencere_dolmadan_kapanmaz()
    {
        var task = await SeedTaskAsync(Guid.NewGuid(), Guid.NewGuid(), T0);

        await Purge().RunAsync([task], "admin-1", default);

        var open = await TaskAsync(task.Id);
        open.CompletedAt.Should().BeNull("o anda yüklenmekte olan nesne pencere içinde gelebilir");
        open.NextAttemptAt.Should().Be(T0 + LicenseMediaPurge.LateWriteWindow);
        (await _db.DataDestructionRecords.AnyAsync()).Should().BeFalse("sıfır nesne kayda yazılmaz");
    }

    [Fact]
    public async Task Silinemeyen_nesne_gorevi_acik_tutar_sonraki_tur_yeniden_dener()
    {
        var (license, subject) = (Guid.NewGuid(), Guid.NewGuid());
        var task = await SeedTaskAsync(license, subject, T0.AddHours(-1));
        var (ok, stuck, waStuck) = (ReceiptKey(license), ReceiptKey(license), WaKey(license));
        await PutAsync(ok, stuck, waStuck);
        _receipts.FailingKeys[stuck] = 0;
        _whatsApp.FailingKeys[waStuck] = 0;

        (await Job().RunAsync(default)).Should().Be(1);

        Exists(ok).Should().BeFalse();
        Exists(stuck).Should().BeTrue();
        (await _db.DataDestructionRecords.AsNoTracking().SingleAsync()).RowCount.Should().Be(1,
            "imha kaydı yalnız gerçekten silineni sayar");
        var open = await TaskAsync(task.Id);
        open.CompletedAt.Should().BeNull();
        open.LastError.Should().Contain("2 nesne silinemedi").And.Contain("kilitli");
        open.NextAttemptAt.Should().Be(T0 + LicenseMediaPurge.RetryDelay);
        (await _db.OrphanedMediaObjects.AnyAsync()).Should().BeFalse("yeniden deneme görevin: anahtar başına kuyruk satırı açılmaz");

        _clock.Now = T0.AddMinutes(10);
        (await Job().RunAsync(default)).Should().Be(0, "yeniden deneme zamanı gelmedi");

        _receipts.FailingKeys.Clear();
        _whatsApp.FailingKeys.Clear();
        _clock.Now = T0 + LicenseMediaPurge.RetryDelay;
        (await Job().RunAsync(default)).Should().Be(2);
        Exists(stuck).Should().BeFalse();
        Exists(waStuck).Should().BeFalse();
        (await TaskAsync(task.Id)).LastError.Should().BeNull();

        await Job().RunAsync(default);
        (await TaskAsync(task.Id)).CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Depo_listelenemezse_tur_hicbir_seyi_silmez_gorev_acik_kalir()
    {
        var license = Guid.NewGuid();
        var task = await SeedTaskAsync(license, Guid.NewGuid(), T0.AddHours(-1));
        var (receipt, post) = (ReceiptKey(license), PostKey(license));
        await PutAsync(receipt, post);
        _receipts.UnlistableLicenses[license] = 0;

        var result = await Purge().RunAsync([task], DataDestruction.SystemActor, default);

        result.Should().BeEquivalentTo(new LicenseMediaPurge.RoundResult(Deleted: 0, Failed: 0, Unlisted: 1));
        Exists(receipt).Should().BeTrue();
        Exists(post).Should().BeTrue("eksik listeyle tur başlamaz");
        var open = await TaskAsync(task.Id);
        open.CompletedAt.Should().BeNull();
        open.LastError.Should().Contain("listelenemiyor");
        open.NextAttemptAt.Should().Be(T0 + LicenseMediaPurge.RetryDelay);
        (await _db.DataDestructionRecords.AnyAsync()).Should().BeFalse();
    }
}
