using OrderDeck.App.Services.Instagram;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Sohbet panelindeki Instagram sağlık şeridinin bir satırı: bağlı bir Sayfanın Instagram hesabının o anki
/// durumu, gösterime hazır. <see cref="InstagramHealthPresenter.Evaluate"/> üretir. Kayıt değişmezdir ve değer
/// eşitliği taşır: iki değerlendirmenin satırları karşılaştırılabilir.
/// </summary>
/// <param name="PageId">Satırın ait olduğu bağlı Facebook Sayfasının kimliği.</param>
/// <param name="Label">Hesabın etiketi: "@" + kullanıcı adı; kullanıcı adı bilinmiyorsa (ve Sayfa adıyla yazılan iki
/// satırda) Sayfa adı.</param>
/// <param name="StateText">Etiketsiz durum metni, ör. <c>yanıt yok (18 sn)</c>.</param>
/// <param name="Text">Şeritte görünen tam metin: etiket + durum metni.</param>
/// <param name="Severity">Satırın noktasının rengi.</param>
/// <param name="Tooltip">Satırın ipucu; null ise ipucu yok.</param>
public sealed record InstagramAccountStatusViewModel(
    string PageId,
    string Label,
    string StateText,
    string Text,
    InstagramStatusSeverity Severity,
    string? Tooltip);
