using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Bu veritabanının kopya kimliği (spec 2026-10-13 §2, §3.2, §8.7): <c>_meta.ReplicaId</c> ve geri yüklemeden
/// sonra sunucuya bildirilmeyi bekleyen <c>_meta.PreviousReplicaId</c>.
///
/// <para><b>Neden veritabanında:</b> sahiplik canlı kuyruğu tutan veritabanı DOSYASINI izlemeli;
/// <c>AppSettings.InstallationId</c> ayar dosyasına bağlı. Kimlik dosyayla taşınır: yedekten geri yüklenen
/// dosya yedeğin kimliğini getirir, bu yüzden <c>RestoreService</c> onu döndürür (<see cref="Rotate(Guid?)"/>)
/// ve eskisini <c>PreviousReplicaId</c>'ye yazar; <c>sales/hello</c> onu <c>previousReplicaId</c> olarak taşır.</para>
///
/// <para><b>Biçim:</b> 049 tohumu <c>lower(hex(randomblob(16)))</c>, yani Guid "N" biçimi; bu depo da "N"
/// yazar. Okuma her standart Guid biçimini kabul eder. Eksik, ayrıştırılamayan ya da boş (sıfır) değer
/// <c>null</c> döner: çağıran v2 isteği göndermez (sunucu boş kimliği zaten 400 ile reddeder).</para>
/// </summary>
public sealed class ReplicaIdentityRepository
{
    private readonly IDbConnectionFactory _factory;

    public ReplicaIdentityRepository(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// <c>_meta</c>'da iki kimlik kolonu da var mı. Göç 049'dan önceki şemada (eski bir yedek) yoktur; geri
    /// yükleme o dosyada döndürme yapamaz, işi açılıştaki göçe bırakır.
    /// </summary>
    public bool HasIdentityColumns()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM pragma_table_info('_meta') WHERE name IN ('ReplicaId', 'PreviousReplicaId')") == 2;
    }

    /// <summary>Bu veritabanının kopya kimliği; eksik ya da bozuksa <c>null</c>.</summary>
    public Guid? GetReplicaId()
    {
        using var conn = _factory.Open();
        return Parse(conn.ExecuteScalar<string?>("SELECT ReplicaId FROM _meta WHERE Id = 1"));
    }

    /// <summary>Sunucuya henüz bildirilmemiş önceki kimlik (§8.7); yoksa ya da bozuksa <c>null</c>.</summary>
    public Guid? GetPendingPreviousReplicaId()
    {
        using var conn = _factory.Open();
        return Parse(conn.ExecuteScalar<string?>("SELECT PreviousReplicaId FROM _meta WHERE Id = 1"));
    }

    public void SetPendingPreviousReplicaId(Guid previous)
    {
        if (previous == Guid.Empty) throw new ArgumentException("Önceki kimlik boş olamaz.", nameof(previous));
        using var conn = _factory.Open();
        RequireMetaRow(conn.Execute(
            "UPDATE _meta SET PreviousReplicaId = @previous WHERE Id = 1",
            new { previous = previous.ToString("N") }));
    }

    /// <summary>
    /// Bekleyen önceki kimliği, yalnız hâlâ <paramref name="sent"/> ise siler (karşılaştır ve sil): hello
    /// uçarken başka bir değer yazıldıysa o değer bir sonraki hello'ya kalır.
    /// </summary>
    /// <returns>Silindiyse true.</returns>
    public bool ClearPendingPreviousReplicaId(Guid sent)
    {
        using var conn = _factory.Open();
        // Biçimden bağımsız eşitlik ("N" ya da "D", harf büyüklüğü): yalnız yerleşik fonksiyonlar.
        return conn.Execute(
            "UPDATE _meta SET PreviousReplicaId = NULL "
          + "WHERE Id = 1 AND lower(replace(PreviousReplicaId, '-', '')) = @sent",
            new { sent = sent.ToString("N") }) == 1;
    }

    /// <summary>Yeni bir kimlik üretir; önceki kimlik bugünkü kimlik olur (<see cref="Rotate(Guid?)"/>).</summary>
    public Guid Rotate() => Rotate(GetReplicaId());

    /// <summary>
    /// Yeni rastgele bir kimlik yazar ve <paramref name="previous"/>'u bekleyen önceki kimlik yapar. Tek
    /// ifade: kimlik ile önceki kimlik birlikte değişir. <paramref name="previous"/> <c>null</c> ise (önceki
    /// kimlik bilinmiyor) bekleyen değer olduğu gibi kalır.
    /// </summary>
    /// <returns>Yeni kimlik.</returns>
    public Guid Rotate(Guid? previous)
    {
        var next = Guid.NewGuid();
        using var conn = _factory.Open();
        RequireMetaRow(conn.Execute(
            "UPDATE _meta SET ReplicaId = @next, PreviousReplicaId = COALESCE(@previous, PreviousReplicaId) "
          + "WHERE Id = 1",
            new
            {
                next = next.ToString("N"),
                previous = previous is { } p && p != Guid.Empty ? p.ToString("N") : null,
            }));
        return next;
    }

    private static Guid? Parse(string? raw) =>
        Guid.TryParse(raw, out var id) && id != Guid.Empty ? id : null;

    private static void RequireMetaRow(int affected)
    {
        if (affected != 1)
            throw new InvalidOperationException("_meta satırı yok; kopya kimliği yazılamadı.");
    }
}
