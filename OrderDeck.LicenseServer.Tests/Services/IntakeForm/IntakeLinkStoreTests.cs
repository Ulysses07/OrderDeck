using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

public sealed class IntakeLinkStoreTests
{
    private static IntakeLinkStore NewStore() => new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public void State_kaydedilir_ve_bir_kez_okunur()
    {
        var store = NewStore();
        var state = new IntakeLinkState("nonce1", "slug1", "youtube", "/musteri-kayit/slug1");

        var token = store.SaveState(state);

        token.Should().MatchRegex("^[0-9a-f]{64}$", "tahmin edilemez, URL-güvenli olmalı");
        store.ConsumeState(token).Should().Be(state);
        // TEK KULLANIMLIK: aynı state ile dönüş ucu iki kez çağrılırsa
        // (tarayıcı geri tuşu, tekrar oynatılan istek) ikincisi reddedilmeli.
        store.ConsumeState(token).Should().BeNull();
    }

    [Fact]
    public void Bilinmeyen_state_null_doner()
    {
        NewStore().ConsumeState("yok-boyle-bir-token").Should().BeNull();
    }

    [Fact]
    public void Kimlik_platforma_gore_ayri_saklanir()
    {
        var store = NewStore();
        var yt = new IntakeLinkedIdentity("Kanal Adı", "@kanal", "UCabc");
        var fb = new IntakeLinkedIdentity("Musa Sevinç", null, null);

        store.SaveIdentity("nonce1", "youtube", yt);
        store.SaveIdentity("nonce1", "facebook", fb);

        store.GetIdentity("nonce1", "youtube").Should().Be(yt);
        store.GetIdentity("nonce1", "facebook").Should().Be(fb);
        // Başka tarayıcının nonce'u başkasının kimliğini GÖREMEZ.
        store.GetIdentity("nonce2", "youtube").Should().BeNull();
    }

    [Fact]
    public void Kimlik_silinebilir()
    {
        var store = NewStore();
        store.SaveIdentity("n", "youtube", new IntakeLinkedIdentity("K", null, "UC1"));

        store.RemoveIdentity("n", "youtube");

        store.GetIdentity("n", "youtube").Should().BeNull();
    }
}
