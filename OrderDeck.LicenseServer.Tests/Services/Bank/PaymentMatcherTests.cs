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

    private static WpfCustomerProjection Customer(LicenseDbContext db, Guid lic, string username, string? fullName = null)
    {
        var c = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = username, FullName = fullName, UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.Add(c); db.SaveChanges();
        return c;
    }

    private static BankTransaction Incoming(LicenseDbContext db, Guid lic, string description, string? ibanHash = null, string code = "FT")
    {
        var t = new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = 500m, Currency = "TL", OccurredAt = DateTimeOffset.UtcNow,
            Description = description, TransactionCode = code, CounterpartyIbanHash = ibanHash, FetchedAt = DateTimeOffset.UtcNow,
        };
        db.BankTransactions.Add(t); db.SaveChanges();
        return t;
    }

    private static void IbanMemory(LicenseDbContext db, Guid lic, Guid customerId, string hash)
    {
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = customerId, IbanHash = hash, IbanMasked = "TR..", LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
    }

    [Fact]
    public async Task Aciklamadaki_kullanici_adi_tek_adayi_bulur()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var ayse = Customer(db, lic, "ayse_gul34"); Customer(db, lic, "mehmet_k");
        var tx = Incoming(db, lic, "HAVALE - AYSE GUL34 - siparis");

        var m = await Matcher(db).MatchAsync(tx, CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.Proposed);
        m.ProposedWpfCustomerId.Should().Be(ayse.Id);
        m.Layer.Should().Be(PaymentMatchLayer.UsernameInDescription);
        m.Confidence.Should().Be(0.90m);
        m.Evidence.Should().Contain("aysegul34");
    }

    [Fact]
    public async Task Bitisik_yazim_da_eslesir_alt_dize_ile()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "burak.yildiz");
        var tx = Incoming(db, lic, "EFT GELEN burakyildiz odeme");
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
    [InlineData("gulsen", "HAVALE AYGUL SENOL")] // iki ayrı kelimeye yayılır
    [InlineData("alican", "FAST VELI ALI CANPOLAT")] // soyadının ortasında biter
    [InlineData("mehmet", "EFT AHMET MEHMETOGLU")] // soyadının başı
    [InlineData("aysenur", "HAVALE AYSE NURCAN")]
    [InlineData("kaya12", "HAVALE SELIN KAYA 1234 TL")] // tutar/referans rakamına taşar
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
    [InlineData("burakyildiz", "EFT BURAK YILDIZ odeme")] // kullanıcı adı bitişik, açıklama ayrık
    [InlineData("aysenur", "HAVALE AYSE NUR odeme")]
    [InlineData("kaya12", "HAVALE SELIN KAYA 12 TL")]
    public async Task Bitisik_yazim_ardisik_tam_tokenlarin_birlesimiyse_eslesir(string kullaniciAdi, string aciklama)
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, kullaniciAdi);

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, aciklama), CancellationToken.None);

        m.ProposedWpfCustomerId.Should().Be(c.Id);
        m.Layer.Should().Be(PaymentMatchLayer.UsernameInDescription);
    }

    [Fact]
    public async Task Birden_fazla_aday_oneri_uretmez()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ayse_gul"); Customer(db, lic, "ayse_gul34");
        var tx = Incoming(db, lic, "HAVALE ayse gul 34");

        var m = await Matcher(db).MatchAsync(tx, CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.NoProposal);
        m.Evidence.Should().StartWith("ambiguous:");
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
        Customer(db, lic, "ayse_gul34"); var b = Customer(db, lic, "mehmet_k");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        IbanMemory(db, lic, b.Id, hash);

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE ayse_gul34", hash), CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.NoProposal);
        m.ProposedWpfCustomerId.Should().BeNull();
        m.Evidence.Should().Contain("conflict");
    }

    [Fact]
    public async Task Ad_benzerligi_yalniz_dusuk_guvenli_oneri()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "xk_77", fullName: "Selin Kaya");
        var tx = Incoming(db, lic, "T.GARANTI BANKASI /IBAN MERKEZ SUBESI gonderilen havale SELIN KAYA odeme");

        var m = await Matcher(db).MatchAsync(tx, CancellationToken.None);

        m.Layer.Should().Be(PaymentMatchLayer.NameAmount);
        m.ProposedWpfCustomerId.Should().Be(c.Id);
        m.Confidence.Should().Be(0.50m);
        // Ad kişisel veri: KVKK silmesi projeksiyondaki adı siler, kanıta dokunmaz. Kanıt yalnız katmanı ve
        // eşleşen token sayısını taşır; ad, admin sayfasında önerilen müşteriden (silinmemişse) okunur.
        m.Evidence.Should().Be("name:2");
    }

    [Fact]
    public async Task Baska_lisansin_ayni_kullanici_adi_ve_iban_hafizasi_aday_olmaz()
    {
        using var db = NewDb(); var lisA = Guid.NewGuid(); var lisB = Guid.NewGuid();
        var bMusterisi = Customer(db, lisB, "ayse_gul34", fullName: "Selin Kaya");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        IbanMemory(db, lisB, bMusterisi.Id, hash);
        // Tek örnek: aday listesi lisans başına bir kez okunur; B'nin listesi A'nın hareketine sızmamalı.
        var matcher = Matcher(db);

        var b = await matcher.MatchAsync(Incoming(db, lisB, "HAVALE ayse_gul34 SELIN KAYA", hash), CancellationToken.None);
        var a = await matcher.MatchAsync(Incoming(db, lisA, "HAVALE ayse_gul34 SELIN KAYA", hash), CancellationToken.None);

        b.ProposedWpfCustomerId.Should().Be(bMusterisi.Id);
        a.Status.Should().Be(PaymentMatchStatus.NoProposal);
        a.ProposedWpfCustomerId.Should().BeNull();
        a.Evidence.Should().Be("no-signal");
    }

    [Fact]
    public async Task Silinmis_musteri_aday_olmaz_iban_hafizasi_da_onu_onermez()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "ayse_gul34", fullName: "Selin Kaya");
        c.PurgedAt = DateTimeOffset.UtcNow; db.SaveChanges();
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        IbanMemory(db, lic, c.Id, hash);

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE ayse_gul34 SELIN KAYA", hash), CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.NoProposal);
        m.ProposedWpfCustomerId.Should().BeNull();
        m.Evidence.Should().Be("no-signal");
    }

    [Fact]
    public async Task Ayni_ornek_ardisik_hareketlerde_dogru_musteriyi_bulur()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var ayse = Customer(db, lic, "ayse_gul34"); var mehmet = Customer(db, lic, "mehmet_k");
        var matcher = Matcher(db);

        (await matcher.MatchAsync(Incoming(db, lic, "HAVALE ayse_gul34"), CancellationToken.None)).ProposedWpfCustomerId.Should().Be(ayse.Id);
        (await matcher.MatchAsync(Incoming(db, lic, "HAVALE mehmet_k"), CancellationToken.None)).ProposedWpfCustomerId.Should().Be(mehmet.Id);
    }

    [Fact]
    public async Task Kanit_ham_aciklama_iban_ve_hash_icermez()
    {
        // Kanıt sütunu 180 gün saklanır ve admin sayfasında görünür: yalnız katman adı + kullanıcı adı taşır, ad taşımaz.
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ayse_gul34"); var b = Customer(db, lic, "mehmet_k", fullName: "Selin Kaya");
        var iban = BankHasherTests.TestIban();
        var hash = Hasher.HashIban(iban)!;
        IbanMemory(db, lic, b.Id, hash);
        (string Aciklama, string? Hash, PaymentMatchLayer Katman)[] durumlar =
        [
            ($"HAVALE ayse_gul34 {iban} siparis", hash, PaymentMatchLayer.None), // çelişki: kullanıcı adı a, IBAN b
            ($"EFT {iban} aciklamasiz", hash, PaymentMatchLayer.IbanMemory),
            ($"HAVALE ayse_gul34 mehmet_k {iban}", null, PaymentMatchLayer.None), // belirsiz
            ($"HAVALE SELIN KAYA {iban}", null, PaymentMatchLayer.NameAmount),
        ];

        foreach (var (aciklama, ibanHash, katman) in durumlar)
        {
            var m = await Matcher(db).MatchAsync(Incoming(db, lic, aciklama, ibanHash), CancellationToken.None);
            m.Layer.Should().Be(katman, aciklama);
            m.Evidence.Should().NotBeNullOrEmpty();
            m.Evidence.Should().NotContain(iban).And.NotContain(iban.ToLowerInvariant()).And.NotContain(iban[2..]).And.NotContain(hash);
            m.Evidence.Should().NotContain(BankTextNormalizer.Normalize(aciklama)).And.NotContain(BankTextNormalizer.Tokenize(aciklama).Joined);
            m.Evidence.Should().NotContain("selin").And.NotContain("kaya");
        }
    }

    [Fact]
    public async Task Pos_kodu_dislanir()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ayse_gul34");
        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "Firma:1234 (ayse_gul34) POS", code: "CCP"), CancellationToken.None);
        m.Status.Should().Be(PaymentMatchStatus.NoProposal);
        m.Evidence.Should().Be("excluded:CCP");
    }

    [Fact]
    public async Task Dislanan_kod_listesi_tekrarli_ve_buyuk_kucuk_harf_duyarsiz()
    {
        // Binder varsayılan ["CCP"]'nin sonuna ekler: liste tekrar taşıyabilir.
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "ayse_gul34");

        var pos = await Matcher(db, "CCP", "CCP", "pos").MatchAsync(Incoming(db, lic, "HAVALE ayse_gul34", code: "POS"), CancellationToken.None);
        var ft = await Matcher(db, "CCP", "CCP", "pos").MatchAsync(Incoming(db, lic, "HAVALE ayse_gul34", code: "FT"), CancellationToken.None);

        pos.Status.Should().Be(PaymentMatchStatus.NoProposal);
        pos.Evidence.Should().Be("excluded:POS");
        ft.ProposedWpfCustomerId.Should().Be(c.Id);
    }

    [Fact]
    public async Task Giden_ya_da_sifir_tutarli_hareket_dislanir()
    {
        // Çekim işi bunları eşleştiriciye vermez; yine de verilirse öneri üretilmez.
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ayse_gul34");
        var giden = Incoming(db, lic, "HAVALE ayse_gul34"); giden.Direction = BankTransactionDirection.Outgoing;
        var sifir = Incoming(db, lic, "HAVALE ayse_gul34"); sifir.Amount = 0m;
        db.SaveChanges();

        var g = await Matcher(db).MatchAsync(giden, CancellationToken.None);
        var s = await Matcher(db).MatchAsync(sifir, CancellationToken.None);

        g.Status.Should().Be(PaymentMatchStatus.NoProposal); g.ProposedWpfCustomerId.Should().BeNull();
        g.Evidence.Should().Be("excluded:direction");
        s.Status.Should().Be(PaymentMatchStatus.NoProposal); s.ProposedWpfCustomerId.Should().BeNull();
        s.Evidence.Should().Be("excluded:zero");
    }

    [Theory]
    [InlineData(PaymentMatchStatus.ConfirmedByHuman)]
    [InlineData(PaymentMatchStatus.Contradicted)]
    [InlineData(PaymentMatchStatus.ManualOnly)]
    public async Task Insan_karari_baglanmis_satir_yeniden_hesaplanmaz(PaymentMatchStatus karar)
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ayse_gul34");
        var tx = Incoming(db, lic, "HAVALE ayse_gul34");
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
        Customer(db, lic, "ayse_gul34");
        var tx = Incoming(db, lic, "HAVALE ayse_gul34");
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
        Customer(db, lic, "ayse_gul34");
        var ilk = Incoming(db, lic, "HAVALE ayse_gul34");
        var sonraki = Incoming(db, lic, "HAVALE ayse_gul34 ikinci siparis");
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
        Customer(db, lic, "ayse_gul34");
        var tx = Incoming(db, lic, "HAVALE ayse_gul34");
        var matcher = Matcher(db);
        var oneri = await matcher.MatchAsync(tx, CancellationToken.None); // yeni satır; hata yalnız güncellemede
        var okunan = oneri.UpdatedAt;
        tx.Description = "HAVALE aciklamasiz"; db.SaveChanges();

        var act = () => matcher.MatchAsync(tx, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        db.ChangeTracker.HasChanges().Should().BeFalse();
        db.Entry(oneri).State.Should().Be(EntityState.Unchanged);
        oneri.Status.Should().Be(PaymentMatchStatus.Proposed);
        oneri.Evidence.Should().Be("username=aysegul34");
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
