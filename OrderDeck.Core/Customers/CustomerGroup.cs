namespace OrderDeck.Core.Customers;

/// <summary>
/// Kişi (grup) eşitliğinin TEK tanımı. Arama kartı (<c>CustomerSearchViewModel.BuildCards</c>),
/// grup tamamlama (<c>CustomerRepository.CompleteGroups</c>), ödeme kişisi
/// (<c>CustomerRepository.GetPaymentPerson</c> ve <c>PaymentJobRepository</c>'nin işlem içi yeniden
/// okuması) ve yayın raporunun kişi toplamı aynı kuralı kullanır. Ayrı ayrı normalize edilseydi kart
/// ile ödeme işi farklı kişileri görürdü: kart 300'lük kişiyi gösterirken iş 100'lük satırda açılırdı.
///
/// <para><b>Kural:</b> <c>GroupId</c> boş ya da yalnız boşluksa satır gruplanmamıştır (kendi kişisidir).
/// Değilse aynı kişi = HAM <c>GroupId</c> değeri birebir aynı olan satırlar — kırpma yok; " g" ile "g"
/// ayrı gruptur (SQL tarafındaki <c>GroupId IN …</c> karşılaştırmasıyla aynı).</para>
/// </summary>
public static class CustomerGroup
{
    /// <summary>Satır bir gruba bağlı mı.</summary>
    public static bool IsGrouped(string? groupId) => !string.IsNullOrWhiteSpace(groupId);

    /// <summary>Satırın kişi anahtarı: gruplu satırda grup, değilse satırın kendisi. Aynı anahtarı
    /// taşıyan satırlar aynı kişidir.</summary>
    public static string PersonKey(string id, string? groupId)
        => IsGrouped(groupId) ? "grp:" + groupId : "id:" + id;
}
