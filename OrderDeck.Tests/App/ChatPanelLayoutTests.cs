using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.App.Views.Shell;
using OrderDeck.Core.Chat;

namespace OrderDeck.Tests.App;

/// <summary>
/// Sohbet panelinin gerçek WPF bağları (spec 2026-11-16 §8.6.2): satır şablonu gerçek
/// <see cref="ChatMessageViewModel"/> ile çizilir, görünen metin ve görünürlük doğrulanır. Yanlış yazılmış bir bağ
/// yolunda WPF hata vermez, alan boş görünür; örnekleme testi bunu yakalamaz. Hesap adları üretilir.
/// </summary>
public class ChatPanelLayoutTests
{
    public sealed class ChatStub     // panelin bağlandığı kabuk yüzeyi (bağlamaların okuduğu adlarla); WPF bağlaması public tip ister
    {
        public ObservableCollection<ChatMessageViewModel> ChatView { get; } = new();
        public string ChatSearchText { get; set; } = "";
        public string ActiveCode { get; set; } = "";
        public bool OnlyActiveCode { get; set; }
    }

    private static void OnSta(Action body) => ThemeTestHost.RunOnSta(body).Should().BeNull();

    /// <summary>Üretilmiş izleyici ve metinle bir Instagram satırı. Ad kısa tutulur: ad alanının tabanına sığar,
    /// böylece mesajın başladığı x konumu ada değil sınanan şeye (hesap etiketi) bağlı kalır.</summary>
    private static ChatMessageViewModel Row(string? sourceAccount) => new(
        new ChatMessage(Guid.NewGuid().ToString("N"), "instagram", null, $"izleyici-{Guid.NewGuid():N}"[..12], null, null,
            $"mesaj {Guid.NewGuid():N}", 1_800_000_000, Array.Empty<string>(), sourceAccount), isSenderBlacklisted: false);

