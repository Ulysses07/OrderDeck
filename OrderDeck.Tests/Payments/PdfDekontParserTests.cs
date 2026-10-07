using System;
using FluentAssertions;
using OrderDeck.PdfParsing;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Payments;

/// <summary>
/// PdfDekontParser unit testleri — PdfPig'siz, text directly besleniyor.
/// Türkçe banka dekontu text örnekleri farklı banka format'larını
/// taklit eder (Ziraat / Garanti / Yapı Kredi / Akbank / Papara).
///
/// Banka formatlarının yapısı (bitişik etiketler, boşluk düzeni) gerçek
/// dekontlardan alındı; içlerindeki kişi/şirket adları kurgusal, IBAN'lar
/// banka kodu korunarak her koşuda üretiliyor. Kurgusal adlar ayrıştırıcının
/// etiket/sonlandırıcı kelimelerini (ALICI, GÖNDEREN, MÜŞTERİ, HESAP…) içermez.
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
    [InlineData("Gönderen: Örnek Şahıs\nIBAN: TR...", "Örnek Şahıs")]
    [InlineData("Ad Soyad: Deneme Şahıs\nTC: 12345", "Deneme Şahıs")]
    [InlineData("Hesap Sahibi: Test Şahıs Tarih: 01.01.2025", "Test Şahıs")]
    [InlineData("Gonderen: Kurgu Şahıs\nIBAN: TR...", "Kurgu Şahıs")]
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

Gönderen: Örnek Şahıs
Hesap: TR12 0006 2000 1234 5678 9012 34

Alıcı: ORDERDECK YAYINCI
IBAN: TR00 0000 0000 0000 0000 0000 00

