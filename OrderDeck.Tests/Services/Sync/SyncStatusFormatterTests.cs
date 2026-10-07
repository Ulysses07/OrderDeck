using System;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Storage.Repositories;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

/// <summary>
/// Faz 0 (D2): durum satırı metni. Sıra (C7 incelemesi): önce akışın takıldığı öğe
/// (<see cref="SyncStatusTracker.BlockedOn"/>), sonra yetişme (ilk tam akış henüz yok ya da son
/// turlar akışı ilerletti ama boş sayfaya varmadı), en son "Çevrimdışı": takılı ya da yetişen
/// bilgisayar çevrimdışı gösterilmez — ama yalnız o bilgi TAZEYKEN (üç dakika). Kalıcı uyarılar
/// (D1) her durumun arkasına eklenir.
/// </summary>
public sealed class SyncStatusFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly string ItemId = Guid.NewGuid().ToString("N");
    private static string Code => ItemId[..8];

    private static SyncBlock Block(SyncBlockReason reason, DateTimeOffset lastSeen)
        => new(ItemId, reason, lastSeen.AddMinutes(-3), lastSeen);

    [Fact] public void Hic_cekilmediyse_guncelleniyor()
        => SyncStatusFormatter.Format(0, null, Now).Text.Should().Be("Güncelleniyor…");

    [Fact] public void Uzun_suredir_basari_yoksa_cevrimdisi_ve_bekleyen_sayisi()
    {
        var s = SyncStatusFormatter.Format(12, Now.AddMinutes(-10), Now);
        s.Text.Should().Be("Çevrimdışı — 12 değişiklik bekliyor");
        s.Healthy.Should().BeFalse();
    }

    [Fact] public void Bekleyen_yoksa_yalniz_cevrimdisi()
        => SyncStatusFormatter.Format(0, Now.AddMinutes(-10), Now).Text.Should().Be("Çevrimdışı");

    [Fact] public void Bekleyen_varsa_gonderiliyor()
        => SyncStatusFormatter.Format(3, Now.AddSeconds(-20), Now).Text.Should().Be("Gönderiliyor (3)");

    [Fact] public void Her_sey_gittiyse_guncel()
    {
        var s = SyncStatusFormatter.Format(0, Now.AddSeconds(-20), Now);
        s.Text.Should().StartWith("Güncel ✓");
        s.Healthy.Should().BeTrue();
    }

    [Fact] public void Atlanan_akis_ogesi_kalici_uyari_ve_sagliksiz_durum()
    {
        var s = SyncStatusFormatter.Format(0, Now.AddSeconds(-20), Now, new SyncAttention(1, 0));
        s.Text.Should().StartWith("Güncel ✓").And.EndWith(" · 1 müşteri değişikliği uygulanamadı");
        s.Healthy.Should().BeFalse();
    }

    [Fact] public void Uzlastirma_bekleyen_odeme_isi_uyarisi()
        => SyncStatusFormatter.Format(0, Now.AddSeconds(-20), Now, new SyncAttention(0, 2))
            .Text.Should().EndWith(" · 2 ödeme işi uzlaştırma bekliyor");

    [Fact] public void Iki_uyari_birlikte_bekleyen_sayisinin_arkasinda()
        => SyncStatusFormatter.Format(3, Now.AddSeconds(-20), Now, new SyncAttention(1, 2))
            .Text.Should().Be("Gönderiliyor (3) · 1 müşteri değişikliği uygulanamadı · 2 ödeme işi uzlaştırma bekliyor");

    // ── sınırlar (M-7) ve geri giden saat (M-4) ─────────────────────────

    [Fact] public void Tam_esikte_hala_guncel_esigin_otesinde_cevrimdisi()
    {
        SyncStatusFormatter.Format(0, Now - SyncStatusFormatter.OfflineAfter, Now)
            .Text.Should().StartWith("Güncel ✓", "eşik dahil taze");
        SyncStatusFormatter.Format(0, Now - SyncStatusFormatter.OfflineAfter - TimeSpan.FromTicks(1), Now)
            .Text.Should().Be("Çevrimdışı");
    }

    [Fact] public void Bir_dakikadan_fazla_gelecekteki_damga_bayat_sayilir()
    {
        // Saat geri alındı: son başarı "gelecekte" görünür — taze sayılırsa durum donardı.
        SyncStatusFormatter.Format(2, Now.AddMinutes(5), Now).Text.Should().Be("Çevrimdışı — 2 değişiklik bekliyor");
        SyncStatusFormatter.Format(0, Now.AddSeconds(30), Now).Text.Should().StartWith("Güncel ✓", "küçük kayma tolere edilir");
    }

    // ── izleme başlangıcı: hiç yetişemeyen süreç de çevrimdışı görünür (I-1) ──

    [Fact] public void Hic_yetisemeyen_surec_uc_dakikadan_sonra_cevrimdisi()
        => SyncStatusFormatter.Format(5, null, Now, trackingSince: Now.AddMinutes(-10))
            .Text.Should().Be("Çevrimdışı — 5 değişiklik bekliyor");

    [Fact] public void Izleme_yeni_basladiysa_guncelleniyor()
    {
        var s = SyncStatusFormatter.Format(5, null, Now, trackingSince: Now.AddMinutes(-1));
        s.Text.Should().Be("Güncelleniyor…");
        s.Healthy.Should().BeFalse();
    }

    // ── takılan akış: çevrimdışının önüne geçer, ama yalnız tazeyken (I-2, I-5) ──

    [Fact] public void Takilan_akis_cevrimdisinin_onune_gecer_sebep_once_kisa_kod_sonda()
    {
        var s = SyncStatusFormatter.Format(12, Now.AddMinutes(-10), Now,
            blockedOn: Block(SyncBlockReason.Stalled, Now.AddSeconds(-20)));

        s.Text.Should().Be($"Müşteri güncellemeleri bekliyor: bu bilgisayardaki bir kayıt henüz gönderilemedi (kod {Code})");
        s.Healthy.Should().BeFalse();
    }

    [Fact] public void Odeme_akisindaki_musteri_icin_bekleyen_akis()
        => SyncStatusFormatter.Format(0, Now.AddSeconds(-20), Now,
                blockedOn: Block(SyncBlockReason.Busy, Now.AddSeconds(-20)))
            .Text.Should().Be($"Müşteri güncellemeleri bekliyor: ödemesi süren bir müşteri (kod {Code})");

    [Fact] public void Takilan_akis_ilk_yetismeden_de_once_gelir()
        => SyncStatusFormatter.Format(0, null, Now, blockedOn: Block(SyncBlockReason.Busy, Now.AddSeconds(-20)))
            .Text.Should().StartWith("Müşteri güncellemeleri bekliyor");

    [Fact] public void Takilan_akisin_arkasina_kalici_uyarilar_eklenir()
        => SyncStatusFormatter.Format(0, Now.AddSeconds(-20), Now, new SyncAttention(1, 0),
                blockedOn: Block(SyncBlockReason.Stalled, Now.AddSeconds(-20)))
            .Text.Should().Be($"Müşteri güncellemeleri bekliyor: bu bilgisayardaki bir kayıt henüz gönderilemedi (kod {Code}) · 1 müşteri değişikliği uygulanamadı");

    [Fact] public void Bayat_takilma_cevrimdisini_gizlemez()
    {
        // Takılma son kez 4 dk önce görüldü; o günden beri turlar hiç sunucuya ulaşamıyor.
        SyncStatusFormatter.Format(3, Now.AddMinutes(-10), Now, blockedOn: Block(SyncBlockReason.Busy, Now.AddMinutes(-4)))
            .Text.Should().Be("Çevrimdışı — 3 değişiklik bekliyor");
    }

    [Fact] public void Takilma_esigi_cevrimdisi_esiginden_once_dolar()
        => (CustomerChangesPullHostedService.DefaultCadence * CustomerChangesPullService.BlockedRoundsBeforeWarning)
            .Should().BeLessThan(SyncStatusFormatter.OfflineAfter,
                "takılma görünür olmadan önce satır 'çevrimdışı'ya düşmesin (M-1)");

    // ── yetişme: çevrimdışı değil, ama yalnız ilerleme tazeyken ──────────

    [Fact] public void Sayfa_sinirinda_yetisen_bilgisayar_cevrimdisi_degil_guncelleniyor()
    {
        // Son tam yetişme 10 dk önce; o günden beri turlar akışı ilerletiyor ama boş sayfaya varamıyor.
        var s = SyncStatusFormatter.Format(12, Now.AddMinutes(-10), Now, catchUpProgressAt: Now.AddSeconds(-20));

        s.Text.Should().Be("Güncelleniyor…");
        s.Healthy.Should().BeFalse();
    }

    [Fact] public void Yetisirken_son_tam_yetisme_yeni_olsa_da_guncelleniyor()
        => SyncStatusFormatter.Format(0, Now.AddSeconds(-50), Now, catchUpProgressAt: Now.AddSeconds(-20))
            .Text.Should().Be("Güncelleniyor…", "akış sayfa sınırına takıldı — bilgisayar geride");

    [Fact] public void Yetisme_ilerlemesi_eskidiyse_cevrimdisina_duser()
        => SyncStatusFormatter.Format(4, Now.AddMinutes(-10), Now, catchUpProgressAt: Now.AddMinutes(-5))
            .Text.Should().Be("Çevrimdışı — 4 değişiklik bekliyor",
                "yetişme turlarından sonra bağlantı koptu — üç dakikadır ilerleme yok");

    [Fact] public void Ilk_yetisme_ilerlemesi_eskidiyse_cevrimdisina_duser()
        => SyncStatusFormatter.Format(4, null, Now, catchUpProgressAt: Now.AddMinutes(-5), trackingSince: Now.AddMinutes(-20))
            .Text.Should().Be("Çevrimdışı — 4 değişiklik bekliyor");

    [Fact] public void Yetisirken_kalici_uyarilar_eklenir()
        => SyncStatusFormatter.Format(0, null, Now, new SyncAttention(0, 1))
            .Text.Should().Be("Güncelleniyor… · 1 ödeme işi uzlaştırma bekliyor");

    // ── anlık görüntü (I-4) ─────────────────────────────────────────────

    [Fact] public void Anlik_goruntu_butun_alanlari_tasir()
    {
        var snapshot = new SyncStatusSnapshot(
            LastPullOkAt: Now.AddMinutes(-10),
            LastCatchUpProgressAt: null,
            BlockedOn: Block(SyncBlockReason.Busy, Now.AddSeconds(-20)),
            TrackingSince: Now.AddMinutes(-30));

        SyncStatusFormatter.Format(1, snapshot, Now, new SyncAttention(0, 1))
            .Text.Should().Be($"Müşteri güncellemeleri bekliyor: ödemesi süren bir müşteri (kod {Code}) · 1 ödeme işi uzlaştırma bekliyor");
        SyncStatusFormatter.Format(1, snapshot with { BlockedOn = null }, Now)
            .Text.Should().Be("Çevrimdışı — 1 değişiklik bekliyor");
        SyncStatusFormatter.Format(1, snapshot with { BlockedOn = null, LastPullOkAt = null }, Now)
            .Text.Should().Be("Çevrimdışı — 1 değişiklik bekliyor", "izleme 30 dk önce başladı, hiç yetişilmedi");
    }
}
