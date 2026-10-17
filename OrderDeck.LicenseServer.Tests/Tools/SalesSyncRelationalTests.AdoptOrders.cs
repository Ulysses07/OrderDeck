using System.Net;
using FluentAssertions;
using FluentAssertions.Equivalency;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tools;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>sales-sync adopt-orders</c> (spec 2026-10-13 §4.10, §4.6 <c>attach-shipment</c>, M22, §15-44) gerçek SQL
/// Server'da. Kargo bağı bilinmeyen sipariş (<c>OriginReplicaId</c> boş) <c>attach-shipment</c> adayı olmaz; Burak
/// gerçekten gönderilmediğini doğruladıktan sonra onu verilen kopyaya benimsetir. Kuru koşu hiçbir şey yazmaz ve üç
/// kümeyi Id'leriyle basar (benimsenecek, zaten bilinen, lisansta yok); <c>--apply</c> yalnız boş olanı yazar —
/// birimlere ve <c>SyncVersion</c>'a dokunmaz, <c>ChangeSeq</c> ilerler (akış siparişi yeniden taşır). Başka lisansın
/// siparişine dokunulmaz; kopya o lisansın kopyası olmalı. Okuma ile yazma arasında küme değişirse hiçbir şey yazılmaz,
/// 1 döner. Benimsenen sipariş S2'nin <c>attach-shipment</c>'ında aday olur (§10.1-27'yi tamamlar).
/// </summary>
public sealed partial class SalesSyncRelationalTests
{
    [Fact]
    public async Task AdoptOrders_kuru_kosu_hicbir_seye_yazmaz_kumeleri_Id_ile_gosterir()
    {
        var seed = await SeedAdoptAsync();
        var before = await TableFingerprintsAsync();
        var missing = Guid.NewGuid();

        var (exit, output, error) = await AdoptAsync(new SalesSync.AdoptOrdersArgs(
            seed.Kit.LicenseId, [seed.Unknown1.Id, seed.Known.Id, seed.Foreign.Id, seed.Unknown2.Id, missing],
            seed.Kit.OwnerReplicaId, Apply: false));

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        (await TableFingerprintsAsync()).Should().Equal(before, "kuru koşu hiçbir satırı, rowversion dahil, değiştirmez");
        output.Should().Contain("KURU ÇALIŞTIRMA").And.Contain($"lisans {seed.Kit.LicenseId}")
            .And.Contain($"kopya {seed.Kit.OwnerReplicaId}");
        Section(output, "Kargo bağı bilinmeyen (OriginReplicaId boş), benimsenecek: 2")
            .Should().Contain(seed.Unknown1.Id.ToString()).And.Contain(seed.Unknown2.Id.ToString());
        Section(output, "Kargo bağı zaten bilinen, dokunulmayacak: 1").Should().Contain(seed.Known.Id.ToString());
        Section(output, "Bu lisansta bulunamayan: 2")
            .Should().Contain(seed.Foreign.Id.ToString()).And.Contain(missing.ToString());
        output.Should().Contain("hiçbir şey yazılmadı");
        output.Should().NotContain(seed.Unknown1.CustomerId).And.NotContain(seed.Unknown1.Username)
            .And.NotContain("PC-A", "çıktıda müşteri ve makine yok");
    }

    /// <summary>Bilinmeyen lisans, bu lisansta olmayan kopya (başka lisansın kopyası dahil): 2, hiçbir şey
    /// yazılmaz.</summary>
    [Fact]
    public async Task AdoptOrders_bilinmeyen_lisans_ya_da_kopya_2_doner_hicbir_sey_yazmaz()
    {
        var seed = await SeedAdoptAsync();
        var otherReplica = Guid.NewGuid();
        await seed.ForeignKit.SeedReplicaAsync(otherReplica, DateTimeOffset.UtcNow);
        var before = await TableFingerprintsAsync();

        foreach (var (license, replica, message) in new[]
                 {
                     (Guid.NewGuid(), seed.Kit.OwnerReplicaId, "Lisans bulunamadı"),
                     (seed.Kit.LicenseId, Guid.NewGuid(), "Kopya bu lisansta yok"),
                     (seed.Kit.LicenseId, otherReplica, "Kopya bu lisansta yok"),
                 })
        {
            var (exit, output, error) = await AdoptAsync(
                new SalesSync.AdoptOrdersArgs(license, [seed.Unknown1.Id], replica, Apply: true));

            exit.Should().Be(2);
            error.Should().Contain(message);
            output.Should().BeEmpty();
        }
        (await TableFingerprintsAsync()).Should().Equal(before);
    }

