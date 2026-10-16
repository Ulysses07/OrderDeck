using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// <c>sales-sync force-end --session &lt;guid&gt; [--apply]</c> — spec 2026-10-13 §2 ("Devir" b), §4.10, §4.1, sahip
/// kararları Y7 ve Y8, §12 risk 4, §15-43.
///
/// <para><b>Ne için.</b> Sahibi hiç kalmamış ya da hiç dönmeyecek yayını kapatır ve sunucuya devreder. Arayüzdeki
/// <c>force-end</c> komutu yalnız sahibi ≥ 15 dk susmuş CANLI yayına uygulanır (§4.6); DEVREDİLİYOR yayını (bitti,
/// etiketleri sahipte bekliyor; örn. bir etiket kalıcı olarak gönderilemiyor, risk 4) bitirmenin tek yolu bu CLI'dır.
/// Sessizlik şartı yoktur: Burak kuru koşuda sahibin son sinyalini ve gönderilmemiş etiket sayısını görerek
/// karar verir. Yayın Id'si lisanslar arasında tekildir; lisans verilmez. İki kipte de çalışır.</para>
///
/// <para><b>Kuru koşu</b> (varsayılan) hiçbir şey yazmaz; yayının lisansını ve kipini, hâlini (canlı, devrediliyor,
/// devredilmiş), başlangıç, bitiş ve devir anlarını, zorla bitireni, sahip kopyayı ve son sinyalini, sahibin bildirdiği
/// gönderilmemiş etiket sayısını (<c>UnsentLabelCount</c>) ve sipariş sayısını basar. Makine adı ve başlık
/// basılmaz.</para>
///
/// <para><b><c>--apply</c></b>, tek işlemde:
/// <list type="bullet">
///   <item><b>Canlı</b> (<c>EndedAt IS NULL</c>): koşullu UPDATE (<c>EndedAt IS NULL ∧ ReleasedAt IS NULL</c>) ile
///   <c>EndedAt</c> = şimdi, <c>ForceEndedBy = cli</c>, <c>UpdatedAt</c> = şimdi (panelin oturum listesi bitişi görsün;
///   <c>force-end</c> komutuyla aynı); ardından <see cref="SessionReleaser"/> devri aynı anla yazar. Sonuç
///   <c>EndedAt</c> = <c>ReleasedAt</c>.</item>
///   <item><b>Devrediliyor</b>: koşullu UPDATE (<c>EndedAt IS NOT NULL ∧ ReleasedAt IS NULL</c>) yalnız
///   <c>ForceEndedBy = cli</c> yazar; ardından devir şimdiyle. <c>EndedAt</c>'e dokunulmaz: o sahibin kapanış anıdır ve
///   devir anından farklı kalır.</item>
/// </list>
/// Bu iki alan zorla bitirilen yayının bitirildiği andaki hâlini kalıcı olarak taşır
/// (<see cref="SalesReplicaService.PhaseAtForceEnd"/>): eşitse canlıydı (başka makinedeki geri yüklenmiş kopya sahip
/// yeterince sustuysa sahip sayılır, Y7), farklıysa devrediliyordu (hiç sayılmaz, Y8). Devir yayının bütün
/// siparişlerine <c>SessionReleasedAt</c> basar (DM3); birimlere ve sürümlere dokunmaz. Sahip sonradan dönerse
/// eklemeleri ve damgasız değişiklikleri K5 istisnası (i) ile alınır.</para>
///
/// <para><b>Yarış.</b> Hâl okunduktan sonra sahibin kapanışı (<c>sessions/sync</c>, <c>sales/backfill</c>) araya girip
/// commit olursa koşullu UPDATE satırı beklenen hâlde bulmaz: işlem geri alınır, hiçbir şey yazılmaz, yayının yeni
/// hâli basılır ve 1 döner — yeniden çalıştırınca yeni hâle göre davranır. CLI önce commit olursa sahibin kapanışı
/// <c>EndedAt</c>'i ezmez (yalnız boşsa yazar, §4.1) ve ikinci bir devir yazmaz. Devredilmiş yayın etkisizdir: 0,
/// hiçbir şey yazılmaz. Bilinmeyen yayın 2.</para>
/// </summary>
public static partial class SalesSync
{
    /// <summary><c>force-end</c>'in ayrıştırılmış argümanları.</summary>
    /// <param name="Apply">false = kuru koşu.</param>
    public sealed record ForceEndArgs(Guid Session, bool Apply) : SalesSyncArgs;

