using System;
using FluentAssertions;
using OrderDeck.Core.Payments;
using Xunit;

namespace OrderDeck.Tests.Payments;

/// <summary>
/// PdfDekontParser unit testleri — PdfPig'siz, text directly besleniyor.
/// Türkçe banka dekontu text örnekleri farklı banka format'larını
/// taklit eder (Ziraat / Garanti / Yapı Kredi / Akbank / Papara).
/// </summary>
public sealed class PdfDekontParserTests
{
    private readonly PdfDekontParser _parser = new();
    private const string FakeHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // ── Tutar parse ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Tutar: 1.250,50 TL", 1250.50)]
    [InlineData("Tutar : 5.000,00 TL", 5000.00)]
    [InlineData("Miktar: 100,00 TL", 100.00)]
    [InlineData("İşlem Tutarı: 250,75 TRY", 250.75)]
    [InlineData("Gönderilen Tutar: 999,99 TL", 999.99)]
    [InlineData("Havale Tutarı: 12.345,67 TL", 12345.67)]
    public void ExtractAmount_recognizes_common_labels(string text, double expected)
    {
        var result = _parser.ParseFromText(text, FakeHash);
        result.Amount.Should().Be((decimal)expected);
    }

    [Fact]
    public void ExtractAmount_fallback_picks_largest_currency_value()
    {
        var text = "Bakiye: 50,00 TL\nGönderim: 1.500,00 TL\nKomisyon: 5,00 TL";
        var result = _parser.ParseFromText(text, FakeHash);
        result.Amount.Should().Be(1500.00m);
    }

    [Fact]
    public void ExtractAmount_returns_null_when_no_currency_text()
    {
        var result = _parser.ParseFromText("Lorem ipsum dolor sit amet.", FakeHash);
        result.Amount.Should().BeNull();
    }

    // ── Payer name parse ────────────────────────────────────────────────

    [Theory]
    [InlineData("Gönderen: Ahmet Yıldız\nIBAN: TR...", "Ahmet Yıldız")]
    [InlineData("Ad Soyad: Ayşe Demir\nTC: 12345", "Ayşe Demir")]
    [InlineData("Hesap Sahibi: Mehmet Öztürk Tarih: 01.01.2025", "Mehmet Öztürk")]
    [InlineData("Gonderen: Burak Kaya\nIBAN: TR...", "Burak Kaya")]
    public void ExtractPayerName_recognizes_common_labels(string text, string expected)
    {
        var result = _parser.ParseFromText(text, FakeHash);
        result.PayerName.Should().Be(expected);
    }

    [Fact]
    public void ExtractPayerName_returns_null_when_no_label()
    {
        var result = _parser.ParseFromText("Some unrelated text.", FakeHash);
        result.PayerName.Should().BeNull();
    }

    // ── Date parse ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Tarih: 15.03.2025", 2025, 3, 15)]
    [InlineData("İşlem Tarihi: 01/12/2024", 2024, 12, 1)]
    [InlineData("Valor Tarihi: 5.6.2026", 2026, 6, 5)]
    [InlineData("Tarihi: 08.11.2025 14:30", 2025, 11, 8)]
    public void ExtractPaidAt_recognizes_common_formats(string text, int year, int month, int day)
    {
        var result = _parser.ParseFromText(text, FakeHash);
        result.PaidAt.Should().Be(new DateTime(year, month, day));
    }

    [Fact]
    public void ExtractPaidAt_fallback_picks_first_date_in_text()
    {
        var text = "Some line\nAnother line with 12.04.2025 somewhere";
        var result = _parser.ParseFromText(text, FakeHash);
        result.PaidAt.Should().Be(new DateTime(2025, 4, 12));
    }

    // ── ReferansNo parse ────────────────────────────────────────────────

