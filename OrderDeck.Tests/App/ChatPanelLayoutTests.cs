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

    /// <summary>Üretilmiş izleyici ve metinle bir Instagram satırı.</summary>
    private static ChatMessageViewModel Row(string? sourceAccount) => new(
        new ChatMessage(Guid.NewGuid().ToString("N"), "instagram", null, $"izleyici-{Guid.NewGuid():N}", null, null,
            $"mesaj {Guid.NewGuid():N}", 1_800_000_000, Array.Empty<string>(), sourceAccount), isSenderBlacklisted: false);

    /// <summary>Paneli 700x400'e yerleştirir: ölçüm, bekleyen bağlamalar, yeniden ölçüm
    /// (<see cref="ShellBannersLayoutTests"/> ile aynı kalıp).</summary>
    private static ChatPanel Layout(ChatStub context)
    {
        var size = new Size(700, 400);
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
    private static double NaturalWidth(TextBlock t)
    {
        var probe = new TextBlock
        {
            Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize,
            FontStyle = t.FontStyle, FontWeight = t.FontWeight, FontStretch = t.FontStretch,
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private static TextBlock Bound(ChatPanel panel, ChatMessageViewModel row, string path) =>      // satırın, Text bağı o yola giden tek TextBlock'u
        Descendants(panel).OfType<TextBlock>().Single(t => ReferenceEquals(t.DataContext, row)
            && BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == path);

    [Fact]                                                             // Review Focus 3
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

    [Fact]                                                             // Review Focus 5
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
    /// sığar. Rakamlar harflerden geniştir ("a"–"f" en çok 0,58 em, rakam 0,6 em); rastgele üretilen etiket
    /// çoğunlukla daha dardır ve küçülen bir sınırı ancak ara sıra yakalar, yalnız rakamlı etiket her koşuda yakalar.
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
}
