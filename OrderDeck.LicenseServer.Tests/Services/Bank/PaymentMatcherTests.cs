using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Sentetik açıklamalar demo'daki maskeli kalıplardan türetildi; gerçek ad/IBAN yok.</summary>
public sealed class PaymentMatcherTests
{
    private static LicenseDbContext NewDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"matcher-{Guid.NewGuid():N}");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    /// <summary>Yalnız tohum verisinin IBAN hash'ini üretir; eşleştirici hasher istemez (hash'ler hazır saklanır).</summary>
    private static readonly BankHasher Hasher = new(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));

    private static PaymentMatcher Matcher(LicenseDbContext db, params string[] excluded)
        => new(db, Options.Create(new BankOptions { ExcludedTransactionCodes = excluded.Length == 0 ? ["CCP"] : excluded }),
            NullLogger<PaymentMatcher>.Instance);

    private static WpfCustomerProjection Customer(LicenseDbContext db, Guid lic, string username, string? fullName = null, string? displayName = null,
        string? groupId = null, string platform = "youtube")
    {
        var c = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = platform, Username = username, FullName = fullName, DisplayName = displayName, GroupId = groupId, UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.Add(c); db.SaveChanges();
        return c;
    }

    /// <summary>Üretilmiş 64 hex IBAN özeti (InMemory'de denetim yok; biçim gerçeğinin aynısı).</summary>
    private static string NewHash() => $"{Guid.NewGuid():N}{Guid.NewGuid():N}";

    /// <summary>Id'si "N" biçiminde ordinal en küçük satır: kişi birden çok satırıyla vurulunca önerilen satır (S2b planı Karar 1).</summary>
    private static WpfCustomerProjection SmallestN(params WpfCustomerProjection[] rows)
        => rows.MinBy(r => r.Id.ToString("N"), StringComparer.Ordinal)!;

    private static BankTransaction Incoming(LicenseDbContext db, Guid lic, string description, string? ibanHash = null, string code = "FT",
        string currency = "TL")
    {
        var t = new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = 500m, Currency = currency, OccurredAt = DateTimeOffset.UtcNow,
            Description = description, TransactionCode = code, CounterpartyIbanHash = ibanHash, FetchedAt = DateTimeOffset.UtcNow,
        };
        db.BankTransactions.Add(t); db.SaveChanges();
        return t;
    }

    private static void IbanMemory(LicenseDbContext db, Guid lic, Guid customerId, string hash, DateTimeOffset? lastConfirmedAt = null)
    {
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = customerId, IbanHash = hash, IbanMasked = "TR..", LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = DateTimeOffset.UtcNow, LastConfirmedAt = lastConfirmedAt });
        db.SaveChanges();
    }

    /// <summary>Asıl kayda birleştirilmiş kopya: yönlendirmedir, varsayılan sorgulardan gizli.</summary>
    private static WpfCustomerProjection Copy(LicenseDbContext db, Guid lic, Guid into, bool provisional = false, bool purged = false)
    {
        var c = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "kopya_hesap", MergedIntoId = into, CreatedByShopper = provisional, PurgedAt = purged ? DateTimeOffset.UtcNow : null, UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.Add(c); db.SaveChanges();
        return c;
    }

    [Fact]
    public async Task Aciklamadaki_kullanici_adi_tek_adayi_bulur()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var ayse = Customer(db, lic, "ornek_musteri34"); Customer(db, lic, "mehmet_k");
        var tx = Incoming(db, lic, "HAVALE - ORNEK MUSTERI34 - siparis");

        var m = await Matcher(db).MatchAsync(tx, CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.Proposed);
        m.ProposedWpfCustomerId.Should().Be(ayse.Id);
        m.Layer.Should().Be(PaymentMatchLayer.UsernameInDescription);
        m.Confidence.Should().Be(0.90m);
        m.Evidence.Should().Contain("ornekmusteri34");
    }

    [Fact]
    public async Task Bitisik_yazim_da_eslesir_alt_dize_ile()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "ornek.musteri");
        var tx = Incoming(db, lic, "EFT GELEN ornekmusteri odeme");
        (await Matcher(db).MatchAsync(tx, CancellationToken.None)).ProposedWpfCustomerId.Should().Be(c.Id);
    }

    [Fact]
    public async Task Kisa_kullanici_adi_yalniz_tam_token_ile_eslesir()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "ali");
        var tx1 = Incoming(db, lic, "HAVALE ali odeme");
        var tx2 = Incoming(db, lic, "HAVALE alim satim");
        (await Matcher(db).MatchAsync(tx1, CancellationToken.None)).ProposedWpfCustomerId.Should().Be(c.Id);
        (await Matcher(db).MatchAsync(tx2, CancellationToken.None)).Status.Should().Be(PaymentMatchStatus.NoProposal, "'alim' içinde 'ali' alt dize sayılmaz");
    }

    [Fact]
    public async Task Bos_ya_da_cok_kisa_anahtarli_kullanici_adi_hicbir_harekete_eslesmez()
    {
        // Emoji, Latin dışı yazı ve noktalama anahtarı boşaltır — boş anahtar her açıklamanın "içinde" bulunurdu.
        // NFKD uyumluluk ayrıştırması sembolleri kısa ASCII token'a çevirir: "№"→"no", "™"→"tm", "①"→"1",
        // "Ⅳ"→"iv", "ª"→"a", "½"→"1 2". Açıklama bu token'ların hepsini taşır.
        string[] adlar = [char.ConvertFromUtf32(0x1F600), "Иван", "Ωμεγα", "علي", "___", "№", "™", "①", "Ⅳ", "ª", "½", "ab"];
        foreach (var ad in adlar)
        {
            using var db = NewDb(); var lic = Guid.NewGuid();
            Customer(db, lic, ad);
            var m = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE No 1 IV TM a 12 ab odeme"), CancellationToken.None);

            m.Status.Should().Be(PaymentMatchStatus.NoProposal, $"'{ad}' kullanıcı adı eşleşme anahtarı üretmemeli");
            m.Evidence.Should().Be("no-signal", $"'{ad}' aday bile sayılmamalı");
        }
    }

    [Fact]
    public async Task Harf_rakam_siniri_aciklamada_bitisik_olsa_da_eslesir()
    {
        // Kullanıcı adı harf/rakam sınırında bölünür ("can_1a" → can,1,a), açıklama token'ı "1a" ise bölünmez;
        // karşılaştırma açıklama token'ını da aynı kuralla böler.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "can_1a");

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE can 1a odeme"), CancellationToken.None);

        m.ProposedWpfCustomerId.Should().Be(c.Id);
        m.Layer.Should().Be(PaymentMatchLayer.UsernameInDescription);
    }

    [Fact]
    public async Task Parca_eslesmesi_aciklama_tokeninin_ortasinda_bitmez()
    {
        // "ali_34" anahtarı 5 harf (alt dize araması yok); "ali34x" başka bir kullanıcı adıdır, içinde bulunmaz.
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ali_34");

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE ali34x odeme"), CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.NoProposal);
        m.Evidence.Should().Be("no-signal");
    }

    [Theory]
    [InlineData("emeali", "HAVALE DENEME ALICI")] // iki ayrı kelimeye yayılır
    [InlineData("aramus", "FAST ORNEK ARA MUSTERI")] // soyadının ortasında biter
    [InlineData("musteri", "EFT ORNEK MUSTERILER")] // soyadının başı
    [InlineData("denemealici", "HAVALE DENEME ALICILAR")]
    [InlineData("musteri12", "HAVALE ORNEK MUSTERI 1234 TL")] // tutar/referans rakamına taşar
    [InlineData("ayse34", "HAVALE AYSE 3450")]
    [InlineData("123456", "FAST REF 99123456")] // referans numarasının içi
    public async Task Bitisik_yazim_eslesmesi_aciklama_token_sinirinda_baslar_ve_biter(string kullaniciAdi, string aciklama)
    {
        // Türkçe adlar kısa hece dizileri; açıklama gönderenin tam adını referans ve tutarın yanında taşır.
        // Sınırsız alt dize bunları 0.90 öneriye çevirirdi — tam ad eşleşmesinden (0.50) bile yüksek.
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, kullaniciAdi);

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, aciklama), CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.NoProposal);
        m.Evidence.Should().Be("no-signal");
    }

    [Theory]
    [InlineData("ornekmusteri", "EFT ORNEK MUSTERI odeme")] // kullanıcı adı bitişik, açıklama ayrık
    [InlineData("denemealici", "HAVALE DENEME ALICI odeme")]
    [InlineData("musteri12", "HAVALE ORNEK MUSTERI 12 TL")]
    public async Task Bitisik_yazim_ardisik_tam_tokenlarin_birlesimiyse_eslesir(string kullaniciAdi, string aciklama)
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, kullaniciAdi);

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, aciklama), CancellationToken.None);

        m.ProposedWpfCustomerId.Should().Be(c.Id);
        m.Layer.Should().Be(PaymentMatchLayer.UsernameInDescription);
    }

    [Theory]
    [InlineData("123456", "FAST 1.234,56 TL")] // tutar noktalamada bölünür (1,234,56), bitişik token kuralı yeniden birleştirir
    [InlineData("12032026", "12.03.2026 EFT")] // tarih: 12,03,2026
    [InlineData("2026", "HAVALE 12.03.2026 siparis")] // tarihin yılı tam token
    [InlineData("1000", "FAST 1000,00 TL")] // tutarın tam kısmı tam token
    [InlineData("havale", "HAVALE ayse odeme")] // banka kalıbı: lisansın hemen her açıklamasında geçer
    [InlineData("fast", "FAST GELEN odeme")]
    [InlineData("gelen", "EFT GELEN aciklamasiz")]
    [InlineData("odeme", "HAVALE ODEME")]
    [InlineData("gelen_havale", "EFT GELEN HAVALE")] // yalnız kalıp kelimelerinden oluşur
    [InlineData("fast1000", "FAST 1000,00 TL")] // kalıp + rakam: o tutardaki her FAST'a taşardı
    public async Task Harfsiz_ya_da_yalniz_banka_kalibi_kullanici_adi_katman_bire_girmez(string kullaniciAdi, string aciklama)
    {
        // Böyle tek bir müşteri lisansın her ilgili hareketini 0.90 yanlış öneriye çevirirdi.
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, kullaniciAdi);

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, aciklama), CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.NoProposal);
        m.Evidence.Should().Be("no-signal");
    }

    [Fact]
    public async Task Harfsiz_ya_da_kalip_kullanici_adli_musteri_gercek_adayi_gizlemez_iban_hafizasiyla_bulunur()
    {
        // Gerçek müşteri de eşleşince böyle bir müşteri satırı belirsizliğe (öneri yok) çevirir, doğru öneriyi gizlerdi.
        // Katman 1 dışında kalır ama aday listesinde kalır: IBAN hafızası onu yine gösterir.
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "havale"); var rakam = Customer(db, lic, "123456"); var ayse = Customer(db, lic, "ornek_musteri34");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        IbanMemory(db, lic, rakam.Id, hash);

        var gercek = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE ornek_musteri34 FAST 1.234,56 TL"), CancellationToken.None);
        var iban = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE FAST 1.234,56 TL", hash), CancellationToken.None);

        gercek.Status.Should().Be(PaymentMatchStatus.Proposed);
        gercek.ProposedWpfCustomerId.Should().Be(ayse.Id);
        gercek.Evidence.Should().Be("username=ornekmusteri34");
        iban.ProposedWpfCustomerId.Should().Be(rakam.Id);
        iban.Layer.Should().Be(PaymentMatchLayer.IbanMemory);
        iban.Confidence.Should().Be(0.85m);
    }

    [Fact]
    public async Task Birden_fazla_aday_oneri_uretmez()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ornek_musteri"); Customer(db, lic, "ornek_musteri34");
        var tx = Incoming(db, lic, "HAVALE ornek musteri 34");

        var m = await Matcher(db).MatchAsync(tx, CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.NoProposal);
        m.Evidence.Should().StartWith("ambiguous:");
    }

    /// <summary>
    /// A5b: kopya (MergedIntoId dolu) asıl kaydın kullanıcı adını taşır ama
    /// müşteri değil, yönlendirmedir. Aday sayılsaydı birleşmiş HER kişi için
    /// açıklamadaki kullanıcı adı iki adaya çarpar ve "belirsiz" denirdi.
    /// </summary>
    [Fact]
    public async Task Ayni_kullanici_adinda_asil_ve_kopya_varken_asil_kayit_onerilir()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var ayse = Customer(db, lic, "ornek_musteri34");
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "Ornek_Musteri34",
            MergedIntoId = ayse.Id, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        var tx = Incoming(db, lic, "HAVALE - ORNEK MUSTERI34 - siparis");

        var m = await Matcher(db).MatchAsync(tx, CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.Proposed);
        m.ProposedWpfCustomerId.Should().Be(ayse.Id);
        m.Layer.Should().Be(PaymentMatchLayer.UsernameInDescription);
    }

    [Fact]
    public async Task Iban_hafizasi_ikinci_katman_ve_ikisi_birlikte_yuksek_guven()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "zeynep_d");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        IbanMemory(db, lic, c.Id, hash);

        var onlyIban = await Matcher(db).MatchAsync(Incoming(db, lic, "EFT GELEN aciklamasiz", hash), CancellationToken.None);
        var both = await Matcher(db).MatchAsync(Incoming(db, lic, "EFT GELEN zeynep_d", hash), CancellationToken.None);

        onlyIban.Layer.Should().Be(PaymentMatchLayer.IbanMemory); onlyIban.Confidence.Should().Be(0.85m);
        onlyIban.ProposedWpfCustomerId.Should().Be(c.Id);
        both.Layer.Should().Be(PaymentMatchLayer.UsernameInDescription); both.Confidence.Should().Be(0.98m);
        both.ProposedWpfCustomerId.Should().Be(c.Id);
    }

    [Fact]
    public async Task Iban_hafizasi_kullanici_adiyla_celisirse_oneri_yok()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ornek_musteri34"); var b = Customer(db, lic, "mehmet_k");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        IbanMemory(db, lic, b.Id, hash);

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE ornek_musteri34", hash), CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.NoProposal);
        m.ProposedWpfCustomerId.Should().BeNull();
        m.Evidence.Should().Contain("conflict");
    }

    [Fact]
    public async Task Ad_benzerligi_yalniz_dusuk_guvenli_oneri()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "xk_77", fullName: "Deneme Alıcı");
        var tx = Incoming(db, lic, "T.GARANTI BANKASI /IBAN MERKEZ SUBESI gonderilen havale DENEME ALICI odeme");

        var m = await Matcher(db).MatchAsync(tx, CancellationToken.None);

        m.Layer.Should().Be(PaymentMatchLayer.NameAmount);
        m.ProposedWpfCustomerId.Should().Be(c.Id);
        m.Confidence.Should().Be(0.50m);
        // Ad kişisel veri: KVKK silmesi projeksiyondaki adı siler, kanıta dokunmaz. Kanıt yalnız katmanı ve
        // eşleşen token sayısını taşır; ad, admin sayfasında önerilen müşteriden (silinmemişse) okunur.
        m.Evidence.Should().Be("name:2");
    }

    /// <summary>
    /// Masaüstünün R3-02 yedeği (form adı yoksa FullName'e takma adı yazma)
    /// kalkınca: formu doldurmamış müşteride FullName boş kalır, platform
    /// takma adı DisplayName'de ayrı gelir. Ad katmanı bu durumda DisplayName'i
    /// denemeli — yoksa bankadan gelen gerçek ada dayalı öneri hiç kurulamazdı.
    /// </summary>
    [Fact]
    public async Task Ad_bossa_takma_adla_eslesir()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "xk_77", displayName: "Deneme Alıcı");
        var tx = Incoming(db, lic, "T.GARANTI BANKASI /IBAN MERKEZ SUBESI gonderilen havale DENEME ALICI odeme");

        var m = await Matcher(db).MatchAsync(tx, CancellationToken.None);

        m.Layer.Should().Be(PaymentMatchLayer.NameAmount);
        m.ProposedWpfCustomerId.Should().Be(c.Id);
        m.Confidence.Should().Be(0.50m);
        m.Evidence.Should().Be("name:2");
    }

    /// <summary>FullName doluyken DisplayName de ayrıca dolu ve FARKLIYSA, ad katmanı
    /// yine gerçek adı kullanmalı — takma adın geçtiği bir açıklama eşleşmemeli.</summary>
    [Fact]
    public async Task Ad_doluysa_takma_ad_yerine_gercek_ad_kullanilir()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "xk_77", fullName: "Deneme Alıcı", displayName: "Baska Takma Ad");
        var tx = Incoming(db, lic, "T.GARANTI BANKASI /IBAN MERKEZ SUBESI gonderilen havale DENEME ALICI odeme");

        var m = await Matcher(db).MatchAsync(tx, CancellationToken.None);

        m.Layer.Should().Be(PaymentMatchLayer.NameAmount);
        m.ProposedWpfCustomerId.Should().Be(c.Id);
        m.Evidence.Should().Be("name:2");
    }

    [Fact]
    public async Task Baska_lisansin_ayni_kullanici_adi_ve_iban_hafizasi_aday_olmaz()
    {
        using var db = NewDb(); var lisA = Guid.NewGuid(); var lisB = Guid.NewGuid();
        var bMusterisi = Customer(db, lisB, "ornek_musteri34", fullName: "Deneme Alıcı");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        IbanMemory(db, lisB, bMusterisi.Id, hash);
        // Tek örnek: aday listesi lisans başına bir kez okunur; B'nin listesi A'nın hareketine sızmamalı.
        var matcher = Matcher(db);

        var b = await matcher.MatchAsync(Incoming(db, lisB, "HAVALE ornek_musteri34 DENEME ALICI", hash), CancellationToken.None);
        var a = await matcher.MatchAsync(Incoming(db, lisA, "HAVALE ornek_musteri34 DENEME ALICI", hash), CancellationToken.None);

        b.ProposedWpfCustomerId.Should().Be(bMusterisi.Id);
        a.Status.Should().Be(PaymentMatchStatus.NoProposal);
        a.ProposedWpfCustomerId.Should().BeNull();
        a.Evidence.Should().Be("no-signal");
    }

    [Fact]
    public async Task Silinmis_musteri_aday_olmaz_iban_hafizasi_da_onu_onermez()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "ornek_musteri34", fullName: "Deneme Alıcı");
        c.PurgedAt = DateTimeOffset.UtcNow; db.SaveChanges();
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        IbanMemory(db, lic, c.Id, hash);

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE ornek_musteri34 DENEME ALICI", hash), CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.NoProposal);
        m.ProposedWpfCustomerId.Should().BeNull();
        m.Evidence.Should().Be("no-signal");
    }

    [Fact]
    public async Task Ayni_ornek_ardisik_hareketlerde_dogru_musteriyi_bulur()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var ayse = Customer(db, lic, "ornek_musteri34"); var mehmet = Customer(db, lic, "mehmet_k");
        var matcher = Matcher(db);

        (await matcher.MatchAsync(Incoming(db, lic, "HAVALE ornek_musteri34"), CancellationToken.None)).ProposedWpfCustomerId.Should().Be(ayse.Id);
        (await matcher.MatchAsync(Incoming(db, lic, "HAVALE mehmet_k"), CancellationToken.None)).ProposedWpfCustomerId.Should().Be(mehmet.Id);
    }

    [Fact]
    public async Task Kanit_ham_aciklama_iban_ve_hash_icermez()
    {
        // Kanıt sütunu 180 gün saklanır ve admin sayfasında görünür: yalnız katman adı + kullanıcı adı taşır, ad taşımaz.
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ornek_musteri34"); var b = Customer(db, lic, "mehmet_k", fullName: "Deneme Alıcı");
        var iban = BankHasherTests.TestIban();
        var hash = Hasher.HashIban(iban)!;
        IbanMemory(db, lic, b.Id, hash);
        (string Aciklama, string? Hash, PaymentMatchLayer Katman)[] durumlar =
        [
            ($"HAVALE ornek_musteri34 {iban} siparis", hash, PaymentMatchLayer.None), // çelişki: kullanıcı adı a, IBAN b
            ($"EFT {iban} aciklamasiz", hash, PaymentMatchLayer.IbanMemory),
            ($"HAVALE ornek_musteri34 mehmet_k {iban}", null, PaymentMatchLayer.None), // belirsiz
            ($"HAVALE DENEME ALICI {iban}", null, PaymentMatchLayer.NameAmount),
        ];

        foreach (var (aciklama, ibanHash, katman) in durumlar)
        {
            var m = await Matcher(db).MatchAsync(Incoming(db, lic, aciklama, ibanHash), CancellationToken.None);
            m.Layer.Should().Be(katman, aciklama);
            m.Evidence.Should().NotBeNullOrEmpty();
            m.Evidence.Should().NotContain(iban).And.NotContain(iban.ToLowerInvariant()).And.NotContain(iban[2..]).And.NotContain(hash);
            m.Evidence.Should().NotContain(BankTextNormalizer.Normalize(aciklama)).And.NotContain(BankTextNormalizer.Tokenize(aciklama).Joined);
            m.Evidence.Should().NotContain("deneme").And.NotContain("alici");
        }
    }

    [Fact]
    public async Task Pos_kodu_dislanir()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ornek_musteri34");
        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "Firma:1234 (ornek_musteri34) POS", code: "CCP"), CancellationToken.None);
        m.Status.Should().Be(PaymentMatchStatus.NoProposal);
        m.Evidence.Should().Be("excluded:CCP");
    }

    [Fact]
    public async Task Dislanan_kod_listesi_tekrarli_ve_buyuk_kucuk_harf_duyarsiz()
    {
        // Binder varsayılan ["CCP"]'nin sonuna ekler: liste tekrar taşıyabilir.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "ornek_musteri34");

        var pos = await Matcher(db, "CCP", "CCP", "pos").MatchAsync(Incoming(db, lic, "HAVALE ornek_musteri34", code: "POS"), CancellationToken.None);
        var ft = await Matcher(db, "CCP", "CCP", "pos").MatchAsync(Incoming(db, lic, "HAVALE ornek_musteri34", code: "FT"), CancellationToken.None);

        pos.Status.Should().Be(PaymentMatchStatus.NoProposal);
        pos.Evidence.Should().Be("excluded:POS");
        ft.ProposedWpfCustomerId.Should().Be(c.Id);
    }

    [Fact]
    public async Task Giden_ya_da_sifir_tutarli_hareket_dislanir()
    {
        // Çekim işi bunları eşleştiriciye vermez; yine de verilirse öneri üretilmez.
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ornek_musteri34");
        var giden = Incoming(db, lic, "HAVALE ornek_musteri34"); giden.Direction = BankTransactionDirection.Outgoing;
        var sifir = Incoming(db, lic, "HAVALE ornek_musteri34"); sifir.Amount = 0m;
        db.SaveChanges();

        var g = await Matcher(db).MatchAsync(giden, CancellationToken.None);
        var s = await Matcher(db).MatchAsync(sifir, CancellationToken.None);

        g.Status.Should().Be(PaymentMatchStatus.NoProposal); g.ProposedWpfCustomerId.Should().BeNull();
        g.Evidence.Should().Be("excluded:direction");
        s.Status.Should().Be(PaymentMatchStatus.NoProposal); s.ProposedWpfCustomerId.Should().BeNull();
        s.Evidence.Should().Be("excluded:zero");
    }

    [Theory]
    [InlineData("TL")]
    [InlineData("TRY")]
    [InlineData("try")]
    [InlineData(" TL")]
    public async Task TL_ve_TRY_degerlendirilir(string currency)
    {
        // (koruma) Obifin değeri kırpar ama harfini normalleştirmez: kural da kırpar, harfe bakmaz.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "ornek_musteri34");

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE ornek_musteri34", currency: currency), CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.Proposed);
        m.ProposedWpfCustomerId.Should().Be(c.Id);
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    [InlineData("")]
    public async Task TL_disi_para_birimi_dislanir(string currency)
    {
        // Tasarım §3: para birimi TL/TRY olmayan hareket müşteri havalesi sayılmaz. Açıklamadaki kullanıcı adı öneri üretmez;
        // boş değer (ObifinClient boşu "TL" yapar, başka bir yoldan gelmiş) de dışlanır.
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ornek_musteri34");
        var tx = Incoming(db, lic, "HAVALE ornek_musteri34", currency: currency);
        var matcher = Matcher(db);

        var m = await matcher.MatchAsync(tx, CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.NoProposal); m.ProposedWpfCustomerId.Should().BeNull();
        m.Evidence.Should().Be($"excluded:{PaymentMatcher.CurrencyExclusion}").And.Be("excluded:currency", "kanıt biçimi sözleşmedir");
        matcher.IsExcluded(tx).Should().BeTrue();
    }

    // ── Kişi düzeyi (S2b; tasarım §2 "Kişi", §2.4): kişi aynı GroupId'yi (ham değer, ordinal) paylaşan asıl kayıtlardır.

    [Fact]
    public async Task Ayni_kisinin_iki_satiri_ayni_kullanici_adiyla_tek_oneri_ve_surum_2()
    {
        // Satır düzeyinde aynı kişinin IG ve TT satırı "ambiguous:2" üretir: havale hiç sorulmadan "Eşleşmeyen"e düşerdi.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var group = $"G-{Guid.NewGuid():N}";
        var ig = Customer(db, lic, "ornek_musteri34", groupId: group, platform: "instagram");
        var tt = Customer(db, lic, "ornek_musteri34", groupId: group, platform: "tiktok");

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE ornek_musteri34"), CancellationToken.None);

        (m.Status, m.ProposedWpfCustomerId, m.Layer, m.Confidence, m.Evidence).Should().Be((PaymentMatchStatus.Proposed,
            (Guid?)SmallestN(ig, tt).Id, PaymentMatchLayer.UsernameInDescription, PaymentMatcher.UsernameConfidence,
            "username=ornekmusteri34"));
        m.MatcherVersion.Should().Be(PaymentMatcher.PersonLevelVersion).And.Be(2, "sürüm sözleşmedir: boş = satır düzeyi, 2 = kişi düzeyi");
        m.SupportedByIbanMemory.Should().BeFalse();

        // Vurulan satırlar arasında IBAN bağının satırı varsa o önerilir (Karar 1): bağ "N" sırasında büyük olan satırda.
        var bonded = ReferenceEquals(SmallestN(ig, tt), ig) ? tt : ig;
        var hash = NewHash();
        IbanMemory(db, lic, bonded.Id, hash);
        var withIban = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE ornek_musteri34", hash), CancellationToken.None);
        (withIban.ProposedWpfCustomerId, withIban.Confidence, withIban.Evidence, withIban.SupportedByIbanMemory).Should().Be(
            ((Guid?)bonded.Id, PaymentMatcher.BothConfidence, "username=ornekmusteri34;iban-memory", true));

        // Belirsizlik kişi sayar, satır değil: aynı kullanıcı adı üç satırda, iki kişide.
        var other = Guid.NewGuid();
        Customer(db, other, "ornek_musteri34", groupId: group, platform: "instagram");
        Customer(db, other, "ornek_musteri34", groupId: group, platform: "tiktok");
        Customer(db, other, "ornek_musteri34");
        var ambiguous = await Matcher(db).MatchAsync(Incoming(db, other, "HAVALE ornek_musteri34"), CancellationToken.None);
        (ambiguous.Status, ambiguous.ProposedWpfCustomerId, ambiguous.Evidence).Should().Be((PaymentMatchStatus.NoProposal, (Guid?)null,
            "ambiguous:2"));
    }

    [Fact]
    public async Task Kullanici_adi_bir_satiri_IBAN_ayni_kisinin_obur_satirini_gosterirse_iki_katman_destekler()
    {
        // Satır düzeyinde kullanıcı adı IG satırını, IBAN aynı kişinin TT satırını gösterince "conflict" yazılırdı.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var group = $"G-{Guid.NewGuid():N}";
        var ig = Customer(db, lic, "ornek_musteri34", groupId: group, platform: "instagram");
        var tt = Customer(db, lic, "baska_hesap77", groupId: group, platform: "tiktok");
        var other = Customer(db, lic, "mehmet_k");
        var samePerson = NewHash(); var otherPerson = NewHash();
        IbanMemory(db, lic, tt.Id, samePerson); IbanMemory(db, lic, other.Id, otherPerson);

        var both = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE ornek_musteri34", samePerson), CancellationToken.None);
        var conflict = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE ornek_musteri34", otherPerson), CancellationToken.None);

        (both.Status, both.ProposedWpfCustomerId, both.Layer, both.Confidence, both.Evidence).Should().Be((PaymentMatchStatus.Proposed,
            (Guid?)ig.Id, PaymentMatchLayer.UsernameInDescription, PaymentMatcher.BothConfidence, "username=ornekmusteri34;iban-memory"),
            "öneri kullanıcı adının satırıdır, IBAN aynı kişide");
        both.SupportedByIbanMemory.Should().BeTrue();
        (conflict.Status, conflict.ProposedWpfCustomerId, conflict.Evidence).Should().Be((PaymentMatchStatus.NoProposal, (Guid?)null,
            "conflict:username=ornekmusteri34,iban-memory"), "bağ başka kişide: biçim aynen (KVKK araması anahtardan sonra ',' bekler)");
        conflict.SupportedByIbanMemory.Should().BeFalse();
    }

    [Theory]
    [InlineData("G1", "g1")]
    [InlineData("G1", "G1 ")]
    [InlineData("  ", "  ")]
    [InlineData(null, "")]
    public async Task GroupId_ordinal_bos_ya_da_bosluk_gruplanmamis(string? a, string? b)
    {
        // (koruma) GroupId ham değerde ordinal karşılaştırılır (kırpma ve harf katlaması yok); boş ya da yalnız boşluk
        // gruplanmamıştır (PersonMembership.IsGrouped). İki satır iki kişidir: kullanıcı adı belirsiz kalır.
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ornek_musteri34", groupId: a, platform: "instagram");
        Customer(db, lic, "ornek_musteri34", groupId: b, platform: "tiktok");

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE ornek_musteri34"), CancellationToken.None);

        (m.Status, m.ProposedWpfCustomerId, m.Evidence).Should().Be((PaymentMatchStatus.NoProposal, (Guid?)null, "ambiguous:2"));
    }

    [Fact]
    public async Task Ad_katmani_ayni_kisinin_iki_satirini_tek_kisi_sayar()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var group = $"G-{Guid.NewGuid():N}";
        var ig = Customer(db, lic, "xk_77", fullName: "Deneme Alıcı", groupId: group, platform: "instagram");
        var tt = Customer(db, lic, "yz_88", fullName: "Deneme Alıcı", groupId: group, platform: "tiktok");
        const string aciklama = "T.GARANTI BANKASI /IBAN MERKEZ SUBESI gonderilen havale DENEME ALICI odeme";

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, aciklama), CancellationToken.None);

        (m.Status, m.ProposedWpfCustomerId, m.Layer, m.Confidence, m.Evidence).Should().Be((PaymentMatchStatus.Proposed,
            (Guid?)SmallestN(ig, tt).Id, PaymentMatchLayer.NameAmount, PaymentMatcher.NameConfidence, "name:2"));

        // Aynı adı başka bir kişi de taşıyınca belirsizlik kişi sayar: üç satır, iki kişi.
        Customer(db, lic, "qw_99", fullName: "Deneme Alıcı");
        var ambiguous = await Matcher(db).MatchAsync(Incoming(db, lic, aciklama), CancellationToken.None);
        (ambiguous.Status, ambiguous.ProposedWpfCustomerId, ambiguous.Evidence).Should().Be((PaymentMatchStatus.NoProposal, (Guid?)null,
            "ambiguous-name:2"));
    }

    [Fact]
    public async Task Her_yazim_surum_2_yazar()
    {
        // Sürüm satırın son hesabının üreticisidir (S2b planı Karar 2): dışlama, belirsizlik, sinyalsizlik ve yeniden
        // değerlendirme de yazar.
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ornek_musteri"); Customer(db, lic, "ornek_musteri34"); Customer(db, lic, "mehmet_k");
        var matcher = Matcher(db);

        var excluded = await matcher.MatchAsync(Incoming(db, lic, "Firma:1234 (ornek_musteri34) POS", code: "CCP"), CancellationToken.None);
        var ambiguous = await matcher.MatchAsync(Incoming(db, lic, "HAVALE ornek musteri 34"), CancellationToken.None);
        var none = await matcher.MatchAsync(Incoming(db, lic, "HAVALE aciklamasiz"), CancellationToken.None);
        var reevaluated = await matcher.ReevaluateAsync(Incoming(db, lic, "HAVALE mehmet_k"), CancellationToken.None);

        new[] { excluded, ambiguous, none, reevaluated }.Select(r => r.Evidence).Should().Equal(
            "excluded:CCP", "ambiguous:2", "no-signal", "username=mehmetk" + PaymentMatcher.ReevaluationEvidenceSuffix);
        (await db.PaymentMatches.AsNoTracking().Select(r => r.MatcherVersion).ToListAsync()).Should().HaveCount(4)
            .And.OnlyContain(v => v == PaymentMatcher.PersonLevelVersion);
    }

    [Fact]
    public async Task Ayni_kisi_ayni_grup_ya_da_ayni_Id_aday_disi_satir_satir_duzeyi()
    {
        // Bağdaştırıcı kişi düzeyi öneriyi kişiyle karşılaştırır (S2b Görev 7). Aday olmayan satırda (silinmiş, kopya, başka
        // lisans, bilinmeyen Id) kişi okunmaz: karşılaştırma satır düzeyidir.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var group = $"G-{Guid.NewGuid():N}";
        var ig = Customer(db, lic, "ornek_musteri34", groupId: group, platform: "instagram");
        var tt = Customer(db, lic, "baska_hesap77", groupId: group, platform: "tiktok");
        var alone = Customer(db, lic, "mehmet_k");
        var purged = Customer(db, lic, "silinen_hesap", groupId: group);
        var copy = Customer(db, lic, "kopya_hesap", groupId: group);
        var foreign = Customer(db, Guid.NewGuid(), "yabanci_hesap", groupId: group);
        purged.PurgedAt = DateTimeOffset.UtcNow; copy.MergedIntoId = ig.Id; db.SaveChanges();
        var matcher = Matcher(db);

        (await matcher.SamePersonAsync(lic, ig.Id, tt.Id, CancellationToken.None)).Should().BeTrue("aynı grup");
        (await matcher.SamePersonAsync(lic, tt.Id, ig.Id, CancellationToken.None)).Should().BeTrue("aynı grup");
        (await matcher.SamePersonAsync(lic, alone.Id, alone.Id, CancellationToken.None)).Should().BeTrue("aynı Id");
        (await matcher.SamePersonAsync(lic, ig.Id, alone.Id, CancellationToken.None)).Should().BeFalse("gruplanmamış satır kendi kişisidir");
        foreach (var outside in new[] { purged.Id, copy.Id, foreign.Id, Guid.NewGuid() })
        {
            (await matcher.SamePersonAsync(lic, ig.Id, outside, CancellationToken.None)).Should().BeFalse("aday dışı satır: satır düzeyi");
            (await matcher.SamePersonAsync(lic, outside, ig.Id, CancellationToken.None)).Should().BeFalse("aday dışı satır: satır düzeyi");
            (await matcher.SamePersonAsync(lic, outside, outside, CancellationToken.None)).Should().BeTrue("aday dışı satırda da aynı Id");
        }
    }

    [Fact]
    public async Task Silme_oneriyi_bosaltinca_IBAN_destegi_de_gider_surum_kalir()
    {
        // KVKK silmesi ve IBAN talebi (BankSubjectScrub.MatchForCustomer) kararsız önerinin müşterisini boşaltınca satır
        // eşleştiricinin "öneri yok" biçimine iner; IBAN desteği de öneri alanıdır (tasarım §6 "decline", S2b planı Karar 2).
        // Sürüm kalır: silme yeniden hesap değildir, satır ölçümde bloğunu değiştirmez.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "ornek_musteri34");
        var hash = NewHash();
        IbanMemory(db, lic, c.Id, hash);
        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE ornek_musteri34", hash), CancellationToken.None);
        (m.Status, m.SupportedByIbanMemory).Should().Be((PaymentMatchStatus.Proposed, true), "ön koşul");

        BankSubjectScrub.MatchForCustomer(m, c.Id, DateTimeOffset.UtcNow);

        (m.Status, m.ProposedWpfCustomerId, m.Layer, m.Confidence, m.Evidence, m.SupportedByIbanMemory).Should().Be(
            (PaymentMatchStatus.NoProposal, (Guid?)null, PaymentMatchLayer.None, 0m, (string?)null, false));
        m.MatcherVersion.Should().Be(PaymentMatcher.PersonLevelVersion, "(koruma) silme sürümü değiştirmez");
    }

    // ── IBAN kişi kümesi (S2b; tasarım §2.4, §2.5): özetin bütün bağları kişiye toplanır. InMemory'de dar tekil yok, çift bağ
    //    tohumlanır (S3b şeması); prod'da dar tekil durdukça küme en çok bir kişidir.

    [Fact]
    public async Task Ortak_IBAN_kullanici_adinin_kisisini_iceriyorsa_oneri_odur()
    {
        // Q'nun bağı önce yazılır: sırasız tek satır okuması Q'yu okur ve kullanıcı adıyla "çelişki" derdi.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var p = Customer(db, lic, "ornek_musteri34"); var q = Customer(db, lic, "mehmet_k");
        var hash = NewHash(); var now = DateTimeOffset.UtcNow;
        IbanMemory(db, lic, q.Id, hash, now.AddDays(-2)); IbanMemory(db, lic, p.Id, hash, now.AddDays(-10));
        var tx = Incoming(db, lic, "HAVALE ornek_musteri34", hash);
        var matcher = Matcher(db);

        var m = await matcher.MatchAsync(tx, CancellationToken.None);

        (m.Status, m.ProposedWpfCustomerId, m.Layer, m.Confidence, m.Evidence, m.SupportedByIbanMemory).Should().Be((
            PaymentMatchStatus.Proposed, (Guid?)p.Id, PaymentMatchLayer.UsernameInDescription, PaymentMatcher.BothConfidence,
            "username=ornekmusteri34;iban-memory", true), "küme büyüklüğü kanıta girmez");
        (await matcher.CandidatePersonsAsync(tx, CancellationToken.None)).Should().Equal(p.Id, q.Id);

        // Dışlanan harekette ve kullanıcı adı birden çok kişiye düşünce aday yok (Karar 6).
        Customer(db, lic, "ornek_musteri");
        var fresh = Matcher(db);
        (await fresh.CandidatePersonsAsync(Incoming(db, lic, "HAVALE ornek musteri 34", hash), CancellationToken.None)).Should().BeEmpty();
        (await fresh.CandidatePersonsAsync(Incoming(db, lic, "HAVALE ornek_musteri34", hash, code: "CCP"), CancellationToken.None))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Ortak_IBAN_kullanici_adi_yoksa_oneri_yok_adaylar_son_dogrulamaya_gore()
    {
        // Q'nun bağı önce yazılır (oluşturma anı eski, doğrulaması yeni): sıra son doğrulamadan, eklenme anından değil.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var p = Customer(db, lic, "ornek_musteri34"); var q = Customer(db, lic, "mehmet_k");
        var hash = NewHash(); var now = DateTimeOffset.UtcNow;
        IbanMemory(db, lic, q.Id, hash, now.AddDays(-2)); IbanMemory(db, lic, p.Id, hash, now.AddDays(-10));
        var tx = Incoming(db, lic, "EFT GELEN aciklamasiz", hash);
        var matcher = Matcher(db);

        var m = await matcher.MatchAsync(tx, CancellationToken.None);

        (m.Status, m.ProposedWpfCustomerId, m.Layer, m.Confidence, m.Evidence, m.SupportedByIbanMemory).Should().Be((
            PaymentMatchStatus.NoProposal, (Guid?)null, PaymentMatchLayer.None, 0m, "iban-memory-shared:2", false));
        (await matcher.CandidatePersonsAsync(tx, CancellationToken.None)).Should().Equal(q.Id, p.Id);
        (await matcher.ReevaluateAsync(tx, CancellationToken.None)).Evidence.Should().Be(
            "iban-memory-shared:2" + PaymentMatcher.ReevaluationEvidenceSuffix, "';reeval' sondadır (sözleşme)");

        // Eşitlikte kişiler asıl kaydın "N"ine göre ordinal artan sıralanır; bağların eklenme sırası bunun tersi.
        var tie = NewHash(); var at = now.AddDays(-5);
        var (small, large) = ReferenceEquals(SmallestN(p, q), p) ? (p, q) : (q, p);
        IbanMemory(db, lic, large.Id, tie, at); IbanMemory(db, lic, small.Id, tie, at);
        (await matcher.CandidatePersonsAsync(Incoming(db, lic, "EFT GELEN aciklamasiz", tie), CancellationToken.None))
            .Should().Equal(small.Id, large.Id);
    }

    [Fact]
    public async Task Kullanici_adi_kume_disindaysa_celiski_adaylar_once_kullanici_adinin_kisisi()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var p = Customer(db, lic, "ornek_musteri34"); var q = Customer(db, lic, "mehmet_k");
        var hash = NewHash();
        IbanMemory(db, lic, q.Id, hash);
        var tx = Incoming(db, lic, "HAVALE ornek_musteri34", hash);
        var matcher = Matcher(db);

        var m = await matcher.MatchAsync(tx, CancellationToken.None);

        (m.Status, m.ProposedWpfCustomerId, m.Evidence, m.SupportedByIbanMemory).Should().Be((PaymentMatchStatus.NoProposal,
            (Guid?)null, "conflict:username=ornekmusteri34,iban-memory", false), "biçim aynen (KVKK araması anahtardan sonra ',' bekler)");
        (await matcher.CandidatePersonsAsync(tx, CancellationToken.None)).Should().Equal(p.Id, q.Id);
    }

    [Fact]
    public async Task Ayni_kisinin_iki_bagi_tek_kisi_en_yeni_bagin_satiri_onerilir()
    {
        // TT'nin bağı önce yazılır (oluşturma anı eski, doğrulaması yeni): satır son doğrulamadan seçilir.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var group = $"G-{Guid.NewGuid():N}";
        var ig = Customer(db, lic, "ornek_musteri34", groupId: group, platform: "instagram");
        var tt = Customer(db, lic, "baska_hesap77", groupId: group, platform: "tiktok");
        var hash = NewHash(); var now = DateTimeOffset.UtcNow;
        IbanMemory(db, lic, tt.Id, hash, now.AddDays(-2)); IbanMemory(db, lic, ig.Id, hash, now.AddDays(-10));
        var tx = Incoming(db, lic, "EFT GELEN aciklamasiz", hash);
        var matcher = Matcher(db);

        var m = await matcher.MatchAsync(tx, CancellationToken.None);

        (m.Status, m.ProposedWpfCustomerId, m.Layer, m.Confidence, m.Evidence, m.SupportedByIbanMemory).Should().Be((
            PaymentMatchStatus.Proposed, (Guid?)tt.Id, PaymentMatchLayer.IbanMemory, PaymentMatcher.IbanConfidence, "iban-memory", true),
            "iki bağ tek kişidir, belirsizlik yok");
        (await matcher.CandidatePersonsAsync(tx, CancellationToken.None)).Should().Equal(tt.Id);

        // Eşitlikte asıl kaydın "N"i ordinal küçük olan; bağların eklenme sırası bunun tersi.
        var tie = NewHash(); var at = now.AddDays(-5);
        var (small, large) = ReferenceEquals(SmallestN(ig, tt), ig) ? (ig, tt) : (tt, ig);
        IbanMemory(db, lic, large.Id, tie, at); IbanMemory(db, lic, small.Id, tie, at);
        (await matcher.MatchAsync(Incoming(db, lic, "EFT GELEN aciklamasiz", tie), CancellationToken.None)).ProposedWpfCustomerId
            .Should().Be(small.Id);
    }

    [Fact]
    public async Task Kopyadaki_bag_asil_kayda_sayilir_gecici_ya_da_silinmis_kopyadaki_sayilmaz()
    {
        // S2b planı Karar 7: kopyadaki bağ yalnız kopya geçici ve silinmiş değilse, zincir aynı lisansın aday asıl kaydına
        // varıyorsa sayılır. Geçici kopyanın bağı kanıtsız kişinin havalesinden öğrenilmiş olabilir.
        using var db = NewDb(); var lic = Guid.NewGuid(); var otherLic = Guid.NewGuid();
        var p = Customer(db, lic, "ornek_musteri34");
        var purged = Customer(db, lic, "silinen_hesap"); purged.PurgedAt = DateTimeOffset.UtcNow; db.SaveChanges();
        var foreign = Customer(db, otherLic, "yabanci_hesap");
        var viaCopy = NewHash();
        IbanMemory(db, lic, Copy(db, lic, p.Id).Id, viaCopy);

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "EFT GELEN aciklamasiz", viaCopy), CancellationToken.None);

        (m.Status, m.ProposedWpfCustomerId, m.Layer, m.Evidence, m.SupportedByIbanMemory).Should().Be((PaymentMatchStatus.Proposed,
            (Guid?)p.Id, PaymentMatchLayer.IbanMemory, "iban-memory", true), "geçici olmayan canlı kopyanın bağı asıl kayda sayılır");
        (Guid License, Guid Customer, string Case)[] uncounted =
        [
            (lic, Copy(db, lic, p.Id, provisional: true).Id, "geçici kopya"),
            (lic, Copy(db, lic, p.Id, purged: true).Id, "silinmiş kopya"),
            (lic, purged.Id, "silinmiş asıl kayıt"),
            (otherLic, foreign.Id, "başka lisansın bağı"),
            (lic, Copy(db, lic, foreign.Id).Id, "zincir başka lisansın kaydına varır"),
        ];
        var outcomes = new List<(string Case, PaymentMatchStatus Status, Guid? Proposed, string? Evidence)>();
        foreach (var (license, customer, @case) in uncounted)
        {
            var hash = NewHash();
            IbanMemory(db, license, customer, hash);
            var none = await Matcher(db).MatchAsync(Incoming(db, lic, "EFT GELEN aciklamasiz", hash), CancellationToken.None);
            outcomes.Add((@case, none.Status, none.ProposedWpfCustomerId, none.Evidence));
        }
        outcomes.Should().Equal(uncounted.Select(u => (u.Case, PaymentMatchStatus.NoProposal, (Guid?)null, (string?)"no-signal")));
    }

    [Fact]
    public async Task Adaylar_en_cok_bes_kisi_kullanici_adinin_kisisi_ilk()
    {
        // Kullanıcı adının kişisinin bağı kümenin en eskisi: son doğrulama sırasına bırakılsaydı ilk beşe girmezdi.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var hash = NewHash(); var now = DateTimeOffset.UtcNow;
        var p = Customer(db, lic, "ornek_musteri34");
        IbanMemory(db, lic, p.Id, hash, now.AddDays(-30));
        var others = Enumerable.Range(1, 6).Select(day =>
        {
            var c = Customer(db, lic, $"hesap_{Guid.NewGuid():N}");
            IbanMemory(db, lic, c.Id, hash, now.AddDays(-day));
            return c;
        }).ToList();
        var tx = Incoming(db, lic, "HAVALE ornek_musteri34", hash);
        var matcher = Matcher(db);

        var m = await matcher.MatchAsync(tx, CancellationToken.None);

        (m.ProposedWpfCustomerId, m.Confidence, m.Evidence).Should().Be(((Guid?)p.Id, PaymentMatcher.BothConfidence,
            "username=ornekmusteri34;iban-memory"), "yedi kişilik küme kullanıcı adının kişisini içeriyor");
        PaymentMatcher.MaxCandidatePersons.Should().Be(5);
        (await matcher.CandidatePersonsAsync(tx, CancellationToken.None)).Should().Equal(
            new[] { p.Id }.Concat(others.Take(PaymentMatcher.MaxCandidatePersons - 1).Select(c => c.Id)));
    }

    [Theory]
    [InlineData(PaymentMatchStatus.ConfirmedByHuman)]
    [InlineData(PaymentMatchStatus.Contradicted)]
    [InlineData(PaymentMatchStatus.ManualOnly)]
    public async Task Insan_karari_baglanmis_satir_yeniden_hesaplanmaz(PaymentMatchStatus karar)
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ornek_musteri34");
        var tx = Incoming(db, lic, "HAVALE ornek_musteri34");
        var eski = DateTimeOffset.UtcNow.AddDays(-1);
        var gercek = Guid.NewGuid();
        db.PaymentMatches.Add(new PaymentMatch
        {
            Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id, Layer = PaymentMatchLayer.None, Confidence = 0m,
            Evidence = "no-signal", Status = karar, ActualWpfCustomerId = gercek, DecidedAt = eski, CreatedAt = eski, UpdatedAt = eski,
        });
        db.SaveChanges();

        var m = await Matcher(db).MatchAsync(tx, CancellationToken.None);

        m.Status.Should().Be(karar);
        m.ProposedWpfCustomerId.Should().BeNull();
        m.Evidence.Should().Be("no-signal");
        m.UpdatedAt.Should().Be(eski);
        var row = await db.PaymentMatches.AsNoTracking().SingleAsync(x => x.BankTransactionId == tx.Id);
        row.Status.Should().Be(karar);
        row.ProposedWpfCustomerId.Should().BeNull();
        row.ActualWpfCustomerId.Should().Be(gercek);
        row.UpdatedAt.Should().Be(eski);
    }

    [Fact]
    public async Task Ayni_hareket_icin_ikinci_cagri_ayni_satiri_gunceller()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ornek_musteri34");
        var tx = Incoming(db, lic, "HAVALE ornek_musteri34");
        await Matcher(db).MatchAsync(tx, CancellationToken.None);
        await Matcher(db).MatchAsync(tx, CancellationToken.None);
        (await db.PaymentMatches.CountAsync(m => m.BankTransactionId == tx.Id)).Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Kaydedilemeyen_yeni_oneri_izlemede_kalmaz(bool veritabaniHatasi)
    {
        // Eşleştirici paylaşılan scoped DbContext'te koşar (çekim işi, dekont onay isteği). Başarısız öneri Added
        // kalsaydı kapsamın sonraki her SaveChanges'i onu yeniden dener, partinin kalanı ve çağıranın yazısı düşerdi.
        var hata = new PaymentMatchSaveFailure(EntityState.Added, veritabaniHatasi
            ? () => new DbUpdateException("sahte kayıt hatası")
            : () => new InvalidOperationException("sahte kayıt hatası"));
        using var db = NewDb(hata); var lic = Guid.NewGuid();
        Customer(db, lic, "ornek_musteri34");
        var ilk = Incoming(db, lic, "HAVALE ornek_musteri34");
        var sonraki = Incoming(db, lic, "HAVALE ornek_musteri34 ikinci siparis");
        var matcher = Matcher(db);

        var act = () => matcher.MatchAsync(ilk, CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should()
            .BeOfType(veritabaniHatasi ? typeof(DbUpdateException) : typeof(InvalidOperationException));
        db.ChangeTracker.Entries<PaymentMatch>().Should().BeEmpty();
        hata.Active = false;
        (await matcher.MatchAsync(sonraki, CancellationToken.None)).Status.Should().Be(PaymentMatchStatus.Proposed);
        (await db.PaymentMatches.AsNoTracking().Select(m => m.BankTransactionId).ToListAsync()).Should().Equal(sonraki.Id);
    }

    [Fact]
    public async Task Kaydedilemeyen_guncelleme_satiri_okundugu_haline_dondurur()
    {
        var hata = new PaymentMatchSaveFailure(EntityState.Modified, () => new DbUpdateException("sahte kayıt hatası"));
        using var db = NewDb(hata); var lic = Guid.NewGuid();
        Customer(db, lic, "ornek_musteri34");
        var tx = Incoming(db, lic, "HAVALE ornek_musteri34");
        var matcher = Matcher(db);
        var oneri = await matcher.MatchAsync(tx, CancellationToken.None); // yeni satır; hata yalnız güncellemede
        var okunan = oneri.UpdatedAt;
        tx.Description = "HAVALE aciklamasiz"; db.SaveChanges();

        var act = () => matcher.MatchAsync(tx, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        db.ChangeTracker.HasChanges().Should().BeFalse();
        db.Entry(oneri).State.Should().Be(EntityState.Unchanged);
        oneri.Status.Should().Be(PaymentMatchStatus.Proposed);
        oneri.Evidence.Should().Be("username=ornekmusteri34");
        oneri.UpdatedAt.Should().Be(okunan);
    }

    /// <summary>İstenen durumda PaymentMatch taşıyan SaveChanges'ı düşürür (bağlantı kopması, kısıt ihlali yerine).</summary>
    private sealed class PaymentMatchSaveFailure(EntityState durum, Func<Exception> hata) : SaveChangesInterceptor
    {
        public bool Active { get; set; } = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && eventData.Context!.ChangeTracker.Entries<PaymentMatch>().Any(e => e.State == durum)) throw hata();
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
