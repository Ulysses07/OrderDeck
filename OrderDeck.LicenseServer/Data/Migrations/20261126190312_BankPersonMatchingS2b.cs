using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// Banka havalesi onayı S2b (spec 2026-10-10 §2.3, §2.5; S2b planı Görev 11): kişi düzeyi okuyucuyla aynı PR'da iki veri
    /// deyimi, bu sırayla ve ayrı komutlarla: <see cref="MoveCopyBonds"/>, <see cref="RederivePaymentCustomers"/>.
    ///
    /// <para><b>Yalnız veri yazar.</b> Şema değişmez (model kaymaz): S2a imajı aynı şemada ve bu veriyle çalışır. Göç geri
    /// alınmaz (<c>Down</c> boş); deploy'un otomatik geri alması imajı döndürür, veriyi döndürmez. Bir kez koşar.</para>
    ///
    /// <para><b>Taşınabilir kopya tek kuraldır</b> (S2b planı Karar 7) ve okuyucununkiyle aynıdır (<c>PaymentMatcher</c>'ın IBAN
    /// kümesi, zinciri <c>CustomerIdResolver.ChainOfAsync</c> ile izler): kopya geçici (<c>CreatedByShopper</c>) değil ve
    /// silinmemiş; zincir en çok 3 adımda aynı lisansta silinmemiş bir asıl kayda varıyor; ara adımlar da geçici ya da silinmiş
    /// değil. Geçici kopyadan taşınmaz: SQL telefon kanıtını sınayamaz, bağ kanıtsız kişinin havalesinden öğrenilmiş olabilir.
    /// Kalan kopya satırları bugünkü hâlindedir (eşleştirici görmez, 12 aylık saklaması işler).</para>
    ///
    /// <para><b>Damga, durum ve jeton yazılmaz</b> (Karar 8): <c>PaymentMatches.UpdatedAt</c> ilerleseydi tarama 2. aşaması
    /// (<c>m.UpdatedAt &lt; b.CreatedAt</c>) bağdan önce hesaplanmış satırı seçmezdi; <c>Payments.UpdatedAt</c>
    /// <c>payments/since</c> imlecidir, <c>Payments.Status</c> jetondur. Kopyada kalmış bağlantıyla yazılmış <c>Contradicted</c>
    /// düzeltilmez: ölçümü geçmişe dönük ve sürümle ayrılamaz biçimde değiştirirdi.</para>
    ///
    /// <para><b>Çift doğamaz.</b> Dar tekil <c>(LicenseId, IbanHash)</c> S3b'ye kadar durur ve taşıma o iki sütunu değiştirmez: özet
    /// başına tek satır taşınır, geniş tekil <c>(LicenseId, IbanHash, WpfCustomerId)</c> de çakışmaz.</para>
    ///
    /// <para><b>Türetme S2a sabitinden iki noktada ayrılır</b> (<see cref="RederivePaymentCustomers"/>): zincirin izlediği bir kopya
    /// birleştirilmiş geçici kopyaysa müşteri NULL'dır ve yalnız <c>Source = Receipt</c> satır yazılır.</para>
    /// </summary>
    public partial class BankPersonMatchingS2b : Migration
    {
        /// <summary>Kopyada kalmış IBAN bağını ve aynı kopyayı gösteren eşleşme satırlarını (<c>ProposedWpfCustomerId</c>,
        /// <c>ActualWpfCustomerId</c>) AYNI eşlemeyle asıl kayda taşır (tasarım §2.5; S2a planı Karar 5): bağ ve onu öğreten karar
        /// birlikte, yoksa IBAN talebiyle silme, K-6 kalan doğrulama, dekont onayı ve shopper silmesi yarım taşınmış çifti bulamazdı.
        /// Eşleme (<c>@CopyMap</c>) yalnız taşınabilir kopyalardan kurulur (sınıf özeti); 3 adımda bitmeyen ya da kırık (başka
        /// lisansa yönelen, projeksiyonu olmayan) zincir eşlemeye girmez. Yeniden koşulunca hiçbir satırı değiştirmez: taşınan satır
        /// artık asıl kaydı gösterir.</summary>
        public const string MoveCopyBonds = """
            DECLARE @CopyMap TABLE (CopyId uniqueidentifier NOT NULL PRIMARY KEY, LicenseId uniqueidentifier NOT NULL,
                CanonicalId uniqueidentifier NOT NULL);
            INSERT INTO @CopyMap (CopyId, LicenseId, CanonicalId)
            SELECT k.[Id], k.[LicenseId], c.[Id]
            FROM [WpfCustomerProjections] k
            JOIN [WpfCustomerProjections] a1 ON a1.[Id] = k.[MergedIntoId] AND a1.[LicenseId] = k.[LicenseId]
            LEFT JOIN [WpfCustomerProjections] a2 ON a2.[Id] = a1.[MergedIntoId] AND a2.[LicenseId] = k.[LicenseId]
            LEFT JOIN [WpfCustomerProjections] a3 ON a3.[Id] = a2.[MergedIntoId] AND a3.[LicenseId] = k.[LicenseId]
            CROSS APPLY (SELECT CASE WHEN a1.[MergedIntoId] IS NULL THEN a1.[Id] WHEN a2.[MergedIntoId] IS NULL THEN a2.[Id]
                                     WHEN a3.[MergedIntoId] IS NULL THEN a3.[Id] END AS [Id]) x -- 3 adımda bitmeyen ya da kırık zincir: NULL
            JOIN [WpfCustomerProjections] c ON c.[Id] = x.[Id] AND c.[LicenseId] = k.[LicenseId]
            WHERE k.[MergedIntoId] IS NOT NULL AND k.[PurgedAt] IS NULL AND k.[CreatedByShopper] = 0 AND c.[PurgedAt] IS NULL
              AND NOT (a1.[MergedIntoId] IS NOT NULL AND (a1.[CreatedByShopper] = 1 OR a1.[PurgedAt] IS NOT NULL))
              AND NOT (a2.[MergedIntoId] IS NOT NULL AND (a2.[CreatedByShopper] = 1 OR a2.[PurgedAt] IS NOT NULL));
            UPDATE m SET m.[WpfCustomerId] = cm.[CanonicalId]
            FROM [CustomerIbanMemories] m JOIN @CopyMap cm ON cm.[CopyId] = m.[WpfCustomerId] AND cm.[LicenseId] = m.[LicenseId];
            UPDATE pm SET pm.[ProposedWpfCustomerId] = COALESCE(cp.[CanonicalId], pm.[ProposedWpfCustomerId]),
                          pm.[ActualWpfCustomerId] = COALESCE(ca.[CanonicalId], pm.[ActualWpfCustomerId])
            FROM [PaymentMatches] pm
            LEFT JOIN @CopyMap cp ON cp.[CopyId] = pm.[ProposedWpfCustomerId] AND cp.[LicenseId] = pm.[LicenseId]
            LEFT JOIN @CopyMap ca ON ca.[CopyId] = pm.[ActualWpfCustomerId] AND ca.[LicenseId] = pm.[LicenseId]
            WHERE cp.[CopyId] IS NOT NULL OR ca.[CopyId] IS NOT NULL;
            """;

        /// <summary>Dekontun müşterisini bağdan YENİDEN türetir (tasarım §2.3): S2a'nın
        /// <see cref="BankDecisionsS2a.BackfillPaymentCustomers"/>'ının kopyası. Bağ seçimi aynı (<c>LeftAt</c>'e bakılmaz, en yeni
        /// <c>JoinedAt</c>'li müşterili bağ, eşitlikte <c>Id</c>), asıl kayda en çok 3 adım, yalnız aynı lisans; değişmeyen satıra ve
        /// shopper'sız dekonta dokunmaz. Doluyu da yazar: S2a penceresinde gönderilen dekont dolar, geri alma penceresinin bayat dolu
        /// bağı kapanır. İki noktada ayrılır: zincirin izlediği bir kopya birleştirilmiş geçici kopyaysa (<c>CreatedByShopper</c>)
        /// müşteri NULL'dır, telefon kanıtlasa da (C# eşi <c>ShopperCustomerResolver.PaymentCustomerAsync</c> kanıtlı geçici kopyada
        /// asıl kaydı verir; SQL <c>PhoneNormalizer</c>'ı koşamaz, S2b planı Karar 9); ve yalnız <c>Source = Receipt</c> satır yazılır
        /// (banka satırının müşterisi kararından gelir, bağdan türetilmez). Tek UPDATE; yeniden koşulunca 0 satır.</summary>
        public const string RederivePaymentCustomers = """
            UPDATE p SET p.[WpfCustomerId] = d.[Derived]
            FROM [Payments] p
            OUTER APPLY (SELECT TOP (1) x.[WpfCustomerId] FROM [ShopperBroadcasterLinks] x
                         WHERE x.[ShopperId] = p.[ShopperId] AND x.[LicenseId] = p.[LicenseId] AND x.[WpfCustomerId] IS NOT NULL
                         ORDER BY x.[JoinedAt] DESC, x.[Id] DESC) l
            LEFT JOIN [WpfCustomerProjections] h1 ON h1.[Id] = l.[WpfCustomerId] AND h1.[LicenseId] = p.[LicenseId]
            LEFT JOIN [WpfCustomerProjections] h2 ON h2.[Id] = h1.[MergedIntoId] AND h2.[LicenseId] = p.[LicenseId]
            LEFT JOIN [WpfCustomerProjections] h3 ON h3.[Id] = h2.[MergedIntoId] AND h3.[LicenseId] = p.[LicenseId]
            CROSS APPLY (SELECT CASE WHEN (h1.[MergedIntoId] IS NOT NULL AND h1.[CreatedByShopper] = 1)
                                       OR (h2.[MergedIntoId] IS NOT NULL AND h2.[CreatedByShopper] = 1)
                                       OR (h3.[MergedIntoId] IS NOT NULL AND h3.[CreatedByShopper] = 1) THEN NULL
                                     ELSE COALESCE(h3.[MergedIntoId], h2.[MergedIntoId], h1.[MergedIntoId], l.[WpfCustomerId]) END AS [Derived]) d
            WHERE p.[ShopperId] IS NOT NULL AND p.[Source] = N'Receipt'
              AND NOT EXISTS (SELECT p.[WpfCustomerId] INTERSECT SELECT d.[Derived]);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Yalnız DML, ayrı komutlar (sınıf özeti): önce bağ ve onu gösteren kararlar, sonra dekontun müşterisi.
            migrationBuilder.Sql(MoveCopyBonds);
            migrationBuilder.Sql(RederivePaymentCustomers);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Göç geri alınmaz (sınıf özeti).
        }
    }
}
