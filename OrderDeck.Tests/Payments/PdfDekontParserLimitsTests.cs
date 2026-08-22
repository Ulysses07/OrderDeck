using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using FluentAssertions;
using OrderDeck.PdfParsing;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace OrderDeck.Tests.Payments;

/// <summary>
/// Ayrıştırıcının kaynak tavanları (denetim O-04).
///
/// <para><b>Neden var:</b> PDF düşman kontrolünde — hem shopper uygulamasından
/// yüklenebiliyor hem WhatsApp'tan gelebiliyor. Bayt sınırı (5/10 MB) tek
/// başına yetmiyor: sayfa sayısı, çıkarılan metnin uzunluğu ve regex işi
/// bayttan bağımsız büyüyebiliyor. Asıl tehlike sonuncusu — alan desenlerinin
/// çoğu tembel niceleyici + alternatifli ileri-bakış içeriyor ve eşleşmeyen
/// uzun bir metinde maliyet metin uzunluğunun karesiyle büyüyor.</para>
///
/// <para>Buradaki tavanlar gerçek bir banka dekontuna (1-2 sayfa, birkaç KB
/// metin) hiç değmiyor; ölçtükleri tek şey patolojik girdi.</para>
/// </summary>
public sealed class PdfDekontParserLimitsTests
{
    private const string FakeHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const int MaxTextChars = 200_000;
    private const int MaxPages = 25;

    private readonly PdfDekontParser _parser = new();

    [Fact]
    public void Sayfa_tavanindan_sonraki_sayfalar_okunmuyor()
    {
        // Birkaç KB'lık bir PDF binlerce sayfa ilan edebiliyor; her sayfanın
        // metnini çıkarmak sınırsız iş demek.
        var pdf = BuildPdf(pageCount: MaxPages + 10, textOnPage: i => $"SAYFAISARETI{i}");

        var result = _parser.Parse(pdf);

        result.RawText.Should().Contain("SAYFAISARETI1");
        result.RawText.Should().Contain($"SAYFAISARETI{MaxPages}");
        result.RawText.Should().NotContain($"SAYFAISARETI{MaxPages + 1}",
            "sayfa tavanından sonrası hiç açılmamalı");
    }

    [Fact]
    public void Cok_uzun_metin_tavana_kirpiliyor()
    {
        // Sıkıştırılmış bir içerik akışı megabaytlarca metne açılabiliyor.
        var huge = new string('A', 2_000_000);

        var result = _parser.ParseFromText(huge, FakeHash);

        result.RawText.Length.Should().Be(MaxTextChars);
    }

    [Fact]
    public void Patolojik_metin_butceyi_asmiyor()
    {
        // Garanti deseni — SAYIN\s*(...)(?=\s*(?:İZMİR|İSTANBUL|...)) — tek
        // alternatifi şehir adı; çoğu desenin aksine "$" ÇIKIŞI YOK. Sonlandırıcı
        // içermeyen bir metinde tembel niceleyici her "SAYIN" için metnin sonuna
        // kadar açılıp başarısız oluyor, motor bir sonraki başlangıç noktasına
        // geçiyor. Anahtar kelimeyi arka arkaya dizmek çıpa sayısını metin
        // uzunluğuyla orantılı yapıyor: maliyet uzunluğun KARESİ.
        //
        // ~120 KB, gerçek bir dekontun yüzde biri kadar; bayt sınırının (5/10 MB)
        // çok altında, yani boyut kontrolü bunu hiç görmüyor.
        var hostile = new StringBuilder();
        while (hostile.Length < 120_000) hostile.Append("SAYIN ");

        var sw = Stopwatch.StartNew();
        var result = _parser.ParseFromText(hostile.ToString(), FakeHash);
        sw.Stop();

        // Bütçe 3 sn; pay CI'ın yavaş anları için. Bütçe kaldırılınca aynı
        // girdi dakikalar sürüyor — ölçüldü, eşik gerçekten bir bariyer.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
        result.PdfHash.Should().Be(FakeHash);
    }

    [Fact]
    public void Tavanlar_gercek_dekontu_bozmuyor()
    {
        // Koruma eklenirken normal yolun aynı kaldığını gösteren nöbetçi:
        // tek sayfalık, kısa bir dekont hâlâ tüm alanlarıyla çıkıyor.
        const string text =
            "Gönderen : AHMET YILMAZ Alıcı : RIDVAN ÖZCAN Alıcı IBAN : TR48 0011 1000 0000 0107 0201 32 "
            + "Tutar : 1.250,50 TL Tarih : 14.05.2026 Referans No : 1503468325";

        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("AHMET YILMAZ");
        result.Amount.Should().Be(1250.50m);
        result.PaidAt.Should().Be(new DateTime(2026, 5, 14));
        result.ReferansNo.Should().Be("1503468325");
        result.RecipientIban.Should().Be("TR480011100000000107020132");
    }

    private static byte[] BuildPdf(int pageCount, Func<int, string> textOnPage)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 1; i <= pageCount; i++)
        {
            var page = builder.AddPage(595, 842);
            page.AddText(textOnPage(i), 12, new PdfPoint(50, 700), font);
        }
        return builder.Build();
    }
}
