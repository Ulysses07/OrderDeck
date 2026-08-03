using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using ClosedXML.Excel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.Core;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using Microsoft.Win32;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Dönem raporu: seçilen tarih aralığında alışveriş yapan kişilerin listesi ve
/// muhasebenin entegratöre yükleyeceği e-Arşiv toplu fatura dosyası.
/// Yayın raporundan farkı, tek yayına değil takvim aralığına bakması.
/// </summary>
public sealed partial class PeriodReportViewModel : ViewModelBase
{
    private readonly LabelRepository _labels;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;

    [ObservableProperty] private DateTime _fromDate = FirstDayOfCurrentMonth();
    [ObservableProperty] private DateTime _toDate = FirstDayOfCurrentMonth().AddMonths(1).AddDays(-1);
    [ObservableProperty] private int _personCount;
    [ObservableProperty] private int _completeCount;
    [ObservableProperty] private int _orderCount;
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private int _invoiceCount;
    [ObservableProperty] private bool _onlyInvoiceReady;

    // e-Fatura şablon alanları — ayarlardan yüklenir, dışa aktarımda geri yazılır.
    [ObservableProperty] private string _numberPrefix = "";
    [ObservableProperty] private string _nextNumberText = "";
    [ObservableProperty] private string _itemName = "";
    [ObservableProperty] private decimal _vatRate;

    /// <summary>Ekranda gösterilen kişi listesi — <see cref="OnlyInvoiceReady"/>
    /// filtresi uygulanmış hâli.</summary>
    public ObservableCollection<PeriodCustomerRow> People { get; } = new();

    private IReadOnlyList<PeriodAccountRow> _rawRows = Array.Empty<PeriodAccountRow>();
    private IReadOnlyList<PeriodCustomerRow> _allPeople = Array.Empty<PeriodCustomerRow>();
    private IReadOnlyList<PeriodInvoiceRow> _invoices = Array.Empty<PeriodInvoiceRow>();

    public int MissingCount => PersonCount - CompleteCount;

    public PeriodReportViewModel(LabelRepository labels, AppSettings settings, SettingsStore settingsStore)
    {
        _labels = labels;
        _settings = settings;
        _settingsStore = settingsStore;

        NumberPrefix = settings.EInvoice.NumberPrefix;
        NextNumberText = settings.EInvoice.NextNumber > 0
            ? settings.EInvoice.NextNumber.ToString()
            : "";
        ItemName = settings.EInvoice.ItemName;
        VatRate = settings.EInvoice.VatRate;
    }

    private static DateTime FirstDayOfCurrentMonth()
    {
        var now = DateTime.Today;
        return new DateTime(now.Year, now.Month, 1);
    }

    /// <summary>Yerel gün başlangıcını unix saniyeye çevirir. Etiket zaman
    /// damgaları UTC saniye; operatör ise takvim günü seçiyor.</summary>
    private static long ToUnix(DateTime localDay) =>
        new DateTimeOffset(DateTime.SpecifyKind(localDay, DateTimeKind.Local)).ToUnixTimeSeconds();

    [RelayCommand]
    private void Load()
    {
        // Aralık kullanıcıya gün bazında sorulur; sorgu [from, to) çalışır,
        // o yüzden bitiş gününün SONU = ertesi günün başlangıcı.
        _rawRows = _labels.GetPeriodAccountRows(ToUnix(FromDate.Date), ToUnix(ToDate.Date.AddDays(1)));

        _allPeople = PeriodReportBuilder.Build(_rawRows);
        _invoices = PeriodReportBuilder.BuildInvoices(_rawRows);

        PersonCount = _allPeople.Count;
        CompleteCount = _allPeople.Count(p => p.HasInvoiceInfo);
        OrderCount = _allPeople.Sum(p => p.OrderCount);
        TotalAmount = _allPeople.Sum(p => p.TotalAmount);
        InvoiceCount = _invoices.Count;
        OnPropertyChanged(nameof(MissingCount));

        ApplyFilter();
    }

    partial void OnOnlyInvoiceReadyChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        People.Clear();
        foreach (var p in OnlyInvoiceReady ? _allPeople.Where(p => p.HasInvoiceInfo) : _allPeople)
            People.Add(p);
    }

    [RelayCommand]
    private void ExportToExcel()
    {
        if (_allPeople.Count == 0)
        {
            MessageBox.Show("Aktarılacak kayıt yok. Önce raporu oluşturun.",
                "Dönem Raporu", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        long? startNumber = null;
        if (!string.IsNullOrWhiteSpace(NextNumberText))
        {
            if (!long.TryParse(NextNumberText.Trim(), out var parsed) || parsed < 0)
            {
                MessageBox.Show("Fatura başlangıç numarası sayı olmalı.",
                    "Geçersiz numara", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            startNumber = parsed;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "Excel Workbook|*.xlsx",
            FileName = $"orderdeck-donem-{FromDate:yyyy-MM-dd}_{ToDate:yyyy-MM-dd}.xlsx",
            InitialDirectory = AppPaths.ReportsFolder
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            using var wb = new XLWorkbook();
            WriteInvoiceSheet(wb.Worksheets.Add("e-Fatura"), startNumber);
            WriteDetailSheet(wb.Worksheets.Add("Detay"));
            wb.SaveAs(dlg.FileName);

            PersistEInvoiceSettings(startNumber);

            MessageBox.Show(
                $"Rapor kaydedildi:\n{dlg.FileName}\n\n" +
                $"e-Fatura satırı: {_invoices.Count}\n" +
                $"Adı olmadığı için faturaya girmeyen kişi: {MissingCount}",
                "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Excel'e aktarma başarısız: {ex.Message}",
                "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Muhasebenin entegratöre yüklediği e-Arşiv şablonu. Sütun sırası ve
    /// başlıkları BİREBİR sabit — şablon sütun adına göre okunuyor, sıra veya
    /// yazım değişirse yükleme reddedilir. Doldurulmayan sütunlar (adres,
    /// telefon, e-posta, irsaliye, ÖTV vs.) örnekte de boş; başlıkları yine de
    /// yazılıyor ki dosya şablonla aynı şekle sahip olsun.
    /// </summary>
    private void WriteInvoiceSheet(IXLWorksheet ws, long? startNumber)
    {
        for (int i = 0; i < EInvoiceTemplate.Headers.Length; i++)
            ws.Cell(1, i + 1).Value = EInvoiceTemplate.Headers[i];
        ws.Row(1).Style.Font.Bold = true;

        var prefix = NumberPrefix.Trim();
        var digits = _settings.EInvoice.NumberDigits;
        var defaultTckn = _settings.EInvoice.DefaultTckn?.Trim() ?? "";

        int row = 2;
        int id = 1;
        foreach (var inv in _invoices)
        {
            ws.Cell(row, EInvoiceTemplate.Id).Value = id;

            if (prefix.Length > 0 && startNumber is long n)
                ws.Cell(row, EInvoiceTemplate.InvoiceNumber).Value =
                    prefix + (n + id - 1).ToString().PadLeft(digits, '0');

            ws.Cell(row, EInvoiceTemplate.InvoiceDate).Value = inv.IssuedAt.Date;
            ws.Cell(row, EInvoiceTemplate.InvoiceDate).Style.DateFormat.Format = "dd.MM.yyyy";
            ws.Cell(row, EInvoiceTemplate.InvoiceTime).Value = inv.IssuedAt.TimeOfDay;
            ws.Cell(row, EInvoiceTemplate.InvoiceTime).Style.DateFormat.Format = "h:mm:ss";

            ws.Cell(row, EInvoiceTemplate.InvoiceType).Value = "SATIS";
            ws.Cell(row, EInvoiceTemplate.InvoiceProfile).Value = "TEMELFATURA";
            ws.Cell(row, EInvoiceTemplate.CurrencyCode).Value = "TRY";

            // TCKN sayı olarak yazılıyor (örnek dosyada da öyle). TC kimlik
            // numarası sıfırla başlayamadığı için sayıya çevirmek güvenli.
            var tckn = string.IsNullOrWhiteSpace(inv.Tckn) ? defaultTckn : inv.Tckn.Trim();
            if (long.TryParse(tckn, out var tcknNumber))
                ws.Cell(row, EInvoiceTemplate.BuyerTckn).Value = tcknNumber;
            else if (tckn.Length > 0)
                ws.Cell(row, EInvoiceTemplate.BuyerTckn).Value = tckn;

            ws.Cell(row, EInvoiceTemplate.BuyerFirstName).Value = inv.FirstName;
            ws.Cell(row, EInvoiceTemplate.BuyerLastName).Value = inv.LastName;
            ws.Cell(row, EInvoiceTemplate.BuyerCountry).Value = "TÜRKİYE";
            ws.Cell(row, EInvoiceTemplate.DeliveryType).Value = "ELEKTRONİK";

            ws.Cell(row, EInvoiceTemplate.ItemName).Value = ItemName;
            ws.Cell(row, EInvoiceTemplate.Quantity).Value = 1;
            ws.Cell(row, EInvoiceTemplate.UnitCode).Value = "ADET";
            ws.Cell(row, EInvoiceTemplate.VatRate).Value = VatRate;
            ws.Cell(row, EInvoiceTemplate.TotalWithTax).Value = inv.TotalAmount;
            ws.Cell(row, EInvoiceTemplate.TotalWithTax).Style.NumberFormat.Format = "0.00";

            row++;
            id++;
        }
    }

    /// <summary>Operatörün kontrol ettiği okunabilir liste — dönemin tamamı
    /// için kişi bazlı. Faturaya girmeyenler de burada, "Eksik" işaretiyle.</summary>
    private void WriteDetailSheet(IXLWorksheet ws)
    {
        ws.Cell(1, 1).Value = $"Dönem Raporu — {FromDate:dd.MM.yyyy} / {ToDate:dd.MM.yyyy}";
        ws.Cell(1, 1).Style.Font.Bold = true;

        ws.Cell(2, 1).Value = "Kişi";           ws.Cell(2, 2).Value = _allPeople.Count;
        ws.Cell(3, 1).Value = "Sipariş";        ws.Cell(3, 2).Value = _allPeople.Sum(p => p.OrderCount);
        ws.Cell(4, 1).Value = "Toplam tutar";   ws.Cell(4, 2).Value = _allPeople.Sum(p => p.TotalAmount);
        ws.Cell(4, 2).Style.NumberFormat.Format = "#,##0.00 \"TL\"";
        ws.Cell(5, 1).Value = "e-Fatura satırı"; ws.Cell(5, 2).Value = _invoices.Count;

        int row = 7;
        string[] headers =
        {
            "#", "Ad Soyad", "TCKN", "Telefon", "Adres", "E-posta",
            "Platform / Kullanıcı", "Alışveriş Günü", "Sipariş Adedi",
            "Toplam Tutar (TL)", "Fatura Bilgisi"
        };
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(row, i + 1).Value = headers[i];
        ws.Range(row, 1, row, headers.Length).Style.Font.Bold = true;
        row++;

        int no = 1;
        foreach (var p in _allPeople)
        {
            ws.Cell(row, 1).Value = no++;
            ws.Cell(row, 2).Value = p.FullName ?? p.DisplayLabel;
            // TCKN/telefon metin olarak: 11 hane sayı olursa Excel bilimsel
            // gösterime çevirip baştaki sıfırı yutuyor.
            ws.Cell(row, 3).Value = p.Tckn ?? "";
            ws.Cell(row, 3).Style.NumberFormat.Format = "@";
            ws.Cell(row, 4).Value = p.Phone ?? "";
            ws.Cell(row, 4).Style.NumberFormat.Format = "@";
            ws.Cell(row, 5).Value = p.Address ?? "";
            ws.Cell(row, 6).Value = p.Email ?? "";
            ws.Cell(row, 7).Value = p.Accounts;
            ws.Cell(row, 8).Value = p.DayCount;
            ws.Cell(row, 9).Value = p.OrderCount;
            ws.Cell(row, 10).Value = p.TotalAmount;
            ws.Cell(row, 10).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 11).Value = p.InvoiceStatusLabel;
            row++;
        }

        ws.Columns().AdjustToContents();
    }

    /// <summary>Bir sonraki raporda kaldığı yerden devam edebilmek için önek,
    /// sayaç ve şablon sabitlerini kaydeder. Sayaç yalnızca gerçekten numara
    /// üretildiyse ilerletilir.</summary>
    private void PersistEInvoiceSettings(long? startNumber)
    {
        var prefix = NumberPrefix.Trim();
        _settings.EInvoice.NumberPrefix = prefix;
        _settings.EInvoice.ItemName = ItemName;
        _settings.EInvoice.VatRate = VatRate;

        if (prefix.Length > 0 && startNumber is long n && _invoices.Count > 0)
        {
            _settings.EInvoice.NextNumber = n + _invoices.Count;
            NextNumberText = _settings.EInvoice.NextNumber.ToString();
        }

        _settingsStore.Save(_settings);
    }
}

/// <summary>
/// e-Arşiv toplu yükleme şablonunun sütun düzeni. Başlık metinleri ve sıra
/// muhasebenin gönderdiği örnek dosyadan birebir alındı — entegratör dosyayı
/// bu şekle göre okuyor, değiştirilmemeli.
/// </summary>
internal static class EInvoiceTemplate
{
    public const int Id = 1;
    public const int InvoiceNumber = 2;
    public const int InvoiceDate = 4;
    public const int InvoiceTime = 5;
    public const int InvoiceType = 6;
    public const int InvoiceProfile = 7;
    public const int CurrencyCode = 13;
    public const int BuyerTckn = 24;
    public const int BuyerFirstName = 25;
    public const int BuyerLastName = 26;
    public const int BuyerCountry = 27;
    public const int DeliveryType = 45;
    public const int ItemName = 53;
    public const int Quantity = 54;
    public const int UnitCode = 55;
    public const int VatRate = 57;
    public const int TotalWithTax = 58;

    public static readonly string[] Headers =
    {
        "Id",
        "Fatura Numarası",
        "ETTN",
        "Fatura Tarihi",
        "Fatura Saati",
        "Fatura Tipi",
        "Fatura Profili",
        "e-Arşiv İhracat Mı?",
        "Not1",
        "Not2",
        "Not3",
        "Not4",
        "Döviz Kodu",
        "Döviz Kuru",
        "İade Tarihi",
        "İade Fatura Numarası",
        "Sipariş Tarihi",
        "Yatırım Teşvik Belge Numarası",
        "Yatırım Teşvik Belge Tarihi",
        "Sevkiyat Numarası",
        "Sipariş Numarası",
        "İrsaliye Numarası",
        "İrsaliye Tarihi",
        "Alıcı VKN/TCKN",
        "Alıcı Ünvan/Adı | Yabancı Alıcı Ünvan/Adı | Turist Adı",
        "Alıcı Soyadı | Yabancı Alıcı Soyadı | Turist Soyadı ",
        "Alıcı Ülke | Yabancı Ülke | Turist Ülke",
        "Alıcı Şehir | Yabancı Şehir | Turist Şehir",
        "Alıcı İlçe | Yabancı İlçe | Turist İlçe",
        "Alıcı Sokak | Yabancı Sokak | Turist Sokak",
        "Alıcı Bina No | Yabancı Bina No | Turist Bina No",
        "Alıcı Kapı No | Yabancı Kapı No | Turist Kapı No",
        "Alıcı Eposta | Yabancı Eposta | Turist Eposta",
        "Alıcı Telefon | Yabancı Telefon | Turist Telefon",
        "Alıcı Vergi Dairesi",
        "Alıcı Posta Kutusu",
        "Yabancı Alıcı Ülkesindeki VKN",
        "Yabancı Alıcı Resmi Ünvan",
        "Turist Ülke Kodu",
        "Turist Pasaport No",
        "Pasaport Veriliş Tarihi",
        "Aracı Kurum Posta Kutusu",
        "Aracı Kurum VKN",
        "Aracı Kurum Adı",
        "Gönderim Türü",
        "Satışın Yapıldığı Web Sitesi",
        "Ödeme Tarihi",
        "Ödeme Türü",
        "Ödeyen Adı",
        "Taşıyıcı Ünvanı",
        "Taşıyıcı Tckn/Vkn",
        "Gönderim Tarihi",
        "Mal/Hizmet Adı",
        "Miktar",
        "Birim Kodu",
        "Birim Fiyat",
        "KDV Oranı",
        "Vergiler Dahil Tutar",
        "Vazgeçilen KDV Oranı",
        "KDV Muafiyet Kodu",
        "KDV Muafiyet Nedeni",
        "İskonto Oranı",
        "İskonto Açıklaması",
        "İskonto Oranı 2",
        "İskonto Açıklaması 2",
        "Harcama Tipi",
        "Makine Adı",
        "Makine ID",
        "Makine Teçhizat Sıra No",
        "Satıcı Kodu (SellersItemIdentification)",
        "Alıcı Kodu (BuyersItemIdentification)",
        "Üretici Kodu (ManufacturersItemIdentification)",
        "Marka (BrandName)",
        "Model (ModelName)",
        "Menşei Kodu",
        "Mal/Hizmet İrsaliye Numarası",
        "Mal/Hizmet İrsaliye Tarihi",
        "Mal/Hizmet Sipariş Numarası ",
        "Mal/Hizmet Sipariş Tarihi ",
        "Açıklama (Description)",
        "Not (Note)",
        "Etiket No",
        "Artırım Oranı",
        "Artırım Tutarı",
        "ÖTV Kodu",
        "ÖTV Oranı",
        "ÖTV Tutarı",
        "Tevkifat Kodu",
        "Tevkifat Oranı",
        "BSMV Oranı",
        "Enerji Fonu Vergi Oranı",
        "TRT Payı Vergi Oranı",
        "Elektrik ve Havagazı Tüketim Vergisi Oranı",
        "Konaklama Vergisi Oranı",
        "GTip No",
        "Teslim Şartı",
        "Gönderilme Şekli",
        "Gümrük Takip No",
        "Bulunduğu Kabın Markası",
        "Bulunduğu Kabın Cinsi",
        "Bulunduğu Kabın Numarası",
        "Bulunduğu Kabın Adedi",
        "İhracat Teslim ve Ödeme Yeri/Ülke",
        "İhracat Teslim ve Ödeme Yeri/Şehir",
        "İhracat Teslim ve Ödeme Yeri/Mahalle/İlçe",
        "Künye No",
        "Mal Sahibi Ad/Soyad/Ünvan",
        "Mal Sahibi Vkn/Tckn",
        "Mal Kalemi Brüt Kilogram",
        "Mal Kalemi Net Kilogram",
        "Fatura Teslim ve Ödeme Yeri/Ülke",
        "Fatura Teslim ve Ödeme Yeri/Şehir",
        "Fatura Teslim ve Ödeme Yeri/Mahalle/İlçe",
        "Fatura Teslim ve Ödeme Yeri/Kasaba/Köy",
        "Fatura Teslim ve Ödeme Yeri/Cadde/Sokak",
        "Fatura Teslim ve Ödeme Yeri/Posta Kodu",
        "Fatura Teslim ve Ödeme Yeri/Bina Adı",
        "Fatura Teslim ve Ödeme Yeri/Bina No",
        "Fatura Teslim ve Ödeme Yeri/Kapı No",
        "Fatura Teslim ve Ödeme Yeri/Teslim Şartı",
        "Fatura Teslim ve Ödeme Yeri/Gönderilme Şekli",
        "Toplam Kap Adedi",
        "Toplam Brüt Kilogram",
        "Toplam Net Kilogram",
        "İlaç / Tıbbi Cihaz / Diğer Ürün-Hizmet ",
        "GTIN No",
        "Parti Numarası",
        "Sıra Numarası",
        "Son Kullanma Tarihi",
        "UNO (Ürün Numarası)",
        "LNO (Lot/Batch Numarası)",
        "SNO (Seri/Sıra Numarası)",
        "URT (Üretim Tarihi)",
        "Teknolojik Cihaz Desteği",
        "IMEI1",
        "IMEI2",
        "IMEI3",
        "IMEI4"
    };
}
