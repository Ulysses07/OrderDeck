namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Ödeme işi SQL'inin birden çok depoda paylaşılan parçaları. Aynı kural iki yerde ayrı yazılsaydı
/// biri değişip öbürü unutulurdu — kopya taşıması ile kişi birleştirmesi aynı şeyi yapıyor: aynı kişi
/// ve kapsamda ikinci bir iş.
/// </summary>
internal static class PaymentJobSql
{
    /// <summary>İş para taşıyor mu (WHERE/CASE koşulu, satırın kendi kolonları üzerinde). C# karşılığı
    /// <see cref="PaymentJob.CarriesMoney"/> — ikisi aynı kalmalı.
    ///
    /// <para>Belirsiz (<c>apply_uncertain</c>), geri alma bekleyen (<c>reverse_pending</c>) ve tutarı
    /// sıfırdan büyük uygulanmış iş. Tutarı boş 'applied' satırı (sağlıklı akışta oluşmaz) temkinle
    /// para taşıyor sayılır: uzlaştırma onu yeniden oynatıp gerçeği öğrenir.</para></summary>
    public const string CarriesMoney =
        "(State IN ('apply_uncertain', 'reverse_pending') " +
        "OR (State = 'applied' AND (AppliedAmount IS NULL OR CAST(AppliedAmount AS REAL) > 0)))";

    /// <summary>
    /// Bir işi miras kapsamına indirir — çağıran <c>WHERE</c> ekler; parametre <c>@now</c>.
    /// <list type="bullet">
    /// <item><c>ScopeKey = 'legacy:{ApplyKey ya da Id}:{Id}'</c>: iş Id'si adı kendiliğinden benzersiz
    ///   kılar (UX_PaymentJob_Scope çakışmaz). Adı hiçbir yol ayrıştırmaz (miras taraması
    ///   <c>LIKE 'legacy:%'</c>, yeniden oynatma <c>ApplyKey</c> kolonunu okur).</item>
    /// <item><c>PriorScopeKey</c> (göç 047): indirilen işin ASIL kapsamı — banka beklentisi (W2)
    ///   kaybeden işin beklentisini bununla iptal eder. İlk indirme korunur.</item>
    /// <item>Para taşıyan iş AÇIK bırakılır (<c>ClosedAt = NULL</c>, kapalı olsa bile): miras
    ///   uzlaştırması (<c>PaymentRequestService</c>) onu kendi müşteri Id'siyle kesinleştirir ve
    ///   fazla düşümü geri alır. Eskiden taşıma anahtarlı ama KAPALI işi kapalı bırakıyordu; miras
    ///   taraması yalnız açık işlere baktığı için o düşüm hiç geri alınmıyordu.</item>
    /// <item>Gerisi (parasız) kapanır: geri alınacak bir şey yok.</item>
    /// </list>
    /// </summary>
    public const string DemoteToLegacy =
        "UPDATE PaymentJob SET " +
        "PriorScopeKey = COALESCE(PriorScopeKey, ScopeKey), " +
        "ScopeKey = 'legacy:' || COALESCE(ApplyKey, Id) || ':' || Id, " +
        "ClosedAt = CASE WHEN " + CarriesMoney + " THEN NULL ELSE COALESCE(ClosedAt, @now) END, " +
        "UpdatedAt = @now ";
}
