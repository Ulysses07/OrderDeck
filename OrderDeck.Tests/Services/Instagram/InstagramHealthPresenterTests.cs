using FluentAssertions;
using OrderDeck.App.Services.Instagram;
using OrderDeck.App.ViewModels;
using OrderDeck.Chat.Ingestors.Instagram;
using Severity = OrderDeck.App.Services.Instagram.InstagramStatusSeverity;
using State = OrderDeck.Chat.Ingestors.Instagram.InstagramAccountState;

namespace OrderDeck.Tests.Services.Instagram;

/// <summary>
/// Instagram sağlık şeridinin görünüm kuralları (çoklu hesap tasarımı §8.5 "Görünüm kuralları", §8.6.3, §8.6.4).
///
/// NEDEN: izleyici yalnız olgu tutar; operatörün gördüğü her şey (satır metni, renk, şeridin çıkıp çıkmayacağı,
/// üst noktanın sararıp sararmayacağı) bu saf işlevde türetilir. Bir eşik ya da kapı yanlışsa susan hesap sağlam
/// görünür ya da Instagram'ı hiç kullanmayan yayıncının önüne her gece sarı bir şerit çıkar. Saat parametredir:
/// testler beklemez, ağa çıkmaz. Kimlik, Sayfa adı ve kullanıcı adı literal yazılmaz, üretilir (depo herkese
/// açık); arayüz metinleri ise bilerek literal'dir (üretim sabitine başvuran test metin değişikliğini yakalamaz).
/// </summary>
public sealed class InstagramHealthPresenterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 21, 14, 7, TimeSpan.FromHours(3));   // Review Focus 4: +03:00; girdilerin saatleri UTC
    private const string Fatal = "Instagram bağlantını yenilemen gerekiyor.";
    private const string Reauth = "Ayarlar > Facebook > \"Facebook izinlerini güncelle\".";
    private const string NoAnswer = "Instagram'dan yanıt alınamıyor, yeniden deneniyor. Yorumlar gecikiyor olabilir.";

    private static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>Now'dan <paramref name="seconds"/> saniye sonrası (eksi = öncesi), UTC'de: izleyici saatleri UTC yazar.</summary>
    private static DateTimeOffset FromNow(double seconds) => Now.AddSeconds(seconds).ToUniversalTime();

    /// <summary>Girdi kurar; <paramref name="named"/> false ise Username null. Süreler saniyedir: sinceAgo ve contactAgo
    /// Now'dan geriye, retryIn Now'dan ileriye (eksi = geçmiş); saat alanları UTC'ye çevrilir.</summary>
    private static InstagramAccountHealth H(State state, double sinceAgo = 30, double? contactAgo = null, double? retryIn = null,
        bool wasLive = false, bool primary = true, string? detail = null, bool named = true)
        => new(New("p"), New("Sayfa"), primary, named ? New("hesap") : null, state, detail,
            FromNow(-sinceAgo), contactAgo is { } c ? FromNow(-c) : null, retryIn is { } r ? FromNow(r) : null, wasLive);

    private static InstagramAccountHealth Healthy(double sinceAgo = 300, bool primary = true) => H(State.Live, sinceAgo, contactAgo: 1, wasLive: true, primary: primary);
    private static InstagramHealthView Eval(params InstagramAccountHealth[] snapshot) => InstagramHealthPresenter.Evaluate(snapshot, Now, sessionActive: true);
    private static InstagramAccountStatusViewModel Row(params InstagramAccountHealth[] snapshot) => Eval(snapshot).Rows.Single();
    private static InstagramAccountStatusViewModel At(InstagramAccountHealth h, string state, Severity severity, string? tip)        // beklenen satır, `@ad` etiketli
        => new(h.PageId, $"@{h.Username}", state, $"@{h.Username} {state}", severity, tip);
    private static InstagramAccountStatusViewModel Paged(InstagramAccountHealth h, string state, Severity severity, string? tip)     // beklenen satır, Sayfa adı etiketli
        => new(h.PageId, h.PageName, state, $"{h.PageName}: {state}", severity, tip);

    [Fact] public void Canli_bekleyen_baglanan_ve_yayini_biten_hesabin_satiri()
    {
        var (live, waiting) = (Healthy(), H(State.WaitingForBroadcast, contactAgo: 5));
        var (starting, ended) = (H(State.Starting, sinceAgo: 5), H(State.BroadcastEnded, sinceAgo: 240, contactAgo: 5, wasLive: true));
        Row(live).Should().Be(At(live, "canlı", Severity.Live, "Son yanıt 1 sn önce"));
        Row(waiting).Should().Be(At(waiting, "yayın bekleniyor", Severity.Neutral, "Bu hesapta canlı yayın açılınca yorumlar akmaya başlar."));
        Row(starting).Should().Be(At(starting, "bağlanıyor", Severity.Neutral, null));
        Row(ended).Should().Be(At(ended, "yayın bitti (21:10)", Severity.Neutral, null));
    }
    [Fact] public void Sorun_durumlarinin_satiri_ve_ipucu()                  // tek Sayfa + bu oturumda canlıydı → sarı
    {
        var silent = H(State.Live, sinceAgo: 300, contactAgo: 18, wasLive: true);
        var limited = H(State.RateLimited, contactAgo: 30, retryIn: 353, wasLive: true);               // 21:20:00
        var faulted = H(State.Faulted, retryIn: 293, wasLive: true, detail: Fatal);                     // 21:19:00
        var unresolved = H(State.ResolveFailed, retryIn: 293, wasLive: true);
        Row(silent).Should().Be(At(silent, "yanıt yok (18 sn)", Severity.Warning, NoAnswer));
        Row(limited).Should().Be(At(limited, "hız sınırı, 21:20'de dener", Severity.Warning,
            "Instagram istek sınırına takıldı. Bu hesabın yorumları o saate kadar gelmeyecek."));
        Row(faulted).Should().Be(At(faulted, "bağlantı yenilenmeli", Severity.Warning, $"{Fatal} {Reauth} 21:19'da yeniden denenecek."));
        Row(unresolved).Should().Be(Paged(unresolved, "hesap bilgisi okunamadı, 21:19'da dener", Severity.Warning,
            $"Sayfaya bağlı Instagram hesabı okunamadı. Sürerse: {Reauth}"));
    }
    [Fact] public void RetryAt_yoksa_saat_parcasi_yazilmaz_kullanici_adi_yoksa_etiket_Sayfa_adidir()
    {
        Row(H(State.RateLimited, contactAgo: 5, wasLive: true)).StateText.Should().Be("hız sınırı");
        Row(H(State.ResolveFailed, wasLive: true)).StateText.Should().Be("hesap bilgisi okunamadı");
        Row(H(State.Faulted, wasLive: true, detail: Fatal)).Tooltip.Should().Be($"{Fatal} {Reauth}");
        var unnamed = H(State.Starting, sinceAgo: 5, named: false);
        Row(unnamed).Should().Be(new InstagramAccountStatusViewModel(unnamed.PageId, unnamed.PageName, "bağlanıyor", $"{unnamed.PageName} bağlanıyor", Severity.Neutral, null));
    }
    [Fact] public void Saat_metni_now_ofsetine_cevrilir()                    // Review Focus 4: makinenin saat dilimi değil, now'ın ofseti; ek de o duvar saatinden
    {
        var now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.FromHours(5.5)); var at = now.AddSeconds(-30).ToUniversalTime();
        var limited = H(State.RateLimited, wasLive: true) with { Since = at, LastContactAt = at, RetryAt = now.AddMinutes(10).ToUniversalTime() };
        InstagramHealthPresenter.Evaluate(new[] { limited }, now, sessionActive: true).Rows.Single().StateText.Should().Be("hız sınırı, 09:10'da dener");   // an Locative'e doğrudan verilirse UTC'de de +03:00'te de dakika 40 okunur: 'ta
    }
    [Fact] public void Live_ama_10_sn_den_eski_temasli_hesap_canli_sayilmaz()
    {
        Eval(H(State.Live, sinceAgo: 300, contactAgo: 10, wasLive: true)).AnyAccountLive.Should().BeTrue();
        var stale = Eval(H(State.Live, sinceAgo: 300, contactAgo: 11, wasLive: true));
        stale.AnyAccountLive.Should().BeFalse(); stale.Rows.Single().StateText.Should().Be("yanıt yok (11 sn)");
    }
    [Theory, InlineData(State.WaitingForBroadcast), InlineData(State.BroadcastEnded), InlineData(State.Starting)]
    public void Bekleyen_durum_45_sn_den_uzun_sessizlikte_yanit_yok_olur_sure_son_temas_ile_Since_ten_gec_olandan_olculur(State state)
    {
        Row(H(state, sinceAgo: 300, contactAgo: 45, wasLive: true)).StateText.Should().NotStartWith("yanıt yok");
        Row(H(state, sinceAgo: 300, contactAgo: 46, wasLive: true)).StateText.Should().Be("yanıt yok (46 sn)");
        Row(H(state, sinceAgo: 44, contactAgo: 600, wasLive: true)).StateText.Should().NotStartWith("yanıt yok");   // duruma yeni geçildi: eski temas sayılmaz
        Row(H(state, sinceAgo: 46, contactAgo: 600, wasLive: true)).StateText.Should().Be("yanıt yok (46 sn)");
    }
    [Theory, InlineData(State.RateLimited), InlineData(State.Faulted), InlineData(State.ResolveFailed), InlineData(State.NotLinked)]
    public void RetryAt_i_45_sn_den_fazla_gecilmis_hesap_yanit_yok_olur(State state)
    {
        Row(H(state, sinceAgo: 400, retryIn: -45, wasLive: true)).StateText.Should().NotStartWith("yanıt yok");
        var late = H(state, sinceAgo: 400, retryIn: -46, wasLive: true);
        Row(late).Should().Be(At(late, "yanıt yok (46 sn)", Severity.Warning, NoAnswer));
    }
    [Fact] public void Tek_Sayfada_sorun_durumu_yalniz_hesap_bu_oturumda_canliydiysa_saridir()
    {
        var wasLive = Eval(H(State.Faulted, retryIn: 270, wasLive: true, detail: Fatal));
        wasLive.Rows.Single().Severity.Should().Be(Severity.Warning);
        wasLive.Should().BeEquivalentTo(new { ShowStrip = true, TopDotTooltip = $"Instagram {wasLive.Rows[0].Label}: bağlantı yenilenmeli." });
        var never = Eval(H(State.Faulted, retryIn: 270, detail: Fatal));             // hiç canlı olmadı: aynı metin, nötr, şerit gizli
        never.Rows.Single().Should().BeEquivalentTo(new { StateText = "bağlantı yenilenmeli", Severity = Severity.Neutral });
        never.Should().BeEquivalentTo(new { ShowStrip = false, TopDotTooltip = (string?)null });
    }
    [Theory, InlineData(State.RateLimited, 30.0), InlineData(State.Starting, 46.0), InlineData(State.Faulted, 30.0), InlineData(State.ResolveFailed, 30.0), InlineData(State.NotLinked, 30.0)]
    public void Iki_Sayfada_sorun_durumu_hic_canli_olmamis_hesapta_da_saridir(State state, double sinceAgo)   // kapı yok; Starting 46 sn temassız → "yanıt yok"
    {
        var view = Eval(H(state, sinceAgo, retryIn: 270), H(state, sinceAgo, retryIn: 270, primary: false));
        view.Rows.Should().HaveCount(2).And.OnlyContain(r => r.Severity == Severity.Warning);
        view.Should().BeEquivalentTo(new { ShowStrip = true, TopDotTooltip = $"Instagram: 2 hesapta sorun ({view.Rows[0].Label}, {view.Rows[1].Label})." });
    }
    [Fact] public void Canli_yayin_bulunamadi_90_sn_bekleme_ile_oteki_hesabin_canliligindan_gec_olandan_olculur()
    {
        InstagramAccountStatusViewModel Waiting(double waitingFor, double otherLiveFor)        // öteki hesap otherLiveFor sn'dir canlı
            => Eval(Healthy(otherLiveFor), H(State.WaitingForBroadcast, waitingFor, contactAgo: 5, primary: false)).Rows[1];
        Waiting(89, 300).StateText.Should().Be("yayın bekleniyor");
        Waiting(300, 89).StateText.Should().Be("yayın bekleniyor");
        Waiting(300, 91).StateText.Should().Be("canlı yayın bulunamadı");
        Waiting(91, 300).Should().BeEquivalentTo(new { StateText = "canlı yayın bulunamadı", Severity = Severity.Warning,
            Tooltip = "Diğer hesap yayında ama bu hesapta canlı yayın görünmüyor. Bu hesapta yayını açtın mı?" });
        Eval(Healthy(200), Healthy(50, primary: false), H(State.WaitingForBroadcast, sinceAgo: 300, contactAgo: 5, primary: false))
            .Rows[2].StateText.Should().Be("canlı yayın bulunamadı");        // üç hesap: canlılardan EN ERKEN canlı olan (200 sn'lik) esas alınır
    }
    [Fact] public void Yayin_kesildi_yalniz_baska_hesap_canliyken_saridir_ust_noktayi_ilk_2_dakika_sarartir()
    {
        var early = Eval(Healthy(), H(State.BroadcastEnded, sinceAgo: 119, contactAgo: 5, wasLive: true, primary: false));
        early.Rows[1].Should().BeEquivalentTo(new { StateText = "yayın kesildi (21:12)", Severity = Severity.Warning,
            Tooltip = "Bu hesabın yayını kapandı; yorumları gelmiyor. Diğer hesap yayında." });
        early.TopDotTooltip.Should().Be($"Instagram {early.Rows[1].Label}: yayın kesildi (21:12).");
        var late = Eval(Healthy(), H(State.BroadcastEnded, sinceAgo: 121, contactAgo: 5, wasLive: true, primary: false));
        late.Rows[1].Severity.Should().Be(Severity.Warning); late.TopDotTooltip.Should().BeNull();       // satır sarı kalır
    }
    [Fact] public void Canli_yayin_bulunamadi_ust_noktayi_sarartmaz_baska_sari_durum_sarartir()           // TK20
    {
        var missing = H(State.WaitingForBroadcast, sinceAgo: 120, contactAgo: 5, primary: false);
        var only = Eval(Healthy(), missing);
        only.Rows[1].Severity.Should().Be(Severity.Warning);
        only.Should().BeEquivalentTo(new { ShowStrip = true, TopDotTooltip = (string?)null });
        var limited = H(State.RateLimited, contactAgo: 30, retryIn: 300, primary: false);                // 21:19:07
        Eval(Healthy(), missing, limited).TopDotTooltip.Should().Be($"Instagram @{limited.Username}: hız sınırı, 21:19'da dener.");
    }
    [Fact] public void NotLinked_tek_Sayfada_canli_olmadiysa_gizlidir_canliydiysa_ve_iki_Sayfada_saridir_ipucu_birincilde_ve_ekte_ayridir()
    {
        var hidden = Eval(H(State.NotLinked, retryIn: 270)); hidden.Rows.Should().BeEmpty(); hidden.ShowStrip.Should().BeFalse();
        Eval(H(State.NotLinked, sinceAgo: 400, retryIn: -46)).Rows.Should().BeEmpty();                   // gizleme "yanıt yok"tan önce
        var wasLive = Eval(H(State.NotLinked, retryIn: 270, wasLive: true));
        wasLive.Rows.Single().Severity.Should().Be(Severity.Warning); wasLive.ShowStrip.Should().BeTrue();
        var (primary, extra) = (H(State.NotLinked, retryIn: 270), H(State.NotLinked, retryIn: 270, primary: false));   // iki Sayfa: birincil dahil sarı
        Eval(primary, extra).Rows.Should().Equal(
            Paged(primary, "Instagram hesabı bağlı değil", Severity.Warning, "Birincil Sayfaya bağlı Instagram hesabı görünmüyor. Sayfa ile Instagram " +
                "hesabının bağını kontrol et; bağ duruyorsa Ayarlar > Facebook > \"Facebook izinlerini güncelle\"ye basıp tüm hesapları işaretle."),
            Paged(extra, "Instagram hesabı bağlı değil", Severity.Warning, "Bu Sayfaya bir Instagram profesyonel hesabı bağla ya da Sayfayı Ayarlar'dan kaldır."));
    }
    [Theory, InlineData(State.WaitingForBroadcast), InlineData(State.BroadcastEnded)]
    public void Yanit_yok_taban_durumun_satirindan_once_gelir(State state)
    {
        var silent = H(state, sinceAgo: 300, contactAgo: 46, wasLive: true, primary: false);
        var view = Eval(Healthy(), silent);
        view.Rows[1].StateText.Should().Be("yanıt yok (46 sn)");
        view.TopDotTooltip.Should().Be($"Instagram @{silent.Username}: yanıt yok (46 sn).");
    }
    [Fact] public void Oteki_hesap_yanitsizken_bagimli_satirlar_notre_doner_donunce_sure_yeniden_sayilmaz()
    {
        var ended = H(State.BroadcastEnded, sinceAgo: 60, contactAgo: 5, wasLive: true, primary: false);
        var waiting = H(State.WaitingForBroadcast, sinceAgo: 200, contactAgo: 5, primary: false);
        var stalled = Eval(H(State.Live, sinceAgo: 300, contactAgo: 11, wasLive: true), ended, waiting);
        stalled.Rows.Select(r => r.StateText).Should().Equal("yanıt yok (11 sn)", "yayın bitti (21:13)", "yayın bekleniyor");
        stalled.Rows.Select(r => r.Severity).Should().Equal(Severity.Warning, Severity.Neutral, Severity.Neutral);
        Eval(Healthy(300), ended, waiting).Rows.Select(r => r.StateText)                                 // aynı Since: süre baştan sayılmaz
            .Should().Equal("canlı", "yayın kesildi (21:13)", "canlı yayın bulunamadı");
    }
    [Fact] public void Skipped_ve_Stopped_seride_girmez_ama_Sayfa_sayisina_sayilir()
    {
        Row(H(State.Faulted, retryIn: 270, detail: Fatal), H(State.Skipped, primary: false)).Severity.Should().Be(Severity.Warning);   // 2 Sayfa: kapı yok
        var healthy = Eval(Healthy(), H(State.Skipped, primary: false));
        healthy.Rows.Should().ContainSingle(); healthy.ShowStrip.Should().BeFalse();                     // şeride giren hesap 1, sarı yok
        var stopped = Eval(H(State.Stopped), H(State.Stopped, primary: false));
        stopped.Rows.Should().BeEmpty(); stopped.Should().BeEquivalentTo(new { ShowStrip = false, AnyAccountLive = false });
    }
    [Fact] public void Serit_oturum_acikken_iki_hesapta_ya_da_sari_satirda_gorunur()
    {
        Eval(Healthy()).ShowStrip.Should().BeFalse();                                                     // tek hesap sağlıklı
        Eval(H(State.Live, sinceAgo: 300, contactAgo: 11, wasLive: true)).ShowStrip.Should().BeTrue();    // tek hesap sarı
        Eval(Healthy(), Healthy(primary: false)).ShowStrip.Should().BeTrue();                             // iki hesap
        var noSession = InstagramHealthPresenter.Evaluate(new[] { Healthy(), Healthy(primary: false) }, Now, sessionActive: false);
        noSession.Rows.Should().HaveCount(2);                                                             // oturum yalnız şeridi kapılar
        noSession.Should().BeEquivalentTo(new { ShowStrip = false, AnyAccountLive = true });
    }
    [Fact] public void Oturum_yokken_sari_satir_seridi_acmaz_ust_nokta_ipucu_yine_hesaplanir()
    {
        var silent = H(State.Live, sinceAgo: 300, contactAgo: 11, wasLive: true);
        InstagramHealthPresenter.Evaluate(new[] { silent }, Now, sessionActive: false)
            .Should().BeEquivalentTo(new { ShowStrip = false, TopDotTooltip = $"Instagram @{silent.Username}: yanıt yok (11 sn)." });
    }
    [Theory, InlineData(State.Starting, 46.0, "yanıt yok (46 sn)"), InlineData(State.RateLimited, 30.0, "hız sınırı, 21:18'de dener"),
        InlineData(State.ResolveFailed, 30.0, "hesap bilgisi okunamadı, 21:18'de dener")]
    public void Tek_Sayfada_hic_canli_olmamis_hesabin_sorun_satiri_ayni_metinle_notrdur_serit_ve_ust_nokta_sararmaz(State state, double sinceAgo, string text)
    {
        var view = Eval(H(state, sinceAgo, retryIn: 270));                                                // 21:18:37; Starting 46 sn temassız → "yanıt yok"
        view.Rows.Single().Should().BeEquivalentTo(new { StateText = text, Severity = Severity.Neutral });
        view.Should().BeEquivalentTo(new { ShowStrip = false, TopDotTooltip = (string?)null });
    }
    [Fact] public void Doksan_saniye_ve_iki_dakika_esiginin_kendisi_asim_degildir()
    {
        Eval(Healthy(), H(State.WaitingForBroadcast, sinceAgo: 90, contactAgo: 5, primary: false)).Rows[1].StateText.Should().Be("yayın bekleniyor");
        var ended = H(State.BroadcastEnded, sinceAgo: 120, contactAgo: 5, wasLive: true, primary: false);
        Eval(Healthy(), ended).TopDotTooltip.Should().Be($"Instagram @{ended.Username}: yayın kesildi (21:12).");   // tam 2 dakika: üst nokta hâlâ sarı
    }
    [Fact] public void Saniyeler_tam_saniyedir_kesir_atilir()
    {
        Row(H(State.Live, sinceAgo: 300, contactAgo: 1.9, wasLive: true)).Tooltip.Should().Be("Son yanıt 1 sn önce");
        Row(H(State.Live, sinceAgo: 300, contactAgo: 18.9, wasLive: true)).StateText.Should().Be("yanıt yok (18 sn)");
        Row(H(State.RateLimited, sinceAgo: 400, retryIn: -46.9, wasLive: true)).StateText.Should().Be("yanıt yok (46 sn)");
    }
    [Fact] public void Aciklama_ya_da_kullanici_adi_bos_metinse_yok_sayilir()
    {
        Row(H(State.Faulted, retryIn: 293, wasLive: true)).Tooltip.Should().Be($"{Reauth} 21:19'da yeniden denenecek.");   // açıklama yok: sabit cümleyle başlar
        Row(H(State.Faulted, wasLive: true, detail: "")).Tooltip.Should().Be(Reauth);
        var blank = H(State.Starting, sinceAgo: 5) with { Username = "" };
        Row(blank).Should().Be(new InstagramAccountStatusViewModel(blank.PageId, blank.PageName, "bağlanıyor", $"{blank.PageName} bağlanıyor", Severity.Neutral, null));
    }
    [Fact] public void Saat_geri_alinirsa_canli_ipucu_eksi_saniye_yazmaz()
    {
        Row(H(State.Live, sinceAgo: 300, contactAgo: -3, wasLive: true)).Tooltip.Should().Be("Son yanıt 0 sn önce");   // son temas now'dan 3 sn ileride
    }
    [Fact] public void En_erken_canli_hesap_anlik_goruntude_ilk_sirada_olmasa_da_esas_alinir()
    {
        Eval(Healthy(50), Healthy(200, primary: false), H(State.WaitingForBroadcast, sinceAgo: 300, contactAgo: 5, primary: false))
            .Rows[2].StateText.Should().Be("canlı yayın bulunamadı");        // ilk sıradaki 50 sn'lik canlı esas alınsaydı 90 sn dolmazdı
    }
    [Fact] public void Atlanan_ve_duran_girdi_eskise_de_RetryAt_tasisa_da_satir_uretmez()
    {
        Eval(Healthy(), H(State.Skipped, sinceAgo: 400, retryIn: -46, primary: false)).Rows.Should().ContainSingle();   // atlanan Sayfa RetryAt taşır: o saat 45 sn'den fazla geçilse de "yanıt yok" olmaz
        Eval(H(State.Stopped, sinceAgo: 400, contactAgo: 300, wasLive: true), H(State.Stopped, sinceAgo: 400, primary: false)).Rows.Should().BeEmpty();   // duran girdi eskir: eski temas "yanıt yok" üretmez
    }
    [Fact] public void Aciklama_ya_da_kullanici_adi_yalniz_bosluksa_yok_sayilir()
    {
        var blank = H(State.Starting, sinceAgo: 5) with { Username = " " };
        Row(blank).Label.Should().Be(blank.PageName);
        Row(H(State.Faulted, wasLive: true, detail: " ")).Tooltip.Should().Be(Reauth);
    }
}