    /// <summary><c>force-end</c>'in gövdesi: argümanlar ayrıştırılmış, bağlantı nesnesi kurulmuş. Testler gerçek SQL
    /// Server'a buradan girer.</summary>
    public static async Task<int> RunAsync(
        LicenseDbContext db, ForceEndArgs args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var session = await ReadForceEndSessionAsync(db, args.Session, ct);
        if (session is null)
        {
            error.WriteLine($"Yayın bulunamadı: {args.Session}. Hiçbir şey yazılmadı.");
            return 2;
        }

        var now = DateTimeOffset.UtcNow;
        var mode = await db.Licenses.AsNoTracking()
            .Where(l => l.Id == session.LicenseId)
            .Select(l => l.SalesSyncMode)
            .SingleAsync(ct);
        var ownerLastSeenAt = session.OwnerReplicaId is { } owner
            ? await db.WpfReplicas.AsNoTracking()
                .Where(r => r.LicenseId == session.LicenseId && r.ReplicaId == owner)
                .Select(r => (DateTimeOffset?)r.LastSeenAt)
                .SingleOrDefaultAsync(ct)
            : null;
        var orderCount = await db.Orders.AsNoTracking()
            .CountAsync(o => o.LicenseId == session.LicenseId && o.SessionId == session.Id, ct);

        output.WriteLine(
            $"{(args.Apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA")} — sales-sync force-end — yayın {session.Id} — {Time(now)}");
        output.WriteLine($"Lisans: {session.LicenseId}, mod: {mode}");
        WriteForceEndState(output, session);
        output.WriteLine(session.OwnerReplicaId is { } ownerId
            ? $"Sahip kopya: {ownerId}, sahibin son sinyali: {(ownerLastSeenAt is null ? "kopya satırı yok" : Time(ownerLastSeenAt))}"
            : "Sahip kopya: yok (miras)");
        output.WriteLine("Gönderilmemiş etiket (sahibin bildirdiği): "
            + (session.UnsentLabelCount?.ToString() ?? "bildirilmedi"));
        output.WriteLine($"Sipariş: {orderCount}");

        if (session.Phase == ForceEndPhase.Released)
        {
            output.WriteLine("Yayın zaten devredilmiş: yapılacak bir şey yok, hiçbir şey yazılmadı.");
            return 0;
        }

        output.WriteLine(session.Phase == ForceEndPhase.Live
            ? $"Yapılacak: EndedAt = ReleasedAt = şimdi, ForceEndedBy = {StreamSession.ForceEndedByCli}; "
                + "siparişlere devir anı (SessionReleasedAt)"
            : $"Yapılacak: EndedAt değişmez (sahibin kapanış anı), ReleasedAt = şimdi, "
                + $"ForceEndedBy = {StreamSession.ForceEndedByCli}; siparişlere devir anı (SessionReleasedAt)");
        if (!args.Apply)
        {
            output.WriteLine("Kuru çalıştırma: hiçbir şey yazılmadı. Yazmak için --apply ekle.");
            return 0;
        }

