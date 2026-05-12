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
}