    [Theory]
    [InlineData("Referans No: 1234567890", "1234567890")]
    [InlineData("İşlem No: 9988776655", "9988776655")]
    [InlineData("Dekont No: 12345678", "12345678")]
    [InlineData("Onay Kodu: 987654", "987654")]
    [InlineData("SORGU NO: 1503468325", "1503468325")]
    [InlineData("Fiş No: 202605099585819", "202605099585819")]
    [InlineData("Fiş No :202605099585819", "202605099585819")]   // QNB style (boşluksuz)
    public void ExtractReferansNo_recognizes_numeric_labels(string text, string expected)
    {
        var result = _parser.ParseFromText(text, FakeHash);
        result.ReferansNo.Should().Be(expected);
    }

    [Theory]
    [InlineData("Referans No: GTI8765432101", "GTI8765432101")]   // Garanti
    [InlineData("İşlem No: AKB12345678", "AKB12345678")]          // Akbank prefix
    public void ExtractReferansNo_recognizes_alphanumeric_with_letter_prefix(string text, string expected)
    {
        var result = _parser.ParseFromText(text, FakeHash);
        result.ReferansNo.Should().Be(expected);
    }

    [Fact]
    public void ExtractReferansNo_returns_null_when_too_short()
    {
        var result = _parser.ParseFromText("Referans No: 12", FakeHash);
        result.ReferansNo.Should().BeNull();
    }

    [Fact]
    public void ExtractReferansNo_stops_at_next_label_in_single_line_pdf()
    {
        // QNB-style: PDF tek satır, label arasında separator yok.
        // Numeric-only pattern "1503468325" + "M" (MÜŞTERİ başlangıcı) yutmamalı.
        var text = "SORGU NO: 1503468325MÜŞTERİ ÜNVANI: ALI";
        var result = _parser.ParseFromText(text, FakeHash);
        result.ReferansNo.Should().Be("1503468325");
    }

    // ── Integration: realistic Turkish bank receipt mock ────────────────

    [Fact]
    public void Parse_extracts_all_four_fields_from_realistic_receipt()
    {
        var text = @"
TÜRKİYE GARANTİ BANKASI A.Ş.
Havale İşlem Dekontu

Tarih: 15.03.2025 14:32
İşlem No: GTI8765432101

Gönderen: Ahmet Yıldız
Hesap: TR12 0006 2000 1234 5678 9012 34

Alıcı: ORDERDECK YAYINCI
IBAN: TR00 0000 0000 0000 0000 0000 00

Tutar: 1.500,00 TL
Açıklama: Yayın ödemesi
";

        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("Ahmet Yıldız");
        result.Amount.Should().Be(1500.00m);
        result.PaidAt.Should().Be(new DateTime(2025, 3, 15));
        result.ReferansNo.Should().Be("GTI8765432101");
        result.PdfHash.Should().Be(FakeHash);
    }

    [Fact]
    public void Parse_with_missing_fields_returns_partial_result()
    {
        // Eksik bilgi: sadece tutar var
        var text = "Yapı Kredi Bankası\nMiktar: 250,00 TL\n";
        var result = _parser.ParseFromText(text, FakeHash);

        result.Amount.Should().Be(250m);
        result.PayerName.Should().BeNull();
        result.PaidAt.Should().BeNull();
        result.ReferansNo.Should().BeNull();
    }

    // ── Türkiye Finans format (2026-05-12 real-world iterate) ───────────

    [Fact]
    public void Parse_turkiye_finans_dekont_extracts_all_fields()
    {
        // Real PDF text dump: "GÖNDEREN" + "İsim : NAME" 2-step,
        // dash-alphanumeric referans no, "Düzenleme Tarihi" label,
        // US-format amount.
        var text = "Büyük Mükellefler V.D. No:0680063870DEKONTFAST" +
                   "Düzenleme Tarihi  : 8.05.2026 18:22:00" +
                   "Referans No       : 20260508-99-XOGKX" +
                   "GÖNDERENİsim              : HARUN CEYLAN" +
                   "ALICIİsim              : RIDVAN ÖZCAN" +
                   "IBAN/Hesap No     : TR480011100000000107020132" +
                   "İŞLEMTutar             : 24,270.00";

        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("HARUN CEYLAN");
        result.Amount.Should().Be(24270m);
        result.PaidAt.Should().Be(new DateTime(2026, 5, 8));
        result.ReferansNo.Should().Be("20260508-99-XOGKX");
        result.RecipientIban.Should().Be("TR480011100000000107020132");
    }

