using System;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.Core.Storage.Repositories;
using Xunit;

namespace OrderDeck.Tests.Services;

// ── Birleştirme notunun kipi (ortak bakiye spec 2026-10-11 §6.1-d, M3, §8.4-4; W-havuz) ─────────────────────
//
// Not yalnız iade başka bir satırın düşümüne dokunuyorsa (RefundsToOtherRow) çıkar; metnini PaymentPrompt.PersonPool
// seçer. Sunucu izlerinin sorusunda kaynak aynı akışın scope yanıtlarıdır. Hepsi aynıysa o değer, karışıksa null; eski
// sunucu alanı göndermez, değer false olur. Yerel planın sorusu sunucu çağrısından önce kurulur: her zaman null, önceki
// akışların yanıtı bellekte tutulmaz.
public partial class PaymentRequestServiceTests
{
    private const string PoolNoteNotrNot =
        " Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; iade o hesaba döner.";

    private const string PoolNoteSatirKipiNotu =
        " Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; iade o hesaba döner, bu istekte kullanılmaz.";

    /// <summary>İki üyeli kişi; sunucu kapsamda iki ayrı üyede düşüm bulur (emsal
    /// <c>Kapsam_izi_iki_uyedeyse_sorulur_onayda_yenisi_iade_edilir</c>). İzlerin <c>personPool</c> alanı verilen
    /// değerlerle kurulur; null olan izde alan hiç yoktur (eski sunucu). Döner: onaysız ilk çağrının birleştirme sorusu.</summary>
    private async Task<PaymentRequestOutcome> IkiIzliSoruAsync(bool? izA, bool? izB)
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}");
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            var (txA, txB) = (Guid.NewGuid(), Guid.NewGuid());
            var t0 = DateTimeOffset.UtcNow.AddHours(-2);
            handler.ScopeResponseFor = id =>
                id == G(a) ? ScopeJsonAt(txA, 40m, 250m, t0, izA)
                : id == G(b) ? ScopeJsonAt(txB, 40m, 250m, t0.AddHours(1), izB)
                : null;

            var soru = await sut.RequestPaymentAsync(a, _ => 250m, T, "session:s1");

            soru.Result.Should().Be(PaymentRequestResult.MergeRequired);
            soru.Prompt!.RefundsToOtherRow.Should().BeTrue("izler tanım gereği farklı üyelerde");
            soru.Prompt.RemoteDeductions.Should().Equal(40m, 40m);
            handler.ReverseCalls.Should().BeEmpty("onaydan önce geri alma yok");
            _launcher.LaunchedUrls.Should().BeEmpty();
            return soru;
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-d: scope yanıtları havuz kipini söylüyor — iade birleşik istekte kullanılır, not yok
    public async Task Sunucu_izlerinin_sorusu_scope_personPool_true_ise_not_yok()
    {
        var soru = await IkiIzliSoruAsync(true, true);

        soru.Prompt!.PersonPool.Should().BeTrue("kaynak aynı akışın scope yanıtları — kesin bilgi");
        var (_, message) = PaymentResultPresenter.PromptText(soru.Prompt, DateTimeOffset.Now);
        message.Should().EndWith("kişinin toplamı tek istekte yeniden istenir.").And.NotContain("Not:");
    }

    [Fact] // §6.1-d: satır kipindeki sunucu personPool: false döner — bugünkü not aynen
    public async Task Sunucu_izlerinin_sorusu_satir_kipinde_bugunku_not()
    {
        var soru = await IkiIzliSoruAsync(false, false);

        soru.Prompt!.PersonPool.Should().BeFalse();
        PaymentResultPresenter.PromptText(soru.Prompt, DateTimeOffset.Now).Message
            .Should().EndWith(PoolNoteSatirKipiNotu);
    }

    [Fact] // §4.3 + §6.1-d: S1 öncesi sunucu scope yanıtında personPool alanını göndermez → DTO varsayılanı false → bugünkü not
    public async Task Eski_sunucu_izlerinde_alan_yok_bugunku_not()
    {
        var soru = await IkiIzliSoruAsync(null, null);

        soru.Prompt!.PersonPool.Should().BeFalse("DTO varsayılanı: alan yoksa satır kipi");
        PaymentResultPresenter.PromptText(soru.Prompt, DateTimeOffset.Now).Message
            .Should().EndWith(PoolNoteSatirKipiNotu);
    }

    [Fact] // Plan fark 11: izler farklı kip taşıyorsa (akış ortasında enable/disable) bilinmiyor — nötr not
    public async Task Karisik_izlerde_PersonPool_null()
    {
        var soru = await IkiIzliSoruAsync(true, false);

        soru.Prompt!.PersonPool.Should().BeNull("iki kipte de doğru metin seçilir");
        PaymentResultPresenter.PromptText(soru.Prompt, DateTimeOffset.Now).Message
            .Should().EndWith(PoolNoteNotrNot);
    }

    // §6.1-d (+ spec §14 #28): yerel planın sorusu hiçbir sunucu çağrısından önce kurulur → her zaman null. Aynı servis
    // örneğinde başka bir kişinin akışı havuz kipinin scope yanıtını (personPool: true) görmüş olsa da değer bellekte
    // tutulmaz: enable/disable oturum ortasında kipi değiştirebilir. Kurulum Birlesen_kisinin_parali_iki_isi_… ile aynı.
    [Fact]
    public async Task Yerel_planin_sorusunda_PersonPool_her_zaman_null()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}");
            var c = Uye(db, $"kisi-{Guid.NewGuid():N}");
            var d = Uye(db, $"kisi-{Guid.NewGuid():N}");
            var (sut, handler) = KisiSut(db);
            handler.RowBalances = new() { [G(a)] = 50m, [G(c)] = 50m };

            (await sut.OpenWhatsAppAsync(a, 100m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            (await sut.OpenWhatsAppAsync(c, 200m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            Kisi(db, a, c);

            // Başka bir kişinin (d) akışı havuz kipinin scope yanıtını görür: tek iz, benimsenir.
            var tx = Guid.NewGuid();
            handler.ScopeResponseFor = id =>
                id == G(d) ? ScopeJsonAt(tx, 40m, 250m, DateTimeOffset.UtcNow, personPool: true) : null;
            (await sut.OpenWhatsAppAsync(d, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            var sorgular = handler.ScopeQueries.Count;

            var soru = await sut.RequestPaymentAsync(a, _ => 300m, T, "session:s1");

            soru.Result.Should().Be(PaymentRequestResult.MergeRequired);
            soru.Prompt!.RemoteDeductions.Should().BeNull("yerel planın sorusu (PromptOf), sunucu izlerininki değil");
            soru.Prompt.RefundsToOtherRow.Should().BeTrue("kaybedenin düşümü kalan işin satırında değil");
            soru.Prompt.PersonPool.Should().BeNull("yerel plan kipi bilmez; önceki akışın personPool'u tutulmaz");
            handler.ScopeQueries.Should().HaveCount(sorgular, "yerel planın sorusu sunucu çağrısından önce döner");
            PaymentResultPresenter.PromptText(soru.Prompt, DateTimeOffset.Now).Message
                .Should().EndWith(PoolNoteNotrNot).And.NotContain("bu istekte kullanılmaz");
        }
        finally { Drop(db); }
    }
}
