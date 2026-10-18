using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api.Models;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Açılışta <c>disable</c> turunun yarıda kalmış 3. adımı (spec 2026-10-13 §8.5; W2-11): yerel kip 0 ∧ bekleyen satış
/// komutu dolu bulunursa (süreç 2. ile 3. adım arasında kapanmış) AppHost, hiçbir gönderimden önce bekleyen komutları
/// olağan yerel yazıma çevirir (<see cref="SalesModeOffRound.Resume"/>). Silinen ya da devre dışı kalan çağrı burada
/// düşer; çevirinin kendisi <c>DisableRoundConversionTests</c>'te.
/// </summary>
[Collection(AppHostBootCollection.Name)]
public sealed class DisableRoundBootTests
{
    [Fact]
    public void AppHost_acilista_yerel_kip_0_iken_kalan_bekleyen_komutlari_yerel_yazima_cevirir()
    {
        using (new global::OrderDeck.App.AppHost()) { }                 // göç
        var factory = new SqliteConnectionFactory(AppPaths.DatabaseFile);
        var customer = Guid.NewGuid().ToString("N");
        var session = Guid.NewGuid().ToString("N");
        var label = Guid.NewGuid().ToString("N");
        var commandId = Guid.NewGuid();
        new CustomerRepository(factory).Insert(new Customer(customer, "tiktok", "alici_" + customer[..8], "Takma Ad",
            null, 1, 1, false, null, null, 0, 0m, null, null, null));
        new SessionRepository(factory).Insert(new StreamSession(session, "Kendi yayını", 1000, 2000, ["tiktok"], null,
            SyncedAt: 2100));
        new LabelRepository(factory).Insert(new Label(label, session, customer, "tiktok", "alici", "yorum", "A1", 40m,
            1100, PrintedAt: null, SyncedAt: 1200));
        var intent = new SalesCommandIntent(commandId, SalesCommandKinds.Cancel,
            new SalesCancelPayload([Guid.ParseExact(label, "N")], "musteri-vazgecti").ToWireJson(), []);
        using (var c = factory.Open())
            c.Execute("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");
        new PendingSalesCommandRepository(factory).Enqueue(commandId.ToString("N"), intent.Kind,
            PendingSalesCommandCodec.Encode(intent), [label], 1300).Should().BeTrue();
        using (var c = factory.Open())
            c.Execute("UPDATE SalesSyncState SET Mode = 0 WHERE Id = 1");  // 2. adım işlendi, süreç 3. adımdan önce kapandı
        new SalesSyncStateRepository(factory).GetSeenMode().Should().Be(1, "ön koşul: tur bitmedi");

        try
        {
            using var host = new global::OrderDeck.App.AppHost();

            new PendingSalesCommandRepository(factory).List().Should().BeEmpty("3. adım açılışta yeniden koştu");
            var row = new LabelRepository(factory).GetById(label)!;
            row.CancelledAt.Should().NotBeNull("bekleyen iptal olağan yerel yazıma çevrildi");
            row.SyncedAt.Should().BeNull("kuyruğa girdi: push'la gider");
            host.Services.GetRequiredService<SalesSyncEnvelope>().SeenMode.Should().Be(0,
                "ilk hello ve state görülen kip 0 taşır");
        }
        finally
        {
            // Paylaşılan veritabanı: sonraki AppHost testleri bu satırları görmesin.
            new PendingSalesCommandRepository(factory).Delete(commandId.ToString("N"));
            using var c = factory.Open();
            c.Execute("DELETE FROM Label WHERE Id = @label", new { label });
            c.Execute("DELETE FROM StreamSession WHERE Id = @session", new { session });
            c.Execute("DELETE FROM Customer WHERE Id = @customer", new { customer });
        }
    }
}
