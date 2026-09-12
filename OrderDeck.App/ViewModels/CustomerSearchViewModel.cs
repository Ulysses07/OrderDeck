using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.ViewModels;

public sealed partial class CustomerSearchViewModel : ViewModelBase
{
    private readonly CustomerRepository _customers;
    private readonly CustomerService _customerService;
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;
    private readonly PaymentRequestService _paymentService;
    private readonly IDialogService _dialogService;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string? _platformFilter;
    [ObservableProperty] private bool _lastStreamShoppersOnly;
    [ObservableProperty] private bool _registeredOnly;

    // Üst bant sayaçları (filtreden bağımsız, tüm DB'den).
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _registeredCount;
    /// <summary>Bu yayında (uygulama açıkken) forma gelen yeni kayıt sayısı —
    /// zil'e basıldığında MainShell'den geçirilir.</summary>
    [ObservableProperty] private bool _hasNewThisSession;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasNewThisSession))] private int _newThisSessionCount;

    partial void OnNewThisSessionCountChanged(int value) => HasNewThisSession = value > 0;

    /// <summary>Arama sonucu üst sınırı.</summary>
    private const int SearchLimit = 50;

    /// <summary>Arama kutusu boşken gösterilen "en yeniler" listesinin üst sınırı.
    /// Sınırsız liste tüm Customer tablosunu belleğe alıyordu (R3-04).</summary>
    private const int RecentLimit = 500;

    /// <summary>Görünen sonuç sayısı (aktif filtreye göre).</summary>
    public int ResultCount => Results.Count;

    private bool _resultsTruncated;

    /// <summary>Liste altındaki bilgi satırı. Sonuç sınıra dayandıysa bunu
    /// AÇIKÇA yazar — sessizce eksik liste göstermek, operatörün "yok" sandığı
    /// bir kaydın aslında listenin dışında kalması demek olurdu.</summary>
    public string ResultSummary => _resultsTruncated
        ? $"{ResultCount} sonuç gösteriliyor (en yeniler) — daha eskisi için arayın"
        : $"{ResultCount} sonuç";

    /// <summary>Platform süzgeci seçenekleri (UI ComboBox). Value boş = tümü.
    /// Not: form kayıtları artık gerçek platform satırları (Platform="form" yok);
    /// "kayıt olan" için RegisteredOnly filtresi kullanılır.</summary>
    public IReadOnlyList<PlatformOption> PlatformOptions { get; } = new[]
    {
        new PlatformOption("Tüm platformlar", ""),
        new PlatformOption("Instagram", "instagram"),
        new PlatformOption("YouTube", "youtube"),
        new PlatformOption("Facebook", "facebook"),
        new PlatformOption("TikTok", "tiktok"),
    };

    public sealed record PlatformOption(string Display, string Value);

    /// <summary>Aynı kişinin (GroupId) birden fazla platform satırını tek kartta
    /// birleştiren görünüm modeli. GroupId null olan müşteri kendi tekil kartıdır.</summary>
    public sealed class CustomerCard
    {
        public required Customer Primary { get; init; }
        public required IReadOnlyList<Customer> Members { get; init; }

        public string? GroupId => Primary.GroupId;
        public string Display => Primary.Display;
        public string? Phone => Members
            .Select(m => m.Phone).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        public string? Email => Members
            .Select(m => m.Email).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));
        public long LastSeenAt => Members.Max(m => m.LastSeenAt);
        public decimal TotalAmount => Members.Sum(m => m.TotalAmount);

        /// <summary>Kartın temsil ettiği tüm platformlar (benzersiz, küçük harf).</summary>
        public IReadOnlyList<string> Platforms => Members
            .Select(m => m.Platform)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        /// <summary>Birden fazla platform satırı birleştirilmiş mi (Ayır butonu için).</summary>
        public bool IsGroup => Members.Count > 1;
    }

    public ObservableCollection<CustomerCard> Results { get; } = new();

    /// <summary>Çoklu seçim (Ctrl+tık) ile birleştirilecek kartlar; code-behind'dan
    /// ListBox.SelectedItems senkronize edilir.</summary>
    public ObservableCollection<CustomerCard> SelectedCards { get; } = new();

    private readonly Dictionary<string, decimal> _streamAmounts = new();

    public CustomerSearchViewModel(
        CustomerRepository customers,
        CustomerService customerService,
        SessionRepository sessions,
        LabelRepository labels,
        PaymentRequestService paymentService,
        IDialogService dialogService)
    {
        _customers = customers;
        _customerService = customerService;
        _sessions = sessions;
        _labels = labels;
        _paymentService = paymentService;
        _dialogService = dialogService;

        SelectedCards.CollectionChanged += (_, _) => MergeSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnQueryChanged(string value) => RefreshSearch();
    partial void OnPlatformFilterChanged(string? value) => RefreshSearch();
    partial void OnLastStreamShoppersOnlyChanged(bool value) => RefreshSearch();
    partial void OnRegisteredOnlyChanged(bool value) => RefreshSearch();

    /// <summary>Phase 4f: external trigger to re-run search after PlatformFilter changes.</summary>
    public void RefreshSearch()
    {
        // Üst bant sayaçları (filtreden bağımsız).
        TotalCount = _customers.CountAll();
        RegisteredCount = _customers.CountRegistered();
        ApplySearch(Query);
        OnPropertyChanged(nameof(ResultCount));
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void ApplySearch(string value)
    {
        Results.Clear();
        SelectedCards.Clear();
        _streamAmounts.Clear();
        _resultsTruncated = false;

        if (LastStreamShoppersOnly)
        {
            var shoppers = _customerService.GetLastStreamShoppers();
            var session = _sessions.GetLatestEnded();
            if (session is not null)
            {
                var top = _labels.GetTopCustomersBySession(session.Id, int.MaxValue);
                foreach (var t in top)
                {
                    var c = shoppers.FirstOrDefault(s => s.Platform == t.Platform && s.Username == t.Username);
                    if (c is not null) _streamAmounts[c.Id] = t.TotalAmount;
                }
            }

            IEnumerable<Customer> filtered = shoppers;
            if (!string.IsNullOrWhiteSpace(value))
            {
                var q = value.Trim();
                filtered = filtered.Where(c => CustomerSearch.Matches(c, q));
            }
            if (!string.IsNullOrEmpty(PlatformFilter))
                filtered = filtered.Where(c => c.Platform == PlatformFilter);
            if (RegisteredOnly)
                filtered = filtered.Where(c => !string.IsNullOrWhiteSpace(c.Phone));

            // R5-02: Kart bir KİŞİ; "son yayında alışveriş yapanlar" listesi ise
            // SATIR seçiyor. Kişinin başka platformdaki satırı bu listede yoksa
            // kartın toplamı eksik çıkar — ödeme komutu o toplamı kullanıyor.
            foreach (var card in BuildCards(_customers.CompleteGroups(filtered.ToList())))
                Results.Add(card);
            return;
        }

        // Boş sorgu: operatör henüz siparişi olmayan yeni kayıtları (shopper
        // app'ten gelenler) görebilsin diye varsayılan liste gösterilir —
        // eskiden burası boş dönüyordu ve kayıtlı müşteri biri yazana kadar
        // görünmezdi.
        //
        // R3-04: liste EN YENİ RecentLimit satırla sınırlı. Sınırsız hâli tüm
        // tabloyu belleğe alıyordu; sıralama zaten LastSeenAt DESC olduğu için
        // sınır listenin amacını bozmaz, daha eskisi arama kutusunun işi.
        // Kesildiğinde ResultSummary bunu açıkça yazar.
        //
        // R3-03: platform/kayıtlı süzgeçleri her iki yolda da SQL'in İÇİNDE,
        // limit'ten ÖNCE uygulanır — limit'ten sonra dışarıda süzmek, süzgece
        // uyan ama ilk N genel satırın dışındaki kaydı yanlış boş sonuçla
        // kaybediyordu.
        var rows = string.IsNullOrWhiteSpace(value)
            ? _customers.GetRecent(RecentLimit, PlatformFilter, RegisteredOnly)
            : _customers.Search(value.Trim(), SearchLimit, PlatformFilter, RegisteredOnly);

        // Kesme sinyali TAMAMLAMADAN ÖNCE ölçülür: tamamlama satır ekleyebildiği
        // için sonradan bakmak kesilmemiş listeyi "kesildi" diye işaretlerdi.
        _resultsTruncated = rows.Count >= (string.IsNullOrWhiteSpace(value) ? RecentLimit : SearchLimit);

        // R5-02: Limit satırı kesiyor, kart ise grubu topluyor. Tamamlamadan
        // çizilen kart, görünür olmasına rağmen kendi toplamını eksik gösterir.
        foreach (var card in BuildCards(_customers.CompleteGroups(rows))) Results.Add(card);
    }

    /// <summary>Müşteri satırlarını GroupId'ye göre tek karta toplar. GroupId null
    /// olanlar tekil kart olur. Kart içindeki "Primary" (birincil) satır: telefonlu
    /// (kayıtlı) satır varsa o, yoksa en çok alışveriş yapan satır — böylece kart
    /// başlığı kişinin gerçek adını ve iletişim bilgisini yansıtır. Sıralama son
    /// görülmeye (grup içi en yeni) göre azalan.</summary>
    private static IEnumerable<CustomerCard> BuildCards(IEnumerable<Customer> customers)
    {
        var list = customers.ToList();
        var cards = new List<CustomerCard>();

        // GroupId null olanları kendi Id'siyle tekilleştir (grup anahtarı çakışmasın).
        var groups = list.GroupBy(c => string.IsNullOrWhiteSpace(c.GroupId) ? "id:" + c.Id : "grp:" + c.GroupId);
        foreach (var g in groups)
        {
            var members = g.ToList();
            var primary =
                members.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Phone))
                ?? members.OrderByDescending(m => m.TotalAmount).First();
            cards.Add(new CustomerCard { Primary = primary, Members = members });
        }

        return cards.OrderByDescending(c => c.LastSeenAt);
    }

    /// <summary>Seçilen kartlardaki tüm platform satırlarını tek gruba bağlar.</summary>
    [RelayCommand(CanExecute = nameof(CanMergeSelected))]
    private void MergeSelected()
    {
        var ids = SelectedCards
            .SelectMany(card => card.Members.Select(m => m.Id))
            .Distinct()
            .ToList();
        if (ids.Count < 2) return;

        _customers.MergeIntoGroup(ids);
        RefreshSearch();
    }

    private bool CanMergeSelected() => SelectedCards.Count >= 2;

    /// <summary>Grubu tekil satırlara ayırır (yanlış birleştirmeyi geri alır).</summary>
    [RelayCommand]
    private void UnmergeGroup(CustomerCard? card)
    {
        if (card?.GroupId is not { Length: > 0 } groupId) return;
        _customers.UnmergeGroup(groupId);
        RefreshSearch();
    }

    [RelayCommand]
    private async Task OpenWhatsAppAsync(CustomerCard? card)
    {
        if (card is null) return;
        var customer = card.Primary;

        // Tutar: yayın-içi filtre aktifse grubun üyelerinin bu-yayın tutarları
        // toplanır; değilse kartın kümülatif toplamı.
        var streamSum = card.Members.Sum(m =>
            _streamAmounts.TryGetValue(m.Id, out var perStream) ? perStream : 0m);
        var amount = streamSum > 0m ? streamSum : card.TotalAmount;

        var session = _sessions.GetLatestEnded();
        var streamDate = session?.EndedAt is long ended
            ? DateTimeOffset.FromUnixTimeSeconds(ended).LocalDateTime
            : DateTime.Now;

        var result = await RequestPaymentAsync(customer);

        if (result == PaymentRequestResult.PhoneRequired)
        {
            var saved = await _dialogService.ShowPhoneEntryAsync(customer.Id);
            if (saved)
            {
                var updated = _customers.GetById(customer.Id);
                if (updated is not null)
                    await RequestPaymentAsync(updated);
            }
        }
        else if (result == PaymentRequestResult.LaunchFailed)
        {
            _dialogService.ShowError("WhatsApp açılamadı. WhatsApp Desktop kurulu mu?");
        }
        else if (result == PaymentRequestResult.SendPending)
        {
            // Sunucu "aynı gönderim işleniyor" dedi: mesaj gitmiş de olabilir,
            // hiç gitmemiş de. Sessiz kalırsak operatör gittiğini varsayar;
            // otomatik wa.me açarsak çift mesaj riski var. Kararı ona bırakıyoruz.
            _dialogService.ShowInfo(
                "Gönderim işleniyor — WhatsApp'ta ulaştığını doğrulayın, aksi halde tekrar deneyin.");
        }
        else if (result == PaymentRequestResult.BalanceUncertain)
        {
            // R2-01..03: düşümün sonucu kesinleşmedi, mesaj GÖNDERİLMEDİ.
            // Tekrar deneme aynı anahtarla replay yapar — çift düşüm imkânsız.
            //
            // Hata değil UYARI: kaybedilmiş bir işlem yok ve operatörün
            // yapabileceği somut bir şey var. Kırmızı hata diyaloğu "bir şey
            // bozuldu" izlenimi veriyordu; asıl endişeyi ("bakiye iki kez mi
            // düştü?") metnin sonundaki güvence kapatıyor.
            _dialogService.Show(
                "Sunucudan kesin cevap alınamadı; mesaj gönderilmedi. " +
                "Bağlantıyı kontrol edip tekrar deneyin — çift düşüm olmaz.",
                "Bakiye doğrulanamadı",
                DialogSeverity.Warning);
        }

        // Kapsam: yayın-içi tutar kullanıldıysa satış o oturuma, değilse
        // müşterinin kümülatif bakiyesine aittir.
        async Task<PaymentRequestResult> RequestPaymentAsync(Customer c) =>
            await _paymentService.OpenWhatsAppAsync(
                c, amount, streamDate,
                streamSum > 0m && session is not null ? $"session:{session.Id}" : "cumulative");
    }
}
