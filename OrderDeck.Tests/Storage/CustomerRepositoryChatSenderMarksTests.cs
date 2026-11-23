using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Sohbet satırının iki işareti (<see cref="CustomerRepository.GetChatSenderMarks"/>): kara liste ve
/// "Kayıtlı". Kayıtlı = telefonu dolu; müşteri arama çekmecesindeki rozetle aynı kural, ama KİŞİ
/// düzeyinde: telefon aynı kişinin bağlı bir hesabında ya da öbür "@" yazımındaki ikizinde de olabilir.
/// Adlar ve telefonlar üretilir (depo herkese açık).
/// </summary>
public class CustomerRepositoryChatSenderMarksTests
{
    private static CustomerRepository NewRepository()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return new CustomerRepository(db);
    }

    private static string Name() => $"izleyici_{Guid.NewGuid():N}"[..20];

    private static Customer Seed(CustomerRepository repo, string platform, string username,
        string? phone = null, string? groupId = null, bool blacklisted = false)
    {
        var c = new Customer(Guid.NewGuid().ToString("N"), platform, username, username, null,
            100, 100, blacklisted, null, null, 0, 0m, null, null, phone, GroupId: groupId);
        repo.Insert(c);
        return c;
    }

    [Fact]
    public void Taninmayan_gonderen_isaretsizdir()
    {
        var repo = NewRepository();

        repo.GetChatSenderMarks("instagram", Name()).Should().Be(new ChatSenderMarks(false, false));
    }

    [Fact]
    public void Telefonu_olan_satir_kayitlidir()
    {
        var repo = NewRepository();
        var ad = Name();
        Seed(repo, "instagram", ad, phone: TestPhone.NewE164());

        repo.GetChatSenderMarks("instagram", ad).IsRegistered.Should().BeTrue();
    }

    [Fact]
    public void Telefon_yalniz_bosluksa_kayitli_degildir()
    {
        var repo = NewRepository();
        var ad = Name();
        Seed(repo, "instagram", ad, phone: "  ");

        repo.GetChatSenderMarks("instagram", ad).IsRegistered.Should().BeFalse(
            "arama çekmecesindeki Kayıtlı sayacı da boşluktan ibaret telefonu saymaz");
    }

    [Fact]
    public void Telefon_bagli_hesaptaysa_yazan_hesap_da_kayitlidir()
    {
        // Kişi formu Instagram hesabıyla doldurdu, yayına TikTok'tan yazıyor: aynı müşteri kartı.
        var repo = NewRepository();
        var grup = $"grp-{Guid.NewGuid():N}";
        var tiktokAdi = Name();
        Seed(repo, "instagram", Name(), phone: TestPhone.NewE164(), groupId: grup);
        Seed(repo, "tiktok", tiktokAdi, groupId: grup);

        repo.GetChatSenderMarks("tiktok", tiktokAdi).IsRegistered.Should().BeTrue();
    }

    [Fact]
    public void Bagli_hesaplarin_hicbirinde_telefon_yoksa_kayitli_degildir()
    {
        var repo = NewRepository();
        var grup = $"grp-{Guid.NewGuid():N}";
        var ad = Name();
        Seed(repo, "instagram", ad, groupId: grup);
        Seed(repo, "tiktok", Name(), groupId: grup);

        repo.GetChatSenderMarks("instagram", ad).IsRegistered.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Bos_GroupId_grup_sayilmaz(string bosGrup)
    {
        // Boş GroupId "gruplanmamış" demektir. Grup araması bunu grup sansaydı, boş GroupId'li
        // ilgisiz bir müşterinin telefonu herkese bulaşır, bütün sohbet "Kayıtlı" görünürdü.
        var repo = NewRepository();
        var telefonsuz = Name();
        Seed(repo, "instagram", Name(), phone: TestPhone.NewE164(), groupId: bosGrup);
        Seed(repo, "instagram", telefonsuz, groupId: bosGrup);

        repo.GetChatSenderMarks("instagram", telefonsuz).IsRegistered.Should().BeFalse();
    }

    [Fact]
    public void Obur_at_yazimindaki_ikiz_telefonluysa_kayitlidir()
    {
        // Ağustos–Ekim'de Instagram adı "@ad" yazıldı; yorumlar artık "@"sız geliyor.
        var repo = NewRepository();
        var ad = Name();
        Seed(repo, "instagram", ad);
        Seed(repo, "instagram", "@" + ad, phone: TestPhone.NewE164());

        repo.GetChatSenderMarks("instagram", ad).IsRegistered.Should().BeTrue();
    }

    [Fact]
    public void Harf_farkiyla_yazan_kayitli_musteri_taninir()
    {
        var repo = NewRepository();
        var ad = Name();
        Seed(repo, "instagram", ad, phone: TestPhone.NewE164());

        repo.GetChatSenderMarks("instagram", ad.ToUpperInvariant()).IsRegistered.Should().BeTrue(
            "sohbet araması kimlik anahtarıyla (harf duyarsız) da bulur");
    }

    [Fact]
    public void Kara_liste_ve_kayitli_birbirinden_bagimsizdir()
    {
        var repo = NewRepository();
        var (ikisiDe, yalnizKaraListe, yalnizKayitli) = (Name(), Name(), Name());
        Seed(repo, "instagram", ikisiDe, phone: TestPhone.NewE164(), blacklisted: true);
        Seed(repo, "instagram", yalnizKaraListe, blacklisted: true);
        Seed(repo, "instagram", yalnizKayitli, phone: TestPhone.NewE164());

        repo.GetChatSenderMarks("instagram", ikisiDe).Should().Be(new ChatSenderMarks(true, true));
        repo.GetChatSenderMarks("instagram", yalnizKaraListe).Should().Be(new ChatSenderMarks(true, false));
        repo.GetChatSenderMarks("instagram", yalnizKayitli).Should().Be(new ChatSenderMarks(false, true));
    }

    [Fact]
    public void Kara_liste_sonucu_IsBlacklistedAnySpelling_ile_aynidir()
    {
        // Sohbet vurgusu bu yönteme taşınıyor: eski kuralın "iki yazım" davranışı korunmalı.
        var repo = NewRepository();
        var ad = Name();
        Seed(repo, "instagram", ad);
        Seed(repo, "instagram", "@" + ad, blacklisted: true);

        repo.IsBlacklistedAnySpelling("instagram", ad).Should().BeTrue("ön koşul: eski kural bunu yakalıyor");
        repo.GetChatSenderMarks("instagram", ad).IsBlacklisted.Should().BeTrue();
    }
}