    /// <summary>Paneli 700x400'e (ya da verilen genişliğe) yerleştirir: ölçüm, bekleyen bağlamalar, yeniden ölçüm
    /// (<see cref="ShellBannersLayoutTests"/> ile aynı kalıp).</summary>
    private static ChatPanel Layout(ChatStub context, double width = 700)
    {
        var size = new Size(width, 400);
        var panel = new ChatPanel { DataContext = context, Width = size.Width, Height = size.Height };
        panel.Measure(size);
        panel.Arrange(new Rect(size));
        ThemeTestHost.Pump();                                   // bağlamalar etkinleşir
        panel.UpdateLayout();
        panel.Measure(size);
        panel.Arrange(new Rect(size));
        panel.UpdateLayout();
        return panel;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)    // görsel ağaç
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deep in Descendants(child)) yield return deep;
        }
    }

    /// <summary>Aynı metin ve yazı tipiyle, genişlik sınırı olmadan ölçüm.</summary>
    private static double NaturalWidth(TextBlock t) => NaturalWidth(t.Text, t);

    /// <summary>Verilen metnin, <paramref name="font"/> ile aynı yazı tipinde, genişlik sınırı olmadan ölçümü.</summary>
    private static double NaturalWidth(string text, TextBlock font)
    {
        var probe = new TextBlock
        {
            Text = text, FontFamily = font.FontFamily, FontSize = font.FontSize,
            FontStyle = font.FontStyle, FontWeight = font.FontWeight, FontStretch = font.FontStretch,
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private static TextBlock Bound(ChatPanel panel, ChatMessageViewModel row, string path) =>      // satırın, Text bağı o yola giden tek TextBlock'u
        Descendants(panel).OfType<TextBlock>().Single(t => ReferenceEquals(t.DataContext, row)
            && BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == path);

    [Fact]                                                             // XAML bağ yolu gerçek ViewModel ile çizilerek sınanır (yanlış yol hata vermez, alan boş kalır)
    public void Etiketli_satirda_etiket_ve_ipucu_gorunur_etiketsiz_satir_bugunku_gibi() => OnSta(() =>
    {
        var (tagged, plain) = (Row("@" + Guid.NewGuid().ToString("N")[..12]), Row(null));
        var panel = Layout(new ChatStub { ChatView = { tagged, plain } });
        var tag = Bound(panel, tagged, nameof(ChatMessageViewModel.AccountTag));
        tag.Text.Should().Be(tagged.AccountTag);
        tag.Visibility.Should().Be(Visibility.Visible);
        tag.ToolTip.Should().Be(tagged.AccountTooltip);
        BindingOperations.GetBinding(tag, FrameworkElement.ToolTipProperty)!.Path.Path.Should().Be(nameof(ChatMessageViewModel.AccountTooltip));
        Bound(panel, plain, nameof(ChatMessageViewModel.AccountTag)).Visibility.Should().Be(Visibility.Collapsed);
        double MessageX(ChatMessageViewModel row) => Bound(panel, row, nameof(ChatMessageViewModel.Text))
            .TransformToAncestor(panel).Transform(new Point(0, 0)).X;
        MessageX(tagged).Should().Be(MessageX(plain), "etiket sağda: kodun başladığı x konumu kaymaz");
    });

    [Fact]                                                             // ortak önekli adlarda ayırt edici ek görünür kalmalı: "@" dahil 16 karakter kırpılmaz
    public void Son_karakteri_farkli_16_karakterlik_iki_etiket_kirpilmaz_uzun_etikette_ipucu_tam_adi_verir() => OnSta(() =>
    {
        var stem = "@" + Guid.NewGuid().ToString("N")[..14];                    // 15 karakter
        var (first, second, longer) = (Row(stem + "1"), Row(stem + "2"), Row("@" + Guid.NewGuid().ToString("N")[..30]));
        var panel = Layout(new ChatStub { ChatView = { first, second, longer } });
        foreach (var row in new[] { first, second })
        {
            var tag = Bound(panel, row, nameof(ChatMessageViewModel.AccountTag));
            tag.Text.Should().HaveLength(16);
            tag.ActualWidth.Should().BeGreaterThanOrEqualTo(NaturalWidth(tag) - 0.5, "ayırt edici son karakter görünmeli");
        }
        var trimmed = Bound(panel, longer, nameof(ChatMessageViewModel.AccountTag));        // Instagram adı 30 karaktere kadar
        trimmed.ActualWidth.Should().BeLessThanOrEqualTo((double)panel.FindResource("OD.Layout.ChatAccountTagMaxWidth"));
        trimmed.TextTrimming.Should().Be(TextTrimming.CharacterEllipsis);
        ((string)trimmed.ToolTip).Should().Contain(longer.AccountTag);
    });

    // ── Ek sabitlemeler: kuralların, yukarıdaki iki testin doğrulamadığı cümleleri ──

    private static double Left(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).Transform(new Point(0, 0)).X;

    private static double Right(FrameworkElement element, Visual root) => Left(element, root) + element.ActualWidth;

    /// <summary>Satırın içerik alanı: öğeyi taşıyan liste satırının içerik sunucusu (satır şablonunun kökünü o tutar).</summary>
    private static ContentPresenter RowContent(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ContentPresenter presenter) return presenter;
        throw new InvalidOperationException("öğe bir liste satırının içinde değil");
    }

    /// <summary>
    /// "Kodun x konumu kaymaz" tek başına etiketin SAĞDA durduğunu söylemez: mesajın hemen ardına yazılan etiket de
    /// x konumunu kaydırmaz. Etiketsiz satır da bugünküyle aynı olmalı: gizli etiket (boşluğuyla) yer tutarsa
    /// mesaj kolonu her satırda daralır.
    /// </summary>
    [Fact]
    public void Etiket_satirin_sag_kenarina_yaslidir_etiketsiz_satirda_mesaj_o_kenara_kadar_uzanir() => OnSta(() =>
    {
        var (tagged, plain) = (Row("@" + Guid.NewGuid().ToString("N")[..12]), Row(null));
        var panel = Layout(new ChatStub { ChatView = { tagged, plain } });
        var tag = Bound(panel, tagged, nameof(ChatMessageViewModel.AccountTag));
        var taggedMessage = Bound(panel, tagged, nameof(ChatMessageViewModel.Text));
        var plainMessage = Bound(panel, plain, nameof(ChatMessageViewModel.Text));

        tag.ActualWidth.Should().BeGreaterThan(0, "ön koşul: etiket çizildi");
        Right(tag, panel).Should().BeApproximately(Right(RowContent(tag), panel), 0.01, "etiket satırın sağ kenarındadır");
        Right(taggedMessage, panel).Should().BeLessThanOrEqualTo(Left(tag, panel), "mesaj etiketin üstüne binmez");
        Right(plainMessage, panel).Should().BeApproximately(Right(RowContent(plainMessage), panel), 0.01,
            "gizli etiket ve boşluğu yer tutmaz: etiketsiz satırda mesaj kolonu bugünkü genişliğindedir");
    });

    /// <summary>
    /// Sınır 112 DIP'lik tema belirtecidir ve onaltılık rakamlardan üretilen EN GENİŞ 16 karakterlik etiket de
    /// sığar. Rakamlar onaltılık harflerden geniştir ("a"–"f" en çok 0,58 em, rakam 0,6 em); rastgele üretilen
    /// etiket çoğunlukla daha dardır ve 108,8'in az altına inen bir sınırı her koşuda yakalamaz, yalnız rakamlı
    /// etiket her koşuda yakalar.
    /// </summary>
    [Fact]
    public void Genislik_siniri_112_DIP_belirtecidir_yalniz_rakamli_en_genis_16_karakterlik_etiket_de_kirpilmaz() => OnSta(() =>
    {
        var digits = string.Concat(Guid.NewGuid().ToByteArray().Take(15).Select(b => (char)('0' + b % 10)));
        var widest = Row("@" + digits);
        var panel = Layout(new ChatStub { ChatView = { widest } });
        var tag = Bound(panel, widest, nameof(ChatMessageViewModel.AccountTag));
        var limit = (double)panel.FindResource("OD.Layout.ChatAccountTagMaxWidth");

        limit.Should().Be(112);
        tag.MaxWidth.Should().Be(limit, "etiketin sınırı belirtecin değeridir");
        tag.Text.Should().HaveLength(16);
        NaturalWidth(tag).Should().BeLessThanOrEqualTo(limit);
        tag.ActualWidth.Should().BeGreaterThanOrEqualTo(NaturalWidth(tag) - 0.5, "en geniş 16 karakter de kırpılmadan sığar");
    });

    // ── "Kayıtlı" rozeti (docs/superpowers/specs/2026-11-23-sohbet-kayitli-rozeti-design.md) ──
    // Ad alanı = ad + (kayıtlıysa) sağ ucunda rozet. Genişliği ada göre, OD.Layout.ChatNameColumn (taban) ile
    // OD.Layout.ChatNameColumnMax (tavan) arasında: ad KESİLMEZ, yer mesajdan alınır (mesaj zaten alt satıra iner).

    private static string LongName() => Guid.NewGuid().ToString("N")[..30];             // Instagram adı 30 karaktere kadar
    private static string ShortName() => "izl" + Guid.NewGuid().ToString("N")[..5];

    /// <summary>Adı verilen, kayıt durumu seçilen bir satır (adlar üretilmiş gelir). Ham ad verilmezse görünen adla
    /// aynıdır (Instagram, TikTok); verilirse ondan ayrıdır (YouTube'da kanal kimliği, Facebook'ta sayfa kapsamlı kimlik).</summary>
    private static ChatMessageViewModel Row(string display, bool registered, string? username = null,
        string? sourceAccount = null) => new(
        new ChatMessage(Guid.NewGuid().ToString("N"), "instagram", null, username ?? display, display, null,
            $"mesaj {Guid.NewGuid():N}", 1_800_000_000, Array.Empty<string>(), sourceAccount),
        isSenderBlacklisted: false, isSenderRegistered: registered);

    /// <summary>Satırın rozet yazısı. Şablonda her satırda bulunur; kayıtsız satırda gizlidir.</summary>
    private static TextBlock Badge(ChatPanel panel, ChatMessageViewModel row) =>
        Descendants(panel).OfType<TextBlock>().Single(t => ReferenceEquals(t.DataContext, row) && t.Text == "Kayıtlı");

    /// <summary>Rozetin çipi: yazıyı saran, zemini ve ipucunu taşıyan öğe.</summary>
    private static FrameworkElement Chip(TextBlock badge) => (FrameworkElement)VisualTreeHelper.GetParent(badge);

    private static TextBlock Name(ChatPanel panel, ChatMessageViewModel row) => Bound(panel, row, nameof(ChatMessageViewModel.Display));

    private static TextBlock Message(ChatPanel panel, ChatMessageViewModel row) => Bound(panel, row, nameof(ChatMessageViewModel.Text));

    private static double MessageX(ChatPanel panel, ChatMessageViewModel row) => Left(Message(panel, row), panel);

    /// <summary>Tema ölçüsü (Themes/Metrics.xaml'deki sayısal değer).</summary>
    private static double Metric(string key) => (double)Application.Current.FindResource(key);

    /// <summary>Mesaja ayrılan taban: ad alanı ne kadar büyürse büyüsün mesaja kalan en az genişlik.</summary>
    private static double MessageFloor() => ((Thickness)Application.Current.FindResource("OD.Pad.ChatMessageReserve")).Right;

    private const double WindowFrame = 16;       // standart Windows penceresinin yatay çerçeve payı
    private const double ScrollBarWidth = 10;    // Themes/Base.xaml, ScrollBar stili; yayında dikey çubuk hep vardır

    /// <summary>
    /// En dar sohbet panelinin genişliği, dikey kaydırma çubuğu düşülmüş hâliyle. Kabuk yerleşimi (MainShellView):
    /// pencere tabanı − pencere çerçevesi − ikon kipindeki sol çubuk − içerik boşluğu (2 × OD.Space.4) − sabit sağ
    /// panel − onun boşluğu (2 × OD.Space.4). 1920'lik ekranda yarım pencere budur ve operatörün olağan çalışma
    /// biçimidir (bkz. Metrics.xaml, OD.Layout.AppMinWidth notu); "dar pencere" bir uç durum değildir.
    /// </summary>
    private static double NarrowestPanelWidth() =>
        Metric("OD.Layout.AppMinWidth") - WindowFrame - Metric("OD.Layout.SideWidthMin") - 2 * Metric("OD.Space.4")
        - Metric("OD.Layout.RightWidth") - 2 * Metric("OD.Space.4") - ScrollBarWidth;

    /// <summary>Satırdaki adın yazı tipiyle doğal genişliği <paramref name="width"/>'i aşmayan, üretilmiş en uzun ad.
    /// Sınır testleri yazı tipinin ölçülerine sayı gömmeden böyle kurulur.</summary>
    private static string NameWithin(double width)
    {
        var sample = Row(ShortName(), registered: false);
        var font = Name(Layout(new ChatStub { ChatView = { sample } }), sample);
        var source = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var length = source.Length;
        while (length > 1 && NaturalWidth(source[..length], font) > width) length--;
        return source[..length];
    }

    [Fact]                                                             // XAML bağ yolu gerçek ViewModel ile çizilerek sınanır (yanlış yol hata vermez, rozet hiç görünmez)
    public void Kayitli_satirda_rozet_gorunur_kayitsiz_satirda_gizlidir() => OnSta(() =>
    {
        var (registered, plain) = (Row(ShortName(), registered: true), Row(ShortName(), registered: false));
        var panel = Layout(new ChatStub { ChatView = { registered, plain } });

        var badge = Badge(panel, registered);
        Chip(badge).Visibility.Should().Be(Visibility.Visible);
        badge.ActualWidth.Should().BeGreaterThan(0, "rozet çizildi");
        badge.Foreground.Should().BeSameAs(panel.FindResource("OD.Brush.Success"));
        Chip(badge).ToolTip.Should().Be("Telefonu kayıtlı müşteri");
        Chip(Badge(panel, plain)).Visibility.Should().Be(Visibility.Collapsed);
    });

    /// <summary>
    /// Rozet adın yerinden yemez: kayıtlı satırda ada kalan yer, rozetten önceki ad sütununun genişliğinden
    /// (OD.Layout.ChatUserMaxWidth) az değildir. Rozet büyürse (yazısı, dolgusu) bu test düşer; ad alanının tabanı
    /// (OD.Layout.ChatNameColumn) onunla birlikte büyütülmelidir.
    /// </summary>
    [Fact]
    public void Rozet_adin_yerinden_yemez() => OnSta(() =>
    {
        var row = Row(ShortName(), registered: true);
        var panel = Layout(new ChatStub { ChatView = { row } });
        var (name, chip) = (Name(panel, row), Chip(Badge(panel, row)));

        chip.ActualWidth.Should().BeGreaterThan(0, "ön koşul: rozet çizildi");
        (Left(chip, panel) - Left(name, panel) - name.Margin.Right).Should()
            .BeGreaterThanOrEqualTo(Metric("OD.Layout.ChatUserMaxWidth"), "adın başladığı yerden rozete kadar olan yer, adın boşluğu düşülünce");
        Right(name, panel).Should().BeLessThanOrEqualTo(Left(chip, panel), "ad rozetin altına girmez");
        Right(chip, panel).Should().BeLessThanOrEqualTo(MessageX(panel, row), "rozet mesajın üstüne taşmaz");
    });

    /// <summary>
    /// Ada sığan satırlarda mesaj aynı hizada başlar: operatörün gözüyle taradığı kod sütunu kayıtlı ve kayıtsız
    /// satırda, eski ad sütununu tam dolduran adda bile oynamaz.
    /// </summary>
    [Fact]
    public void Ada_sigan_satirlarda_mesaj_ayni_hizada_baslar() => OnSta(() =>
    {
        var limit = Metric("OD.Layout.ChatUserMaxWidth");
        var (shortRegistered, plain, fullRegistered) =
            (Row(ShortName(), registered: true), Row(ShortName(), registered: false), Row(NameWithin(limit), registered: true));
        var panel = Layout(new ChatStub { ChatView = { shortRegistered, plain, fullRegistered } });
        var full = Name(panel, fullRegistered);

        NaturalWidth(full).Should().BeGreaterThan(limit - 12, "ön koşul: ad eski sütunu doldurur");
        full.ActualWidth.Should().BeGreaterThanOrEqualTo(NaturalWidth(full) - 0.5, "adın tamamı çizilir");
        full.ActualHeight.Should().Be(Name(panel, plain).ActualHeight, "ad tek satırdadır");
        MessageX(panel, shortRegistered).Should().Be(MessageX(panel, plain));
        MessageX(panel, fullRegistered).Should().Be(MessageX(panel, plain), "eski sütuna sığan ad, rozetle birlikte de mesajı itmez");
        (MessageX(panel, plain) - Left(Name(panel, plain), panel)).Should()
            .BeApproximately(Metric("OD.Layout.ChatNameColumn"), 0.01, "mesaj tam ad alanının bittiği yerde başlar");
    });

    /// <summary>
    /// Mesaja ayrılan taban iki boşlukla kurulur: ad alanının sağındaki ayırma ve mesajın o ayrılan yerin üstüne
    /// oturması için aynı büyüklükte negatif sol boşluk. İkisi ayrışırsa mesaj ya adın üstüne biner ya da aradaki
    /// boşluk kadar sağdan başlar.
    /// </summary>
    [Fact]
    public void Mesaja_ayrilan_taban_ile_geri_alma_boslugu_ayni_buyukluktedir() => OnSta(() =>
    {
        var reserve = (Thickness)Application.Current.FindResource("OD.Pad.ChatMessageReserve");
        var reclaim = (Thickness)Application.Current.FindResource("OD.Pad.ChatMessageReclaim");

        reserve.Should().Be(new Thickness(0, 0, MessageFloor(), 0));
        reclaim.Should().Be(new Thickness(-MessageFloor(), 0, 0, 0));
        MessageFloor().Should().BeGreaterThan(0);
    });

    /// <summary>
    /// Uzun ad KESİLMEZ: tek satırda, tamamı görünür. Yer mesajdan alınır: o satırın mesajı daha sağdan başlar
    /// (sığmazsa zaten alt satıra iner). Rozet adın hemen ardındadır. Ham ad eskisi gibi ipucunda durur.
    /// </summary>
    [Fact]
    public void Uzun_ad_kesilmez_yeri_mesajdan_alinir() => OnSta(() =>
    {
        // Eski sütundan belirgin uzun, ama rozetiyle birlikte tavanın altında: genişliği ölçülerek kurulur
        // (rastgele 30 karakter kimi koşuda tavanı aşıp alt satıra inerdi).
        var longName = NameWithin(Metric("OD.Layout.ChatUserMaxWidth") + 64);
        var (longRow, shortRow) =
            (Row(longName, registered: true, username: $"UC{Guid.NewGuid():N}"), Row(ShortName(), registered: true));
        var panel = Layout(new ChatStub { ChatView = { longRow, shortRow } });
        var (name, chip, message) = (Name(panel, longRow), Chip(Badge(panel, longRow)), Message(panel, longRow));

        NaturalWidth(name).Should().BeGreaterThan(Metric("OD.Layout.ChatUserMaxWidth"), "ön koşul: ad eski sütuna sığmaz");
        name.ActualWidth.Should().BeGreaterThanOrEqualTo(NaturalWidth(name) - 0.5, "adın tamamı çizilir");
        name.ActualHeight.Should().Be(Name(panel, shortRow).ActualHeight, "ad tek satırdadır");
        name.TextTrimming.Should().Be(TextTrimming.None);
        chip.ActualWidth.Should().BeGreaterThan(0, "rozet çizildi");
        Right(name, panel).Should().BeLessThanOrEqualTo(Left(chip, panel), "ad rozetin altına girmez");
        Right(chip, panel).Should().BeLessThanOrEqualTo(Left(message, panel), "rozet mesajın üstüne taşmaz");
        Left(message, panel).Should().BeGreaterThan(MessageX(panel, shortRow), "yer mesajdan alınır: bu satırın mesajı daha sağdan başlar");
        message.ActualWidth.Should().BeGreaterThan(0, "mesaj görünür kalır");
        Right(message, panel).Should().BeLessThanOrEqualTo(Right(RowContent(message), panel) + 0.01, "mesaj satırın dışına taşmaz");
        name.ToolTip.Should().Be(longRow.Username);
    });

    /// <summary>
    /// Ad alanının tavanı (OD.Layout.ChatNameColumnMax): tavana sığmayan ad alt satıra iner (yine kesilmez), mesaja
    /// yer kalır. Tavan olmasaydı çok uzun bir ad satırı tek başına doldurur, mesaj görünmez olurdu.
    /// </summary>
    [Fact]
    public void Tavana_sigmayan_ad_alt_satira_iner_mesaja_yer_kalir() => OnSta(() =>
    {
        var huge = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");            // 64 karakter
        var (hugeRow, shortRow) = (Row(huge, registered: true), Row(ShortName(), registered: true));
        var panel = Layout(new ChatStub { ChatView = { hugeRow, shortRow } });
        var (name, chip, message) = (Name(panel, hugeRow), Chip(Badge(panel, hugeRow)), Message(panel, hugeRow));
        var max = Metric("OD.Layout.ChatNameColumnMax");

        NaturalWidth(name).Should().BeGreaterThan(max, "ön koşul: ad tavana sığmaz");
        name.ActualHeight.Should().BeGreaterThan(Name(panel, shortRow).ActualHeight * 1.5, "ad alt satıra iner");
        name.TextTrimming.Should().Be(TextTrimming.None, "alt satıra inen ad kırpılmaz");
        (Left(message, panel) - Left(name, panel)).Should().BeLessThanOrEqualTo(max + 0.01, "ad alanı tavanı aşmaz");
        chip.ActualWidth.Should().BeGreaterThan(0, "rozet çizildi");
        Right(chip, panel).Should().BeLessThanOrEqualTo(Left(message, panel), "rozet mesajın üstüne taşmaz");
        message.ActualWidth.Should().BeGreaterThan(100, "mesaja yer kalır");
    });

    /// <summary>
    /// En dar pencerede de çok uzun adlı satırın mesajı görünür kalır: ad alanı, mesaja ayrılan tabana dokunamaz.
    /// </summary>
    [Fact]
    public void En_dar_pencerede_de_cok_uzun_adli_satirin_mesaji_gorunur() => OnSta(() =>
    {
        var row = Row(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), registered: true);
        var panel = Layout(new ChatStub { ChatView = { row } }, NarrowestPanelWidth());
        var message = Message(panel, row);

        message.ActualWidth.Should().BeGreaterThanOrEqualTo(MessageFloor() - 0.01, "mesaja ayrılan taban korunur");
        Right(message, panel).Should().BeLessThanOrEqualTo(Right(RowContent(message), panel) + 0.01, "mesaj satırın dışına taşmaz");
    });

    /// <summary>
    /// En dar pencere + Instagram hesap etiketi + uzun ad: üçü birlikte satıra sığmaz. Yer açan addır: alt satıra
    /// iner (kesilmez); mesaj tabanını, etiket tam genişliğini, rozet yerini korur. Ad alanı sabit bir tavana kadar
    /// büyüseydi etiket kalan yerin tamamını alır, mesaj görünmez olurdu (ölçüldü: "B7" yerine bir çizgi).
    /// </summary>
    [Fact]
    public void En_dar_pencerede_hesap_etiketli_uzun_adli_satirda_da_mesaj_gorunur() => OnSta(() =>
    {
        var widestTag = "@" + string.Concat(Guid.NewGuid().ToByteArray().Take(15).Select(b => (char)('0' + b % 10)));
        var (row, shortRow) = (Row(LongName(), registered: true, sourceAccount: widestTag), Row(ShortName(), registered: true));
        var panel = Layout(new ChatStub { ChatView = { row, shortRow } }, NarrowestPanelWidth());
        var (name, chip, message) = (Name(panel, row), Chip(Badge(panel, row)), Message(panel, row));
        var tag = Bound(panel, row, nameof(ChatMessageViewModel.AccountTag));

        NaturalWidth(name).Should().BeGreaterThan(Metric("OD.Layout.ChatUserMaxWidth"), "ön koşul: ad eski sütuna sığmaz");
        message.ActualWidth.Should().BeGreaterThanOrEqualTo(MessageFloor() - 0.01, "mesaja ayrılan taban korunur");
        Right(message, panel).Should().BeLessThanOrEqualTo(Left(tag, panel) + 0.01, "mesaj etiketin üstüne binmez");
        tag.ActualWidth.Should().BeGreaterThanOrEqualTo(NaturalWidth(tag) - 0.5, "16 karakterlik etiket kırpılmaz");
        Right(tag, panel).Should().BeApproximately(Right(RowContent(tag), panel), 0.01, "etiket satırın sağ kenarındadır");
        name.ActualHeight.Should().BeGreaterThan(Name(panel, shortRow).ActualHeight * 1.5, "yer açan addır: alt satıra iner");
        name.TextTrimming.Should().Be(TextTrimming.None, "alt satıra inen ad kırpılmaz");
        Right(name, panel).Should().BeLessThanOrEqualTo(Left(chip, panel), "ad rozetin altına girmez");
        Right(chip, panel).Should().BeLessThanOrEqualTo(Left(message, panel) + 0.01, "rozet mesajın üstüne taşmaz");
        (Left(message, panel) - Left(name, panel)).Should()
            .BeGreaterThanOrEqualTo(Metric("OD.Layout.ChatNameColumn") - 0.01, "ad alanı tabanının altına sıkışmaz, rozet kırpılmaz");
    });

    /// <summary>
    /// Kayıtsız satırda gizli rozet yer tutmaz: ad, rozetin duracağı yeri de kullanır ve eski sütundan uzun olduğu
    /// hâlde mesajı itmez.
    /// </summary>
    [Fact]
    public void Kayitsiz_satirda_ad_rozetin_yerini_de_kullanir() => OnSta(() =>
    {
        var limit = Metric("OD.Layout.ChatUserMaxWidth");
        var (wide, shortRow) = (Row(NameWithin(limit + 24), registered: false), Row(ShortName(), registered: false));
        var panel = Layout(new ChatStub { ChatView = { wide, shortRow } });
        var name = Name(panel, wide);

        NaturalWidth(name).Should().BeGreaterThan(limit, "ön koşul: ad eski sütundan uzun");
        Chip(Badge(panel, wide)).Visibility.Should().Be(Visibility.Collapsed);
        name.ActualWidth.Should().BeGreaterThanOrEqualTo(NaturalWidth(name) - 0.5, "adın tamamı çizilir");
        MessageX(panel, wide).Should().Be(MessageX(panel, shortRow), "ad rozetin yerini kullanır, mesaj itilmez");
    });

    /// <summary>
    /// Yayın sırasında formu dolduran kişinin EKRANDAKİ satırı döner: işaret sonradan açılınca rozet ad alanının
    /// içinde belirir, mesaj yerinden oynamaz. Bağ tek seferlik olsaydı satır çizildiği andaki hâlinde kalırdı.
    /// </summary>
    [Fact]
    public void Ekrandaki_satir_sonradan_kayitli_olunca_rozet_belirir() => OnSta(() =>
    {
        var row = Row(ShortName(), registered: false);
        var panel = Layout(new ChatStub { ChatView = { row } });
        var (name, chip) = (Name(panel, row), Chip(Badge(panel, row)));
        chip.Visibility.Should().Be(Visibility.Collapsed, "ön koşul: satır kayıtsız çizildi");
        var messageX = MessageX(panel, row);

        row.IsSenderRegistered = true;
        ThemeTestHost.Pump();
        panel.UpdateLayout();

        chip.Visibility.Should().Be(Visibility.Visible);
        chip.ActualWidth.Should().BeGreaterThan(0);
        Right(name, panel).Should().BeLessThanOrEqualTo(Left(chip, panel), "ad rozetin altına girmez");
        Right(chip, panel).Should().BeLessThanOrEqualTo(MessageX(panel, row), "rozet ad alanının içinde belirir");
        MessageX(panel, row).Should().Be(messageX, "satır dönerken mesaj yerinden oynamaz");
    });

    /// <summary>
    /// Uzun adlı satır sonradan kayıtlı olunca da ad kesilmez: rozet belirir, yer mesajdan alınır (o satırın mesajı
    /// en çok rozet kadar sağa kayar).
    /// </summary>
    [Fact]
    public void Uzun_adli_satir_sonradan_kayitli_olunca_ad_yine_kesilmez() => OnSta(() =>
    {
        // Kayıtsızken tabana sığan (rozetin yerini de kullanan), rozetle birlikte sığmayan ad.
        var row = Row(NameWithin(Metric("OD.Layout.ChatUserMaxWidth") + 32), registered: false);
        var panel = Layout(new ChatStub { ChatView = { row } });
        var (name, chip, message) = (Name(panel, row), Chip(Badge(panel, row)), Message(panel, row));
        var (messageX, lineHeight) = (Left(message, panel), name.ActualHeight);

        row.IsSenderRegistered = true;
        ThemeTestHost.Pump();
        panel.UpdateLayout();

        chip.Visibility.Should().Be(Visibility.Visible);
        name.ActualWidth.Should().BeGreaterThanOrEqualTo(NaturalWidth(name) - 0.5, "adın tamamı çizilir");
        name.ActualHeight.Should().Be(lineHeight, "ad tek satırda kalır");
        Right(name, panel).Should().BeLessThanOrEqualTo(Left(chip, panel), "ad rozetin altına girmez");
        Right(chip, panel).Should().BeLessThanOrEqualTo(Left(message, panel), "rozet mesajın üstüne taşmaz");
        Left(message, panel).Should().BeGreaterThan(messageX, "yer mesajdan alınır");
        (Left(message, panel) - messageX).Should().BeLessThanOrEqualTo(chip.ActualWidth + chip.Margin.Right + 0.01,
            "mesaj en çok rozet kadar kayar");
    });

    /// <summary>Rozet satırı yükseltmez: kayıtlı ve kayıtsız satır aynı yükseklikte, liste satırdan satıra oynamaz.</summary>
    [Fact]
    public void Rozet_satiri_yukseltmez() => OnSta(() =>
    {
        var (registered, plain) = (Row(ShortName(), registered: true), Row(ShortName(), registered: false));
        var panel = Layout(new ChatStub { ChatView = { registered, plain } });
        var badge = Badge(panel, registered);
        Chip(badge).ActualHeight.Should().BeGreaterThan(0, "ön koşul: rozet çizildi");
        RowContent(badge).ActualHeight.Should().Be(RowContent(Badge(panel, plain)).ActualHeight);
    });

    // Instagram sağlık şeridi, bütün sohbet kaynaklarının durum şeridine dönüştü (tasarım 2026-11-24 §5.4);
    // görünüm testleri ChatPanelStatusStripTests'te.
}