    // ── Ziraat format (2026-05-12 real-world iterate) ───────────────────

    [Fact]
    public void Parse_ziraat_dekont_extracts_all_fields()
    {
        // Real Ziraat sample format. Inline "Gönderen : NAME Alan Banka : ..."
        // ve "Alıcı Hesap : TR..." (IBAN keyword'ü yok).
        // Referans no "Fast Sorgu No" label'i altında.
        var text = "İŞLEM TARİHİ:06/02/2024-12:19:17 - F06195VALÖR:06.02.2024" +
                   "İŞLEM YERİ:ZİRAAT MOBİLHESAPTAN FASTsagolun" +
                   "Fast Mesaj Kodu : A01 Fast Sorgu No : 2383575454" +
                   "Gönderen : FUAD HAMOOD" +
                   "Alan Banka : 0015 - Türkiye Vakıﬂar Bankası T.A.O." +
                   "Alıcı Hesap : TR380001500158007306339861 " +
                   "Alıcı : Doha Mokhtar Mohamed Issa Harby" +
                   "İşlem Tutarı : 1.500,00 TRYKomisyon : 3,97 TRY";

        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("FUAD HAMOOD");
        result.Amount.Should().Be(1500m);
        result.PaidAt.Should().Be(new DateTime(2024, 2, 6));
        result.ReferansNo.Should().Be("2383575454");
        result.RecipientIban.Should().Be("TR380001500158007306339861");
        result.RecipientName.Should().Be("Doha Mokhtar Mohamed Issa Harby");
    }

    // ── Vakıfbank format (2026-05-12 real-world iterate) ────────────────

    [Fact]
    public void Parse_vakifbank_dekont_extracts_all_fields()
    {
        // Vakıfbank klasik havale formatı: separator yok, label sonrası direkt
        // değer continuous text. "GONDEREN ADSOYAD/UNVAN", "ALICI HESAP NO",
        // "ALICI AD SOYAD/UNVAN", "İŞLEM TUTARI" — hiçbir colon yok.
        var text = "VAKIFBANKİŞLEM BİLGİLERİİŞLEMHesaptan Havale" +
                   "İŞLEM TARİHİ10.08.2022 15:29:05" +
                   "ALICI HESAP NOTR54 0001 5001 5800 73168592 23" +
                   "ALICI AD SOYAD/UNVANKIRŞEHİR AHİ EVRAN ÜNİVERSİTESİ" +
                   "GONDEREN HESAP NOTR55 0001 5001 5800 73017241 98" +
                   "GONDEREN ADSOYAD/UNVANERDAL TÖRE" +
                   "İŞLEM TUTARI300,00 TLMASRAF TUTARI" +
                   "İŞLEM NO2022003572846205FİŞ NO";

        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("ERDAL TÖRE");
        result.Amount.Should().Be(300m);
        result.PaidAt.Should().Be(new DateTime(2022, 8, 10));
        result.ReferansNo.Should().Be("2022003572846205");
        result.RecipientIban.Should().Be("TR540001500158007316859223");
        result.RecipientName.Should().Be("KIRŞEHİR AHİ EVRAN ÜNİVERSİTESİ");
    }

    // ── 2026-05-13: 4 yeni banka format'ı (Kuveyt Türk, Garanti, Denizbank, İş Bankası)