    [Fact]
    public async Task AdoptOrders_apply_yalniz_bos_kaynakli_siparisi_yazar_birim_ve_surume_dokunmaz()
    {
        var seed = await SeedAdoptAsync();
        var replica = seed.Kit.OwnerReplicaId;
        var ids = new[] { seed.Unknown1.Id, seed.Unknown2.Id, seed.Known.Id, seed.Foreign.Id };
        var before = await OrdersAsync(ids);

        var (exit, output, error) = await AdoptAsync(
            new SalesSync.AdoptOrdersArgs(seed.Kit.LicenseId, ids, replica, Apply: true));

        exit.Should().Be(0, error);
        output.Should().Contain("UYGULAMA").And.Contain($"UYGULANDI: 2 sipariş benimsendi, OriginReplicaId = {replica}");
        var after = await OrdersAsync(ids);
        foreach (var adopted in new[] { seed.Unknown1.Id, seed.Unknown2.Id })
        {
            after[adopted].OriginReplicaId.Should().Be(replica);
            after[adopted].ChangeSeq.Should().BeGreaterThan(before[adopted].ChangeSeq, "akış siparişi yeniden taşır");
            after[adopted].Should().BeEquivalentTo(before[adopted], o => Columns(o)
                    .Excluding(x => x.OriginReplicaId)
                    .Excluding(x => x.ChangeSeq),
                "birimler, SyncVersion ve UpdatedAt değişmez");
        }
        after[seed.Known.Id].Should().BeEquivalentTo(before[seed.Known.Id], Columns,
            "kargo bağı zaten bilinen siparişe dokunulmaz (ChangeSeq dahil)");
        after[seed.Foreign.Id].Should().BeEquivalentTo(before[seed.Foreign.Id], Columns,
            "başka lisansın siparişine dokunulmaz (ChangeSeq dahil)");
        after[seed.Foreign.Id].OriginReplicaId.Should().BeNull();

        // Yeniden koşmak etkisiz: benimsenecek bir şey kalmadı.
        var again = await AdoptAsync(new SalesSync.AdoptOrdersArgs(seed.Kit.LicenseId, ids, replica, Apply: true));
        again.Exit.Should().Be(0, again.Error);
        again.Output.Should().Contain("Benimsenecek sipariş yok");
        var unchanged = await OrdersAsync(ids);
        foreach (var id in ids)
            unchanged[id].Should().BeEquivalentTo(after[id], Columns, "ikinci koşu hiçbir şey yazmaz");
    }

    /// <summary>Siparişin kolonları (gezinme özellikleri hariç).</summary>
    private static EquivalencyAssertionOptions<Order> Columns(EquivalencyAssertionOptions<Order> options) =>
        options.Excluding(x => x.License).Excluding(x => x.Session);

    /// <summary>Okuma ile yazma arasında bir geri doldurma siparişlerden birinin kaynağını yazarsa koşullu UPDATE daha
    /// az satır bulur: işlem geri alınır, 1 döner — öbür siparişler de boş kalır, yeniden çalıştırınca yalnız kalanı
    /// benimser.</summary>
    [Fact]
    public async Task AdoptOrders_okuma_ile_yazma_arasinda_kume_degisirse_hicbir_sey_yazmaz_1_doner()
    {
        var seed = await SeedAdoptAsync();
        var backfiller = Guid.NewGuid();
        var hook = new BeforeNonQueryHook(
            when: sql => sql.Contains("[OriginReplicaId]", StringComparison.Ordinal),
            before: async () =>
            {
                await using var scope = _factory.Services.CreateAsyncScope();
                var other = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
                await other.Orders.Where(o => o.Id == seed.Unknown1.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.OriginReplicaId, backfiller));
            });
        var args = new SalesSync.AdoptOrdersArgs(
            seed.Kit.LicenseId, [seed.Unknown1.Id, seed.Unknown2.Id], seed.Kit.OwnerReplicaId, Apply: true);
        int exit;
        var output = new StringWriter();
        var error = new StringWriter();
        await using (var db = HookedDb(hook))
            exit = await SalesSync.RunAsync(db, args, output, error, default);

        hook.Ran.Should().BeTrue("kanca yazımdan önce koşmalı");
        exit.Should().Be(1);
        error.ToString().Should().Contain("yeniden çalıştır");
        output.ToString().Should().NotContain("UYGULANDI");
        var rows = await OrdersAsync([seed.Unknown1.Id, seed.Unknown2.Id]);
        rows[seed.Unknown1.Id].OriginReplicaId.Should().Be(backfiller, "eşzamanlı yazım kalır");
        rows[seed.Unknown2.Id].OriginReplicaId.Should().BeNull("işlem geri alındı; hiçbir şey yazılmadı");

