using System;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Storage.Repositories;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

/// <summary>
/// Faz 0 (D2): durum satırı metni. Sıra (C7 incelemesi): önce akışın takıldığı öğe
/// (<see cref="SyncStatusTracker.BlockedOn"/>), sonra yetişme (ilk tam akış henüz yok ya da son
/// turlar sayfa sınırına takıldı — <see cref="CustomerPullOutcome.MorePending"/>), en son
/// "Çevrimdışı": takılı ya da yetişen bilgisayar çevrimdışı gösterilmez. Kalıcı uyarılar
/// (D1) her durumun arkasına eklenir.
/// </summary>
public sealed class SyncStatusFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly string ItemId = Guid.NewGuid().ToString("N");

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

    // ── takılan akış ve yetişme: çevrimdışı gösterilmez (C7 incelemesi) ──

    [Fact] public void Takilan_akis_cevrimdisinin_onune_gecer_ve_ogeyi_soyler()
    {
        var s = SyncStatusFormatter.Format(12, Now.AddMinutes(-10), Now,
            blockedOn: new SyncBlock(ItemId, SyncBlockReason.Stalled, Now.AddMinutes(-4)));

        s.Text.Should().Be($"Akış {ItemId} için bekliyor: gönderilemeyen yerel kopya");
        s.Healthy.Should().BeFalse();
    }

    [Fact] public void Odeme_akisindaki_musteri_icin_bekleyen_akis()
        => SyncStatusFormatter.Format(0, Now.AddSeconds(-20), Now,
                blockedOn: new SyncBlock(ItemId, SyncBlockReason.Busy, Now.AddMinutes(-4)))
            .Text.Should().Be($"Akış {ItemId} için bekliyor: müşteri ödeme akışında");

    [Fact] public void Takilan_akis_ilk_yetismeden_de_once_gelir()
        => SyncStatusFormatter.Format(0, null, Now,
                blockedOn: new SyncBlock(ItemId, SyncBlockReason.Busy, Now.AddMinutes(-4)))
            .Text.Should().StartWith($"Akış {ItemId} için bekliyor");

    [Fact] public void Takilan_akisin_arkasina_kalici_uyarilar_eklenir()
        => SyncStatusFormatter.Format(0, Now.AddSeconds(-20), Now, new SyncAttention(1, 0),
                blockedOn: new SyncBlock(ItemId, SyncBlockReason.Stalled, Now.AddMinutes(-4)))
            .Text.Should().Be($"Akış {ItemId} için bekliyor: gönderilemeyen yerel kopya · 1 müşteri değişikliği uygulanamadı");

    [Fact] public void Sayfa_sinirinda_yetisen_bilgisayar_cevrimdisi_degil_guncelleniyor()
    {
        // Son tam yetişme 10 dk önce; o günden beri turlar sayfa sınırına takılıyor (büyük akış).
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

    [Fact] public void Yetisirken_kalici_uyarilar_eklenir()
        => SyncStatusFormatter.Format(0, null, Now, new SyncAttention(0, 1))
            .Text.Should().Be("Güncelleniyor… · 1 ödeme işi uzlaştırma bekliyor");
}