    [Fact]
    public void Parse_kuveyt_turk_continuous_text_extracts_all_fields()
    {
        // Kuveyt Türk PDF tek satır + hiçbir boşluk yok. Continuous text
        // pattern'larıyla yakalanır: GönderenKişi/Alıcı/GönderilenIBAN/Tutar.
        var text = "KUVEYTTÜRKKATILIMBANKASIVergiNo:6000026814" +
                   "İşlemTarihi30.03.202614:06SorguNumarası9360608" +
                   "GönderenKişiV2SPORMALZEMELERİTEKSTİLLİMİTEDŞİRKETİ" +
                   "AlıcıRıdvanÖzcanGönderilenIBANTR480011100000000107020132" +
                   "AlıcıBankaQnbBankA.Ş.İşlemYeriMobilŞubeAçıklama" +
                   "Tutar20.000,00TLYalnızYirmiBinTL";
        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("V2SPORMALZEMELERİTEKSTİLLİMİTEDŞİRKETİ");
        result.Amount.Should().Be(20000m);
        result.PaidAt.Should().Be(new DateTime(2026, 3, 30));
        result.ReferansNo.Should().Be("9360608");
        result.RecipientIban.Should().Be("TR480011100000000107020132");
        result.RecipientName.Should().Be("RıdvanÖzcan");
    }

    [Fact]
    public void Parse_garanti_bbva_dekont_extracts_all_fields()
    {
        // Garanti BBVA: "SAYIN NAME" (PayerName), "ALACAKLI : NAME" (Recipient),
        // "ALACAKLI IBAN : TR48...", "FAST REF NO : 8794..."
        var text = "T. Garanti Bankası A.Ş.HESAPTAN FAST" +
                   "İŞLEM TARİHİ     : 05/05/2026" +
                   "IBAN:TR44 0006 2000 0920 0006 8833 65" +
                   "SAYINKUBİLAY ÇİFTÇİİZMİR DENİZ ER EĞİTİM MERKEZİ" +
                   "FAST REF NO      : 8794000212" +
                   "ALACAKLI         : RIDVAN ÖZCAN" +
                   "ALACAKLI IBAN    : TR48 0011 1000 0000 0107 0201 32" +
                   "MASRAF           :  15,96 TL  Tutar 25.200,00 TL";
        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("KUBİLAY ÇİFTÇİ");
        result.PaidAt.Should().Be(new DateTime(2026, 5, 5));
        result.ReferansNo.Should().Be("8794000212");
        result.RecipientIban.Should().Be("TR480011100000000107020132");
        result.RecipientName.Should().Be("RIDVAN ÖZCAN");
    }

    [Fact]
    public void Parse_denizbank_dekont_extracts_all_fields()
    {
        // Denizbank: "Adı SoyadıNAME" (no colon, continuous), "Alıcı Adı SoyadıNAME",
        // "Alıcı IBANTR48..."
        var text = "Denizbank A.Ş.Müşteri BilgisiAdı SoyadıLAMİA DİLEK" +
                   "VKN / TCKN/3773007****IBANTR36 0013 4000 0190 0768 3000 01" +
                   "İşlem Tarihi01.05.2026 19:31:38" +
                   "Alıcı Banka0111-QNB BANK A.Ş." +
                   "Alıcı IBANTR48 0011 1000 0000 0107 0201 32" +
                   "Alıcı Adı SoyadıRIDVAN ÖZCANTutar10.000,00 TL";
        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("LAMİA DİLEK");
        result.Amount.Should().Be(10000m);
        result.PaidAt.Should().Be(new DateTime(2026, 5, 1));
        result.RecipientIban.Should().Be("TR480011100000000107020132");
        result.RecipientName.Should().Be("RIDVAN ÖZCAN");
    }