Tutar: 1.500,00 TL
Açıklama: Yayın ödemesi
";

        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("Örnek Şahıs");
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
        var aliciIban = TestIban.NewTr("00111");
        var text = "Büyük Mükellefler V.D. No:0680063870DEKONTFAST" +
                   "Düzenleme Tarihi  : 8.05.2026 18:22:00" +
                   "Referans No       : 20260508-99-XOGKX" +
                   "GÖNDERENİsim              : ÖRNEK ŞAHIS" +
                   "ALICIİsim              : DENEME YAYINCI" +
                   $"IBAN/Hesap No     : {aliciIban}" +
                   "İŞLEMTutar             : 24,270.00";

        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("ÖRNEK ŞAHIS");
        result.Amount.Should().Be(24270m);
        result.PaidAt.Should().Be(new DateTime(2026, 5, 8));
        result.ReferansNo.Should().Be("20260508-99-XOGKX");
        result.RecipientIban.Should().Be(aliciIban);
    }

    // ── Ziraat format (2026-05-12 real-world iterate) ───────────────────

    [Fact]
    public void Parse_ziraat_dekont_extracts_all_fields()
    {
        // Real Ziraat sample format. Inline "Gönderen : NAME Alan Banka : ..."
        // ve "Alıcı Hesap : TR..." (IBAN keyword'ü yok).
        // Referans no "Fast Sorgu No" label'i altında.
        var aliciIban = TestIban.NewTr("00015");
        var text = "İŞLEM TARİHİ:06/02/2024-12:19:17 - F06195VALÖR:06.02.2024" +
                   "İŞLEM YERİ:ZİRAAT MOBİLHESAPTAN FASTsagolun" +
                   "Fast Mesaj Kodu : A01 Fast Sorgu No : 2383575454" +
                   "Gönderen : ÖRNEK ŞAHIS" +
                   "Alan Banka : 0015 - Türkiye Vakıﬂar Bankası T.A.O." +
                   $"Alıcı Hesap : {aliciIban} " +
                   "Alıcı : Deneme Uzun Yabancı Kurgu Şahıs" +
                   "İşlem Tutarı : 1.500,00 TRYKomisyon : 3,97 TRY";

        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("ÖRNEK ŞAHIS");
        result.Amount.Should().Be(1500m);
        result.PaidAt.Should().Be(new DateTime(2024, 2, 6));
        result.ReferansNo.Should().Be("2383575454");
        result.RecipientIban.Should().Be(aliciIban);
        result.RecipientName.Should().Be("Deneme Uzun Yabancı Kurgu Şahıs");
    }

    // ── Vakıfbank format (2026-05-12 real-world iterate) ────────────────

    [Fact]
    public void Parse_vakifbank_dekont_extracts_all_fields()
    {
        // Vakıfbank klasik havale formatı: separator yok, label sonrası direkt
        // değer continuous text. "GONDEREN ADSOYAD/UNVAN", "ALICI HESAP NO",
        // "ALICI AD SOYAD/UNVAN", "İŞLEM TUTARI" — hiçbir colon yok.
        var aliciIban = TestIban.NewTr("00015");
        var gonderenIban = TestIban.NewTr("00015");
        var text = "VAKIFBANKİŞLEM BİLGİLERİİŞLEMHesaptan Havale" +
                   "İŞLEM TARİHİ10.08.2022 15:29:05" +
                   $"ALICI HESAP NO{VakifbankGrouped(aliciIban)}" +
                   "ALICI AD SOYAD/UNVANKIRŞEHİR AHİ EVRAN ÜNİVERSİTESİ" +
                   $"GONDEREN HESAP NO{VakifbankGrouped(gonderenIban)}" +
                   "GONDEREN ADSOYAD/UNVANÖRNEK ŞAHIS" +
                   "İŞLEM TUTARI300,00 TLMASRAF TUTARI" +
                   "İŞLEM NO2022003572846205FİŞ NO";

        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("ÖRNEK ŞAHIS");
        result.Amount.Should().Be(300m);
        result.PaidAt.Should().Be(new DateTime(2022, 8, 10));
        result.ReferansNo.Should().Be("2022003572846205");
        result.RecipientIban.Should().Be(aliciIban);
        result.RecipientName.Should().Be("KIRŞEHİR AHİ EVRAN ÜNİVERSİTESİ");
    }

    // ── 2026-05-13: 4 yeni banka format'ı (Kuveyt Türk, Garanti, Denizbank, İş Bankası)

    [Fact]
    public void Parse_kuveyt_turk_continuous_text_extracts_all_fields()
    {
        // Kuveyt Türk PDF tek satır + hiçbir boşluk yok. Continuous text
        // pattern'larıyla yakalanır: GönderenKişi/Alıcı/GönderilenIBAN/Tutar.
        var aliciIban = TestIban.NewTr("00111");
        var text = "KUVEYTTÜRKKATILIMBANKASIVergiNo:6000026814" +
                   "İşlemTarihi30.03.202614:06SorguNumarası9360608" +
                   "GönderenKişiÖRNEK2SPORMALZEMELERİTEKSTİLLİMİTEDŞİRKETİ" +
                   $"AlıcıDenemeYayıncıGönderilenIBAN{aliciIban}" +
                   "AlıcıBankaQnbBankA.Ş.İşlemYeriMobilŞubeAçıklama" +
                   "Tutar20.000,00TLYalnızYirmiBinTL";
        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("ÖRNEK2SPORMALZEMELERİTEKSTİLLİMİTEDŞİRKETİ");
        result.Amount.Should().Be(20000m);
        result.PaidAt.Should().Be(new DateTime(2026, 3, 30));
        result.ReferansNo.Should().Be("9360608");
        result.RecipientIban.Should().Be(aliciIban);
        result.RecipientName.Should().Be("DenemeYayıncı");
    }

    [Fact]
    public void Parse_garanti_bbva_dekont_extracts_all_fields()
    {
        // Garanti BBVA: "SAYIN NAME" (PayerName), "ALACAKLI : NAME" (Recipient),
        // "ALACAKLI IBAN : TR48...", "FAST REF NO : 8794..."
        var gonderenIban = TestIban.NewTr("00062");
        var aliciIban = TestIban.NewTr("00111");
        var text = "T. Garanti Bankası A.Ş.HESAPTAN FAST" +
                   "İŞLEM TARİHİ     : 05/05/2026" +
                   $"IBAN:{TestIban.Grouped(gonderenIban)}" +
                   "SAYINÖRNEK ŞAHISİZMİR ÖRNEK EĞİTİM MERKEZİ" +
                   "FAST REF NO      : 8794000212" +
                   "ALACAKLI         : DENEME YAYINCI" +
                   $"ALACAKLI IBAN    : {TestIban.Grouped(aliciIban)}" +
                   "MASRAF           :  15,96 TL  Tutar 25.200,00 TL";
        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("ÖRNEK ŞAHIS");
        result.PaidAt.Should().Be(new DateTime(2026, 5, 5));
        result.ReferansNo.Should().Be("8794000212");
        result.RecipientIban.Should().Be(aliciIban);
        result.RecipientName.Should().Be("DENEME YAYINCI");
    }

    [Fact]
    public void Parse_denizbank_dekont_extracts_all_fields()
    {
        // Denizbank: "Adı SoyadıNAME" (no colon, continuous), "Alıcı Adı SoyadıNAME",
        // "Alıcı IBANTR48..."
        var gonderenIban = TestIban.NewTr("00134");
        var aliciIban = TestIban.NewTr("00111");
        var maskeliTc = TestTckn.NewValid()[..7] + "****";
        var text = "Denizbank A.Ş.Müşteri BilgisiAdı SoyadıÖRNEK ŞAHIS" +
                   $"VKN / TCKN/{maskeliTc}IBAN{TestIban.Grouped(gonderenIban)}" +
                   "İşlem Tarihi01.05.2026 19:31:38" +
                   "Alıcı Banka0111-QNB BANK A.Ş." +
                   $"Alıcı IBAN{TestIban.Grouped(aliciIban)}" +
                   "Alıcı Adı SoyadıDENEME YAYINCITutar10.000,00 TL";
        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("ÖRNEK ŞAHIS");
        result.Amount.Should().Be(10000m);
        result.PaidAt.Should().Be(new DateTime(2026, 5, 1));
        result.RecipientIban.Should().Be(aliciIban);
        result.RecipientName.Should().Be("DENEME YAYINCI");
    }

    [Fact]
    public void Parse_is_bankasi_bilgi_dekontu_extracts_all_fields()
    {
        // İş Bankası "Bilgi Dekontu" format: "Alıcı Isim\Unvan:NAME" backslash
        // sub-label.
        var aliciIban = TestIban.NewTr("00111");
        var musteriNo = Random.Shared.Next(100_000_000, 1_000_000_000);
        var text = $"Bilgi DekontuÖRNEK İKİNCİ ŞAHISMüşteri No:{musteriNo}" +
                   "İşlem Zam./Valör:24.04.2026 17:53:41 / 24.04.2026" +
                   "İşlem Tutarı:20.000,00 TRY" +
                   "Sorgu Numarası:3327706380" +
                   "Alıcı Banka:111 - QNB Finansbank A.Ş." +
                   $"Alıcı IBAN:{TestIban.Grouped(aliciIban)}" +
                   @"Alıcı Isim\Unvan:DENEME YAYINCIBSMV:0,77 TRY";
        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("ÖRNEK İKİNCİ ŞAHIS");
        result.Amount.Should().Be(20000m);
        result.PaidAt.Should().Be(new DateTime(2026, 4, 24));
        result.ReferansNo.Should().Be("3327706380");
        result.RecipientIban.Should().Be(aliciIban);
        result.RecipientName.Should().Be("DENEME YAYINCI");
    }

    [Fact]
    public void Parse_yapi_kredi_e_dekont_fast_outgoing_extracts_all_fields()
    {
        // Yapı Kredi e-Dekont FAST (giden) — "GİDEN FAST TUTARI :-35000" negatif
        // outgoing format, decimal yok. PayerName="GÖNDEREN ADI", RecipientName=
        // "ALICI ADI" boşluk padding'li label'lar. Amount abs alınır (caller için
        // pozitif tutar).
        var aliciIban = TestIban.NewTr("00111");
        var text = "e-DekontFAST GÖNDERİMİ" +
                   "İŞLEM TARİHİ:30.04.2026 15:04:56" +
                   "GİDEN FAST TUTARI :-35000                                            " +
                   "GÖNDEREN ADI      :ÖRNEK ŞAHIS                                       " +
                   "ALICI BANKA       :QNB Bank A.Ş.                                     " +
                   "SORGU NO                :2854829652                " +
                   $"ALICI HESAP       :{aliciIban}                        " +
                   "ALICI ADI         :EMAR GLOBAL TEKSTİL GIDA İNŞAAT TURİZM YAZILIM VE TİC.LTD.ŞTİ.                                       " +
                   "ALICI TCKN/VD/VKN : -";
        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Be("ÖRNEK ŞAHIS");
        result.Amount.Should().Be(35000m); // abs alındı, pozitif
        result.PaidAt.Should().Be(new DateTime(2026, 4, 30));
        result.ReferansNo.Should().Be("2854829652");
        result.RecipientIban.Should().Be(aliciIban);
        result.RecipientName.Should().Contain("EMAR GLOBAL TEKSTİL");
    }

    [Fact]
    public void Parse_vakifbank_fast_new_format_with_slash_unvan()
    {
        // Vakıfbank yeni FAST (2026 format): "GÖNDEREN AD SOYAD /UNVAN" ve
        // "ALICI HESAP NO / IBAN" (slash öncesi/sonrası boşluk var; eski
        // continuous format "GONDEREN ADSOYAD/UNVAN"dan farklı).
        var aliciIban = TestIban.NewTr("00111");
        var text = "VAKIFBANKİŞLEM BİLGİLERİİŞLEM TÜRÜFAST Giden Anlık Ödeme" +
                   "İŞLEM TARİHİ10.04.2026 12:52:11" +
                   "SORGU NO2553031025İŞLEM TUTARI80.000,00 TLMASRAF TUTARI" +
                   "GÖNDEREN AD SOYAD /UNVAN123 ÖRNEK TEKSTİLSANAYİ" +
                   "ALICI AD SOYAD/UNVANEMAR GLOBAL TEKSTİL" +
                   $"ALICI HESAP NO / IBAN{VakifbankGrouped(aliciIban)}" +
                   "İŞLEM NO2026005253222628FİŞ NO";
        var result = _parser.ParseFromText(text, FakeHash);

        result.PayerName.Should().Contain("123 ÖRNEK TEKSTİL");
        result.Amount.Should().Be(80000m);
        result.PaidAt.Should().Be(new DateTime(2026, 4, 10));
        result.ReferansNo.Should().Be("2026005253222628");
        result.RecipientIban.Should().Be(aliciIban);
        result.RecipientName.Should().Contain("EMAR GLOBAL");
    }

    [Fact]
    public void ExtractPayerName_ignores_label_without_colon()
    {
        // Vakıfbank "GONDEREN HESAP NOTR55..." — eski loose pattern
        // ("Gönderen" + whitespace) "HESAP NO"'yu PayerName olarak yutuyordu.
        // Doğru pattern colon zorunlu, label "ADSOYAD/UNVAN" lookahead'lı.
        var text = $"GONDEREN HESAP NO{VakifbankGrouped(TestIban.NewTr("00015"))}ALICI";
        var result = _parser.ParseFromText(text, FakeHash);
        result.PayerName.Should().BeNull();
    }

    // ── RecipientIban (2026-05-12) ──────────────────────────────────────

    // IBAN her koşuda üretilir: {0} bitişik, {1} dörtlü gruplu yazılış.
    [Theory]
    [InlineData("ALICI IBAN: {0}", "00205")]
    [InlineData("ALICIIsim : X IBAN/Hesap No : {1}", "00111")]
    [InlineData("Alıcı : DENEME ÖRNEK GIDA IBAN: {0}", "00111")]
    public void ExtractRecipientIban_finds_iban_in_alici_section(string format, string bankCode)
    {
        var iban = TestIban.NewTr(bankCode);
        var text = string.Format(format, iban, TestIban.Grouped(iban));

        var result = _parser.ParseFromText(text, FakeHash);
        result.RecipientIban.Should().Be(iban);
    }

    [Fact]
    public void ExtractRecipientIban_null_when_only_gonderen_iban_present()
    {
        var text = $"Gönderen: Foo IBAN: {TestIban.Grouped(TestIban.NewTr("00111"))}";
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
    [InlineData("ALICI ÜNVANI: DENEME ÖRNEK GIDA   ALICI IBAN: TR...", "DENEME ÖRNEK GIDA")]
    [InlineData("ALICIIsim              : DENEME YAYINCIIBAN/Hesap No", "DENEME YAYINCI")]
    [InlineData("Alıcı : DENEME ÖRNEK GIDA Kuveyt Türk Katılım", "DENEME ÖRNEK GIDA")]
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
    [InlineData("Deneme Örnek Gıda", "Deneme Örnek Gıda", true)]
    [InlineData("Deneme Örnek Gıda", "DENEME ÖRNEK GIDA", true)]   // case-insensitive
    [InlineData("Deneme Örnek Gıda", "Deneme Örnek Gida", true)]   // Türkçe ı→i normalize
    [InlineData("Deneme Örnek Gıda", "DENEME ÖRNEK GIDA Kuveyt Türk Katılım", true)]   // substring
    [InlineData("Deneme Örnek Gıda", "Kurgu Şahıs", false)]
    public void NormalizeName_supports_case_and_turkish_compare(
        string vendor, string pdf, bool expectsMatch)
    {
        var v = PdfDekontParser.NormalizeName(vendor);
        var p = PdfDekontParser.NormalizeName(pdf);
        var match = !string.IsNullOrEmpty(v) && (p.Contains(v) || v.Contains(p));
        match.Should().Be(expectsMatch);
    }

    /// <summary>Vakıfbank dekontlarındaki IBAN yazılışı: 4-4-4-4-8-2 gruplar.</summary>
    private static string VakifbankGrouped(string iban)
        => $"{iban[..4]} {iban[4..8]} {iban[8..12]} {iban[12..16]} {iban[16..24]} {iban[24..]}";
}