        var licenseId = session.LicenseId;
        var sessionId = session.Id;
        const string endedBy = StreamSession.ForceEndedByCli;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var updated = session.Phase == ForceEndPhase.Live
            ? await db.StreamSessions
                .Where(s => s.LicenseId == licenseId && s.Id == sessionId && s.EndedAt == null && s.ReleasedAt == null)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.EndedAt, now)
                    .SetProperty(s => s.ForceEndedBy, endedBy)
                    .SetProperty(s => s.UpdatedAt, now), ct)
            : await db.StreamSessions
                .Where(s => s.LicenseId == licenseId && s.Id == sessionId && s.EndedAt != null && s.ReleasedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.ForceEndedBy, endedBy), ct);
        if (updated == 0)
        {
            await transaction.RollbackAsync(ct);
            var current = await ReadForceEndSessionAsync(db, sessionId, ct);
            error.WriteLine(
                $"Yayın bu arada değişti: şimdi {PhaseName(current!.Phase)} (EndedAt={Time(current.EndedAt)}, "
                + $"ReleasedAt={Time(current.ReleasedAt)}); hiçbir şey yazılmadı. Komutu yeniden çalıştır.");
            return 1;
        }

        // Aynı işlemde az önce bitirilen (ya da işaretlenen) ve devredilmemiş satır; UPDATE'in satır kilidi işlem
        // sonuna kadar sürdüğü için devir onu bulur.
        var released = await new SessionReleaser(db).ReleaseAsync(licenseId, [sessionId], now, ct);
        if (!released.Contains(sessionId))
            throw new InvalidOperationException("Zorla bitirilen yayın aynı işlemde devredilemedi.");
        await transaction.CommitAsync(ct);

        var after = await ReadForceEndSessionAsync(db, sessionId, ct);
        var stamped = await db.Orders.AsNoTracking()
            .CountAsync(o => o.LicenseId == licenseId && o.SessionId == sessionId && o.SessionReleasedAt == now, ct);
        output.WriteLine($"UYGULANDI: EndedAt={Time(after!.EndedAt)}, ReleasedAt={Time(after.ReleasedAt)}, "
            + $"ForceEndedBy={after.ForceEndedBy}; {stamped} siparişe devir anı basıldı");
        return 0;
    }

    private enum ForceEndPhase { Live, HandingOver, Released }

    /// <summary>Yayının <c>force-end</c>'te basılan alanları — makine adı ve başlık bilerek yok.</summary>
    private sealed record ForceEndSession(
        Guid Id,
        Guid LicenseId,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        DateTimeOffset? ReleasedAt,
        Guid? OwnerReplicaId,
        string? ForceEndedBy,
        int? UnsentLabelCount)
    {
        /// <summary>§2 tablosu: <c>ReleasedAt</c> dolu devredilmiş; değilse <c>EndedAt</c> dolu devrediliyor; ikisi
        /// de boş canlı.</summary>
        public ForceEndPhase Phase =>
            ReleasedAt is not null ? ForceEndPhase.Released
            : EndedAt is not null ? ForceEndPhase.HandingOver
            : ForceEndPhase.Live;
    }

    private static Task<ForceEndSession?> ReadForceEndSessionAsync(
        LicenseDbContext db, Guid sessionId, CancellationToken ct) =>
        db.StreamSessions.AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new ForceEndSession(
                s.Id, s.LicenseId, s.StartedAt, s.EndedAt, s.ReleasedAt, s.OwnerReplicaId, s.ForceEndedBy,
                s.UnsentLabelCount))
            .SingleOrDefaultAsync(ct);

    private static void WriteForceEndState(TextWriter output, ForceEndSession session)
    {
        output.WriteLine($"Hâl: {PhaseName(session.Phase)}");
        output.WriteLine($"Başlangıç={Time(session.StartedAt)}, bitiş (EndedAt)={Time(session.EndedAt)}, "
            + $"devir (ReleasedAt)={Time(session.ReleasedAt)}, zorla bitiren={session.ForceEndedBy ?? "yok"}");
    }

    private static string PhaseName(ForceEndPhase phase) => phase switch
    {
        ForceEndPhase.Live => "canlı",
        ForceEndPhase.HandingOver => "devrediliyor",
        _ => "devredilmiş",
    };

    /// <summary><c>force-end</c>: <c>--session</c> zorunlu, <c>--apply</c> isteğe bağlı. Yayın Id'si lisanslar arasında
    /// tekil olduğu için <c>--license</c> tanınmaz.</summary>
    private static bool TryParseForceEnd(string[] args, int start, [NotNullWhen(true)] out SalesSyncArgs? parsed)
    {
        parsed = null;
        if (!TryReadOptions(args, start, ["--session"], ["--apply"], out var values, out var flags)
            || !TryGuidOption(values, "--session", out var session))
            return false;

        parsed = new ForceEndArgs(session, flags.Contains("--apply"));
        return true;
    }
}