    [Fact]
    public void Parse_is_bankasi_bilgi_dekontu_extracts_all_fields()
    {
        // İş Bankası "Bilgi Dekontu" format: "Alıcı Isim\Unvan:NAME" backslash
        // sub-label.
        var text = "Bilgi DekontuİBRAHİM BARIN BESLEKMüşteri No:515066630" +
                   "İşlem Zam./Valör:24.04.2026 17:53:41 / 24.04.2026" +
                   "İşlem Tutarı:20.000,00 TRY" +
                   "Sorgu Numarası:3327706380" +
                   "Alıcı Banka:111 - QNB Finansbank A.Ş." +
                   "Alıcı IBAN:TR48 0011 1000 0000 0107 0201 32" +
                   @"Alıcı Isim\Unvan:RIDVAN ÖZCANBSMV:0,77 TRY";
        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("İBRAHİM BARIN BESLEK");
        result.Amount.Should().Be(20000m);
        result.PaidAt.Should().Be(new DateTime(2026, 4, 24));
        result.ReferansNo.Should().Be("3327706380");
        result.RecipientIban.Should().Be("TR480011100000000107020132");
        result.RecipientName.Should().Be("RIDVAN ÖZCAN");
    }

    [Fact]
    public void Parse_yapi_kredi_e_dekont_fast_outgoing_extracts_all_fields()
    {
        // Yapı Kredi e-Dekont FAST (giden) — "GİDEN FAST TUTARI :-35000" negatif
        // outgoing format, decimal yok. PayerName="GÖNDEREN ADI", RecipientName=
        // "ALICI ADI" boşluk padding'li label'lar. Amount abs alınır (caller için
        // pozitif tutar).
        var text = "e-DekontFAST GÖNDERİMİ" +
                   "İŞLEM TARİHİ:30.04.2026 15:04:56" +
                   "GİDEN FAST TUTARI :-35000                                            " +
                   "GÖNDEREN ADI      :NURSEL ATBAŞ                                      " +
                   "ALICI BANKA       :QNB Bank A.Ş.                                     " +
                   "SORGU NO                :2854829652                " +
                   "ALICI HESAP       :TR430011100000000155645255                        " +
                   "ALICI ADI         :EMAR GLOBAL TEKSTİL GIDA İNŞAAT TURİZM YAZILIM VE TİC.LTD.ŞTİ.                                       " +
                   "ALICI TCKN/VD/VKN : -";
        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("NURSEL ATBAŞ");
        result.Amount.Should().Be(35000m); // abs alındı, pozitif
        result.PaidAt.Should().Be(new DateTime(2026, 4, 30));
        result.ReferansNo.Should().Be("2854829652");
        result.RecipientIban.Should().Be("TR430011100000000155645255");
        result.RecipientName.Should().Contain("EMAR GLOBAL TEKSTİL");
    }

    [Fact]
    public void Parse_vakifbank_fast_new_format_with_slash_unvan()
    {
        // Vakıfbank yeni FAST (2026 format): "GÖNDEREN AD SOYAD /UNVAN" ve
        // "ALICI HESAP NO / IBAN" (slash öncesi/sonrası boşluk var; eski
        // continuous format "GONDEREN ADSOYAD/UNVAN"dan farklı).
        var text = "VAKIFBANKİŞLEM BİLGİLERİİŞLEM TÜRÜFAST Giden Anlık Ödeme" +
                   "İŞLEM TARİHİ10.04.2026 12:52:11" +
                   "SORGU NO2553031025İŞLEM TUTARI80.000,00 TLMASRAF TUTARI" +
                   "GÖNDEREN AD SOYAD /UNVAN242 GİYİM TEKSTİLSANAYİ" +
                   "ALICI AD SOYAD/UNVANEMAR GLOBAL TEKSTİL" +
                   "ALICI HESAP NO / IBANTR43 0011 1000 0000 01556452 55" +
                   "İŞLEM NO2026005253222628FİŞ NO";
        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Contain("242 GİYİM TEKSTİL");
        result.Amount.Should().Be(80000m);
        result.PaidAt.Should().Be(new DateTime(2026, 4, 10));
        result.ReferansNo.Should().Be("2026005253222628");
        result.RecipientIban.Should().Be("TR430011100000000155645255");
        result.RecipientName.Should().Contain("EMAR GLOBAL");
    }

