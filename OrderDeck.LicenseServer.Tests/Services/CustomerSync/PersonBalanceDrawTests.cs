using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;
using Rule = OrderDeck.LicenseServer.Services.CustomerSync.PersonBalance.DrawAmountRule;
using Status = OrderDeck.LicenseServer.Services.CustomerSync.PersonBalance.DrawStatus;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Çok bacaklı çekim yazımı (spec 2026-10-11 §4.2 adım 1–7, §3.2, §4.8, §4.9; <see cref="PersonBalance.DrawAsync"/>):
/// para yalnız P'den, S∖P'nin eksileri havuza sayılır ve kilitlenir; satır biçimi §3.2; kişi kilidi (M6) çekilmeyen
/// izlenen satırların jetonunu ilerletir; geri çağrı her denemenin başında. InMemory: jeton çakışması, satır açma yarışı,
/// 1205, PK ve üyelik değişimi burada kanıtlanmaz (SaveChanges atomik değil, tekil indeks yok) — Testcontainers
/// (Görev 14, 15).
/// </summary>
public sealed class PersonBalanceDrawTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceDrawTests(ApiFactory factory) => _factory = factory;

    private static PersonBalance.DrawRequest Request(
        Kit kit, Guid anchor, decimal requested, Rule rule, IReadOnlyCollection<Guid>? memberIds,
        string kind = Kit.KindDeduction, Guid? key = null, decimal? originalAmount = null, string? saleScope = null,
        string? reason = null, Guid? reverses = null) =>
        new(LicenseId: kit.LicenseId, AnchorRowId: anchor, AnchorTransactionId: key ?? Guid.NewGuid(),
            AnchorIdIsIdempotencyKey: key is not null, Kind: kind, Requested: requested, Rule: rule,
            MemberIds: memberIds, OriginalAmount: originalAmount, SaleScope: saleScope, Reason: reason,
            ReversesTransactionId: reverses, CreatedByCustomerId: kit.CustomerId, ScopeLockResource: null);

    private async Task<PersonBalance.DrawResult> DrawAsync(
        PersonBalance.DrawRequest request, Func<int, CancellationToken, Task<bool>>? beforeAttempt = null)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PersonBalance>().DrawAsync(request, beforeAttempt, default);
    }

    private static Task<int> TransactionCountAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == kit.LicenseId));

    [Fact]
    public async Task Ornek1_satirlari_birebir()
    {
        // Spec Örnek 1: T (sahip) = 0, I = 100, Y = 30; session:42, ProductTotal 120, Amount 120, anahtar K.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m, platform: "instagram");
        var y = await kit.MemberAsync(g, 30m);
        var tBefore = (await kit.BalanceRowAsync(t))!.UpdatedAt;
        var key = Guid.NewGuid();

        var result = await DrawAsync(Request(kit, t, 120m, Rule.UpToPool, [t, i, y], key: key,
            originalAmount: 120m, saleScope: Kit.Session42));

        result.Should().BeEquivalentTo(new
        {
            Status = Status.Drawn, AnchorTransactionId = key, Applied = 120m, Pool = 130m, Legs = 3,
        });
        result.RejectedRowIds.Should().BeEmpty();

        var rows = await kit.GroupRowsAsync(key);
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(r => r.DrawGroupId == key && r.Kind == Kit.KindDeduction
            && r.CreatedAt == rows[0].CreatedAt && r.CreatedByCustomerId == kit.CustomerId);
        rows[0].Should().BeEquivalentTo(new
        {
            Id = key, WpfCustomerId = t, Amount = 0m, OriginalAmount = (decimal?)120m, SaleScope = Kit.Session42,
            Reason = (string?)null, ReversesTransactionId = (Guid?)null,
        });
        rows.Single(r => r.WpfCustomerId == i).Should().BeEquivalentTo(new
        {
            Amount = -100m, OriginalAmount = (decimal?)null, SaleScope = (string?)null, Reason = (string?)null,
            ReversesTransactionId = (Guid?)null,
        });
        rows.Single(r => r.WpfCustomerId == y).Should().BeEquivalentTo(new
        {
            Amount = -20m, OriginalAmount = (decimal?)null, SaleScope = (string?)null, Reason = (string?)null,
            ReversesTransactionId = (Guid?)null,
        });

        (await kit.BalanceAsync(t)).Should().Be(0m);
        (await kit.BalanceAsync(i)).Should().Be(0m);
        (await kit.BalanceAsync(y)).Should().Be(10m);
        foreach (var row in new[] { t, i, y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        (await kit.BalanceRowAsync(t))!.UpdatedAt.Should().BeAfter(tBefore, "0 çekilen çapanın satırı da kilide girer");
    }

    [Fact]
    public async Task Capanin_bakiye_satiri_yoksa_0_ile_acilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g, balanceRow: false);
        var i = await kit.MemberAsync(g, 50m);
        (await kit.BalanceRowAsync(t)).Should().BeNull();
        var key = Guid.NewGuid();

        var result = await DrawAsync(Request(kit, t, 30m, Rule.UpToPool, [t, i], key: key));

        result.Status.Should().Be(Status.Drawn);
        result.Legs.Should().Be(2);
        var opened = await kit.BalanceRowAsync(t);
        opened.Should().NotBeNull("çapanın bakiye satırı 0 ile açılır ve kilide girer (§4.2-5)");
        opened!.Balance.Should().Be(0m);
        var rows = await kit.GroupRowsAsync(key);
        rows[0].Should().BeEquivalentTo(new { Id = key, WpfCustomerId = t, Amount = 0m });
        (await kit.BalanceAsync(i)).Should().Be(20m);
        (await kit.LedgerHoldsAsync(t)).Should().BeTrue("0'lık satır D1'i bozmaz");
        (await kit.LedgerHoldsAsync(i)).Should().BeTrue();
    }

    [Fact]
    public async Task Tek_bacakli_havuz_cekiminde_capa_DrawGroupId_kendi_Id()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 100m);
        var key = Guid.NewGuid();

        var result = await DrawAsync(Request(kit, a, 30m, Rule.UpToPool, [a], key: key));

        result.Should().BeEquivalentTo(new { Status = Status.Drawn, Applied = 30m, Pool = 100m, Legs = 1 });
        var row = (await kit.GroupRowsAsync(key)).Should().ContainSingle().Subject;
        row.Should().BeEquivalentTo(new { Id = key, WpfCustomerId = a, Amount = -30m, DrawGroupId = (Guid?)key },
            "havuz kipinde tek bacaklı çekimin de DrawGroupId'si kendi Id'si (§3.2)");
        (await kit.BalanceAsync(a)).Should().Be(70m);
    }

    [Fact]
    public async Task Eksiler_kilitlenir_cekilmez()
    {
        // Spec §8.3: A = 100, B = −60, W = {A} → havuz 40; B'nin borcu havuzu daraltır ama B'den çekilmez.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var b = await kit.MemberAsync(g, -60m);

        var result = await DrawAsync(Request(kit, a, 100m, Rule.UpToPool, [a]));

        result.Should().BeEquivalentTo(new { Status = Status.Drawn, Applied = 40m, Pool = 40m, Legs = 1 });
        (await kit.BalanceAsync(a)).Should().Be(60m);
        (await kit.BalanceAsync(b)).Should().Be(-60m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue();
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue();
    }

    [Fact]
    public async Task UpToPool_havuz_sifir_ya_da_eksi_NoBalance_yazim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 50m);
        var b = await kit.MemberAsync(g, -60m);
        // Sınır: Σ = 0 da NoBalance'tır (WPF'in kesin "bakiye yok"u, §4.2-2), NothingToApply değil.
        var g2 = Kit.NewGroup();
        var c = await kit.MemberAsync(g2, 60m);
        var d = await kit.MemberAsync(g2, -60m);
        var before = await TransactionCountAsync(kit);

        var negative = await DrawAsync(Request(kit, a, 30m, Rule.UpToPool, [a, b]));
        var zero = await DrawAsync(Request(kit, c, 30m, Rule.UpToPool, [c, d]));

        negative.Should().BeEquivalentTo(new { Status = Status.NoBalance, Applied = 0m, Pool = -10m, Legs = 0 });
        zero.Should().BeEquivalentTo(new { Status = Status.NoBalance, Applied = 0m, Pool = 0m, Legs = 0 });
        (await TransactionCountAsync(kit)).Should().Be(before);
        (await kit.BalanceAsync(a)).Should().Be(50m);
        (await kit.BalanceAsync(c)).Should().Be(60m);
    }

    [Fact]
    public async Task Requested_kurus_hassasiyetini_asarsa_firlatir_yazim_yok()
    {
        // Ön koşul: Requested en çok 2 ondalık. decimal(18,2) bacağın Amount'unu ve satırın bakiyesini ayrı ayrı
        // yuvarlardı; kuruş altı artık başka üyenin satırına taşınır, D1 bozulurdu.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var before = await TransactionCountAsync(kit);

        var act = () => DrawAsync(Request(kit, a, 10.005m, Rule.AllOrNothing, memberIds: null, kind: Kit.KindManual,
            reason: $"elle düzeltme {Guid.NewGuid():N}"));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await TransactionCountAsync(kit)).Should().Be(before);
        (await kit.BalanceAsync(a)).Should().Be(10m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
    }

    [Fact]
    public async Task UpToPool_Requested_sifir_NothingToApply()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        var before = await TransactionCountAsync(kit);

        var result = await DrawAsync(Request(kit, a, 0m, Rule.UpToPool, [a]));

        result.Should().BeEquivalentTo(new { Status = Status.NothingToApply, Applied = 0m, Pool = 50m, Legs = 0 });
        (await TransactionCountAsync(kit)).Should().Be(before);
    }

    [Fact]
    public async Task Kural_reddi_CustomerNotSynced_satirlariyla()
    {
        // Kural 3b: S'de olup W'de olmayan pozitif canlı üye.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 40m);
        var before = await TransactionCountAsync(kit);

        var result = await DrawAsync(Request(kit, a, 30m, Rule.UpToPool, [a]));

        result.Should().BeEquivalentTo(new { Status = Status.CustomerNotSynced, Applied = 0m, Legs = 0 });
        result.RejectedRowIds.Should().Equal(b);
        (await TransactionCountAsync(kit)).Should().Be(before);
        (await kit.BalanceAsync(b)).Should().Be(40m);
    }

    [Fact]
    public async Task AllOrNothing_K5_ornegi()
    {
        // Spec §4.8: A = 10, B = 40, rota A, "−30 elle düzeltme" → çapa (A, −10, Reason), bacak (B, −20).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var reason = $"elle düzeltme {Guid.NewGuid():N}";
        var request = Request(kit, a, 30m, Rule.AllOrNothing, memberIds: null, kind: Kit.KindManual, reason: reason);

        var result = await DrawAsync(request);

        result.Should().BeEquivalentTo(new { Status = Status.Drawn, Applied = 30m, Pool = 50m, Legs = 2 });
        var rows = await kit.GroupRowsAsync(request.AnchorTransactionId);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Kind == Kit.KindManual && r.DrawGroupId == request.AnchorTransactionId);
        rows[0].Should().BeEquivalentTo(new { WpfCustomerId = a, Amount = -10m, Reason = reason });
        rows[1].Should().BeEquivalentTo(new { WpfCustomerId = b, Amount = -20m, Reason = (string?)null });
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(20m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue();
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue();
    }

    [Fact]
    public async Task AllOrNothing_havuz_yetmezse_Insufficient_yazim_yok()
    {
        // Panelde P = S: C'nin eksisi Draw'da, havuz 10 + 40 − 20 = 30 < 40 → kısmi ayar yok.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var c = await kit.MemberAsync(g, -20m);
        var before = await TransactionCountAsync(kit);

        var result = await DrawAsync(Request(kit, a, 40m, Rule.AllOrNothing, memberIds: null, kind: Kit.KindManual,
            reason: $"elle düzeltme {Guid.NewGuid():N}"));

        result.Should().BeEquivalentTo(new { Status = Status.Insufficient, Applied = 0m, Pool = 30m, Legs = 0 });
        (await TransactionCountAsync(kit)).Should().Be(before);
        (await kit.BalanceAsync(a)).Should().Be(10m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
        (await kit.BalanceAsync(c)).Should().Be(-20m);
    }

    [Fact]
    public async Task Cekim_turu_iptal_ReversesTransactionId_yalniz_capada()
    {
        // Spec Örnek 4: A'ya iade R (+80), sonra A'dan 50 düşüm → A = 30; B = 60. R'nin iptali (−80) havuzdan.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var r = await kit.FundAsync(a, 80m);
        await kit.SeedLegacyDeductionAsync(a, 50m, 50m);
        var b = await kit.MemberAsync(g, 60m);
        var reason = $"Reverse of {r:N}";
        var request = Request(kit, a, 80m, Rule.AllOrNothing, memberIds: null, kind: Kit.KindReversal,
            reason: reason, reverses: r);

        var result = await DrawAsync(request);

        result.Should().BeEquivalentTo(new { Status = Status.Drawn, Applied = 80m, Pool = 90m, Legs = 2 });
        var rows = await kit.GroupRowsAsync(request.AnchorTransactionId);
        rows.Should().OnlyContain(x => x.Kind == Kit.KindReversal && x.DrawGroupId == request.AnchorTransactionId);
        rows[0].Should().BeEquivalentTo(new
        {
            WpfCustomerId = a, Amount = -30m, ReversesTransactionId = (Guid?)r, Reason = reason,
        });
        rows[1].Should().BeEquivalentTo(new
        {
            WpfCustomerId = b, Amount = -50m, ReversesTransactionId = (Guid?)null, Reason = (string?)null,
        }, "çekim türü iptalde ReversesTransactionId yalnız çapada (§3.2)");
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(10m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue();
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue();
    }

    [Fact]
    public async Task Kisi_kilidi_cekilmeyen_satirlarin_jetonunu_ilerletir()
    {
        // M6, §4.13: P'nin satırlı bütün üyeleri (Z, 0) ve S∖P'nin eksileri (B) çekilmese de jetona dokunulur.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var z = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, -60m);
        var zBefore = (await kit.BalanceRowAsync(z))!.UpdatedAt;
        var bBefore = (await kit.BalanceRowAsync(b))!.UpdatedAt;

        var result = await DrawAsync(Request(kit, a, 10m, Rule.UpToPool, [a, z]));

        result.Should().BeEquivalentTo(new { Status = Status.Drawn, Applied = 10m, Pool = 40m, Legs = 1 });
        var zAfter = (await kit.BalanceRowAsync(z))!;
        var bAfter = (await kit.BalanceRowAsync(b))!;
        zAfter.UpdatedAt.Should().BeAfter(zBefore);
        bAfter.UpdatedAt.Should().BeAfter(bBefore);
        zAfter.Balance.Should().Be(0m);
        bAfter.Balance.Should().Be(-60m);
        (await kit.BalanceAsync(a)).Should().Be(90m);
    }

    [Fact]
    public async Task beforeAttempt_true_donerse_Stopped_yazim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 100m);
        var before = await TransactionCountAsync(kit);
        var calls = new List<int>();

        var result = await DrawAsync(Request(kit, a, 30m, Rule.UpToPool, [a]), (attempt, _) =>
        {
            calls.Add(attempt);
            return Task.FromResult(true);
        });

        result.Should().BeEquivalentTo(new { Status = Status.Stopped, Applied = 0m, Legs = 0 });
        calls.Should().Equal(1);
        (await TransactionCountAsync(kit)).Should().Be(before);
        (await kit.BalanceAsync(a)).Should().Be(100m);
    }

    [Fact]
    public async Task beforeAttempt_false_donerse_cekim_surer_ilk_deneme_1()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 100m);
        var calls = new List<int>();

        var result = await DrawAsync(Request(kit, a, 30m, Rule.UpToPool, [a]), (attempt, _) =>
        {
            calls.Add(attempt);
            return Task.FromResult(false);
        });

        result.Status.Should().Be(Status.Drawn);
        calls.Should().Equal(1);
        (await kit.BalanceAsync(a)).Should().Be(70m);
    }

    [Fact]
    public void IsDuplicateTransactionKey_yalniz_PK_adini_tanir()
    {
        static DbUpdateException Save(int number, string message) =>
            new("kayıt düştü", SqlExceptionFactory.Create(number, message));

        PersonBalance.IsDuplicateTransactionKey(Save(2627,
                "Violation of PRIMARY KEY constraint 'PK_CustomerBalanceTransactions'. Cannot insert duplicate key in object 'dbo.CustomerBalanceTransactions'."))
            .Should().BeTrue("aynı anahtarla yarış: çapanın PK'sı");
        PersonBalance.IsDuplicateTransactionKey(Save(2601,
                "Cannot insert duplicate key row in object 'dbo.CustomerBalanceTransactions' with unique index 'PK_CustomerBalanceTransactions'."))
            .Should().BeTrue();

        PersonBalance.IsDuplicateTransactionKey(Save(2601,
                "Cannot insert duplicate key row in object 'dbo.CustomerBalances' with unique index 'IX_CustomerBalances_LicenseId_WpfCustomerId'."))
            .Should().BeFalse("bakiye satırı açma yarışı yeniden denenir, oynatılmaz");
        PersonBalance.IsDuplicateTransactionKey(Save(2601,
                "Cannot insert duplicate key row in object 'dbo.CustomerBalanceTransactions' with unique index 'IX_CustomerBalanceTransactions_ReversesTransactionId'."))
            .Should().BeFalse("N01 ihlali already-reversed'tir");
        PersonBalance.IsDuplicateTransactionKey(Save(547, "'PK_CustomerBalanceTransactions'"))
            .Should().BeFalse("numara da tutmalı");
        PersonBalance.IsDuplicateTransactionKey(new DbUpdateException("iç istisna yok")).Should().BeFalse();
    }
}