        var rerun = await AdoptAsync(args);
        rerun.Exit.Should().Be(0, rerun.Error);
        rerun.Output.Should().Contain("UYGULANDI: 1 sipariş benimsendi");
        rows = await OrdersAsync([seed.Unknown1.Id, seed.Unknown2.Id]);
        rows[seed.Unknown1.Id].OriginReplicaId.Should().Be(backfiller);
        rows[seed.Unknown2.Id].OriginReplicaId.Should().Be(seed.Kit.OwnerReplicaId);
    }

    /// <summary>
    /// M22, §15-44: kargo bağı bilinmeyen sipariş <c>attach-shipment</c> adayı değildir — istemci onu beklenen kümeye
    /// koyarsa 409 <c>stale</c> (sunucunun kümesi boş). Benimsendikten sonra aynı istek onu bağlar.
    /// </summary>
    [Fact]
    public async Task AdoptOrders_sonrasi_siparis_attach_shipment_adayi_olur()
    {
        var seed = await SeedAdoptAsync();
        var customer = seed.Unknown1.CustomerId;

        var refused = await seed.Kit.SendAsync(
            seed.Kit.AttachShipment(Guid.NewGuid(), customer, Guid.NewGuid(), [seed.Unknown1]));
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());
        (await Kit.ProblemAsync(refused)).GetProperty("title").GetString().Should().Be("stale");
        (await seed.Kit.OrderAsync(seed.Unknown1.Id)).ShipmentId.Should().BeNull();

        var adopt = await AdoptAsync(new SalesSync.AdoptOrdersArgs(
            seed.Kit.LicenseId, [seed.Unknown1.Id], seed.Kit.OwnerReplicaId, Apply: true));
        adopt.Exit.Should().Be(0, adopt.Error);

        var shipmentId = Guid.NewGuid();
        var attach = await seed.Kit.SendAsync(
            seed.Kit.AttachShipment(Guid.NewGuid(), customer, shipmentId, [seed.Unknown1]));
        attach.StatusCode.Should().Be(HttpStatusCode.OK, await attach.Content.ReadAsStringAsync());
        (await seed.Kit.OrderAsync(seed.Unknown1.Id)).ShipmentId.Should().Be(shipmentId);
        (await seed.Kit.ShipmentAsync(shipmentId)).CumulativeAmount.Should().Be(seed.Unknown1.Price);
    }

    // ── Tohum ve yardımcılar ──────────────────────────────────────────────────

    private sealed record AdoptSeed(Kit Kit, Kit ForeignKit, Order Unknown1, Order Unknown2, Order Known, Order Foreign);

    /// <summary>Mod On lisansında devredilmiş bir yayın: kaynağı boş iki sipariş (ayrı müşteriler), kaynağı bilinen bir
    /// sipariş ve sahip kopya satırı; aynı yayıncının ikinci lisansında kaynağı boş bir sipariş.</summary>
    private async Task<AdoptSeed> SeedAdoptAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, DateTimeOffset.UtcNow.AddMinutes(-5));
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var unknown1 = await kit.SeedOrderAsync(session, o => o.OriginReplicaId = null);
        var unknown2 = await kit.SeedOrderAsync(session, o => o.OriginReplicaId = null);
        var known = await kit.SeedOrderAsync(session);

        var foreignKit = await kit.SecondLicenseAsync();
        var foreignSession = await foreignKit.SeedSessionAsync(Kit.Phase.Released);
        var foreign = await foreignKit.SeedOrderAsync(foreignSession, o => o.OriginReplicaId = null);
        return new AdoptSeed(kit, foreignKit, unknown1, unknown2, known, foreign);
    }

    private async Task<(int Exit, string Output, string Error)> AdoptAsync(SalesSync.AdoptOrdersArgs args)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SalesSync.RunAsync(db, args, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<Dictionary<Guid, Order>> OrdersAsync(IReadOnlyCollection<Guid> ids)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Orders.AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id);
    }

    /// <summary>Başlık satırından bir sonraki başlık (girintisiz satır) ya da metin sonuna kadarki bölüm.</summary>
    private static string Section(string output, string heading)
    {
        var start = output.IndexOf(heading + Environment.NewLine, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"\"{heading}\" satırı olmalı:{Environment.NewLine}{output}");
        var body = start + heading.Length + Environment.NewLine.Length;
        var lines = output[body..].Split(Environment.NewLine);
        return heading + Environment.NewLine
            + string.Join(Environment.NewLine, lines.TakeWhile(l => l.StartsWith(' ')));
    }
}