    [Fact]
    public void ExtractPayerName_ignores_label_without_colon()
    {
        // Vakıfbank "GONDEREN HESAP NOTR55..." — eski loose pattern
        // ("Gönderen" + whitespace) "HESAP NO"'yu PayerName olarak yutuyordu.
        // Doğru pattern colon zorunlu, label "ADSOYAD/UNVAN" lookahead'lı.
        var text = "GONDEREN HESAP NOTR55 0001 5001 5800 73017241 98ALICI";
        var result = _parser.ParseFromText(text, FakeHash);
        result.PayerName.Should().BeNull();
    }

    // ── RecipientIban (2026-05-12) ──────────────────────────────────────

    [Theory]
    [InlineData("ALICI IBAN: TR830020500009512140100001", "TR830020500009512140100001")]
    [InlineData("ALICIIsim : X IBAN/Hesap No : TR48 0011 1000 0000 0107 0201 32", "TR480011100000000107020132")]
    [InlineData("Alıcı : ERDEM HAN GIDA IBAN: TR430011100000000155645255", "TR430011100000000155645255")]
    public void ExtractRecipientIban_finds_iban_in_alici_section(string text, string expected)
    {
        var result = _parser.ParseFromText(text, FakeHash);
        result.RecipientIban.Should().Be(expected);
    }

    [Fact]
    public void ExtractRecipientIban_null_when_only_gonderen_iban_present()
    {
        var text = "Gönderen: Foo IBAN: TR12 0011 1000 0000 0107 0201 32";
        var result = _parser.ParseFromText(text, FakeHash);
        result.RecipientIban.Should().BeNull();
    }

    [Fact]
    public void ExtractReferansNo_dash_alphanumeric_stops_at_next_section()
    {
        // Türkiye Finans single-line: "20260508-99-XOGKXGÖNDEREN" — son "G"
        // (GÖNDEREN başı) yutulmamalı.
        var text = "Referans No: 20260508-99-XOGKXGÖNDEREN";
        var result = _parser.ParseFromText(text, FakeHash);
        result.ReferansNo.Should().Be("20260508-99-XOGKX");
    }

    // ── RecipientName (2026-05-12 — IBAN + name match güvenliği) ────────

    [Theory]
    [InlineData("ALICI ÜNVANI: ERDEM HAN GIDA   ALICI IBAN: TR...", "ERDEM HAN GIDA")]
    [InlineData("ALICIIsim              : RIDVAN ÖZCANIBAN/Hesap No", "RIDVAN ÖZCAN")]
    [InlineData("Alıcı : ERDEM HAN GIDA Kuveyt Türk Katılım", "ERDEM HAN GIDA")]
    public void ExtractRecipientName_recognizes_common_formats(string text, string expected)
    {
        var result = _parser.ParseFromText(text, FakeHash);
        result.RecipientName.Should().Be(expected);
    }

    [Fact]
    public void ExtractRecipientName_null_when_no_alici_section()
    {
        var text = "Gönderen: Foo IBAN: TR12";
        var result = _parser.ParseFromText(text, FakeHash);
        result.RecipientName.Should().BeNull();
    }

    [Theory]
    [InlineData("Erdem Han Gıda", "Erdem Han Gıda", true)]
    [InlineData("Erdem Han Gıda", "ERDEM HAN GIDA", true)]   // case-insensitive
    [InlineData("Erdem Han Gıda", "Erdem Han Gida", true)]   // Türkçe ı→i normalize
    [InlineData("Erdem Han Gıda", "ERDEM HAN GIDA Kuveyt Türk Katılım", true)]   // substring
    [InlineData("Erdem Han Gıda", "Mehmet Yılmaz", false)]
    public void NormalizeName_supports_case_and_turkish_compare(
        string vendor, string pdf, bool expectsMatch)
    {
        var v = PdfDekontParser.NormalizeName(vendor);
        var p = PdfDekontParser.NormalizeName(pdf);
        var match = !string.IsNullOrEmpty(v) && (p.Contains(v) || v.Contains(p));
        match.Should().Be(expectsMatch);
    }
}
