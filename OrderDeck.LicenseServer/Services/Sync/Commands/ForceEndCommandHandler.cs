using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// <c>force-end</c> komutu — spec 2026-10-13 §2 ("Devir" b), §4.6 tablosu, §5.3, §5.7, sahip kararları Y7 ve Y8, M18,
/// §12 risk 4 ve 5, §15-43. Yük <c>{sessionId, confirm = true}</c>; <c>expected</c> (sipariş sürümleri) boş olmalı.
/// Sahibi susmuş CANLI yayını başka bilgisayardan bitirir ve aynı işlemde sunucuya devreder; yerel aynası yok.
/// <list type="number">
///   <item><b>Onay.</b> <c>confirm</c> true değilse 400 <c>invalid-payload</c>: yayını bitirmek operatörün açık
///   onayını ister (§5.7 onay metni). Yayın bu lisansta yoksa 400 <c>unknown-session</c>.</item>
///   <item><b>Devredilmiş yayın</b>: etkisiz başarı — 200 <c>unchanged</c>, <c>result.alreadyReleased = true</c> ve yayın
///   güncel hâliyle; <c>ForceEndedBy</c> ve devir anı değişmez (zaten iptal edilmiş siparişe <c>cancel</c> gibi).</item>
///   <item><b>Devrediliyor yayın</b> (bitti, devredilmedi): 409 <c>handing-over</c>, sahibin bildirdiği gönderilmemiş
///   etiket sayısı, makine ve son sinyalle — sahip ne kadar sessiz olursa olsun (Y8). Etiketleri sahipte bekliyor;
///   devralma eksik etiketle ödeme isteği ve koli üretirdi ve sunucu da eksik olduğu için bunu yakalayamazdı (M2).
///   Sahip hiç dönmeyecekse kapatma Burak'ın CLI'ıyladır (§4.10, risk 4).</item>
///   <item><b>Canlı yayın</b>: sahip kopyanın son kalp atışı (<c>WpfReplicas.LastSeenAt</c>) en az
///   <c>SalesSync:ForceEndSilenceMinutes</c> (Y7, varsayılan 15) önce olmalı; değilse 409 <c>owner-active</c> ve son
///   sinyal. Sahibi yazılmamış (eski sürümde açılmış, miras) yayında sessizlik ölçülemez: kapalı-hata, 409
///   <c>owner-active</c> ve boş son sinyal — onu kapatmak da CLI'ın işi (§4.10 "sahibi hiç kalmamış"). Sahibi yazılı
///   ama kopya satırı hiç olmayan (hiç sinyal vermemiş) sahip sessiz sayılır; günlüğe düşer.</item>
///   <item><b>Etki</b>, tek işlemde (<see cref="SalesCommandScope.ForceEndAsync"/>): koşullu UPDATE ile <c>EndedAt</c> =
///   sunucu saati, <c>ForceEndedBy</c> = çağıran kopya; ardından devir (<c>ReleasedAt</c> aynı an, yayının bütün
///   siparişlerine <c>SessionReleasedAt</c>) ve günlük satırı. Okumadan sonra sahibin kapanışı araya girdiyse koşullu
///   UPDATE hiçbir şey yazmaz; yayın yeniden okunur ve hâline göre <c>handing-over</c> ya da etkisiz başarı döner —
///   kapanışın <c>EndedAt</c>'i ezilmez (§10.1-11).</item>
/// </list>
/// Sonrası S1'in yollarıyla yürür: sahip aslında yayındaysa yerelde yayına devam eder (M18); yeni etiketleri ve damgasız
/// birim değişiklikleri kabul edilir, komutla damgalanmış birimde komut kazanır (K5 istisnası i); kapanışı devri görür.
/// </summary>
public sealed class ForceEndCommandHandler : ISalesCommandHandler
{
    public const string KindName = "force-end";

    private readonly SalesSyncOptions _options;
    private readonly ILogger<ForceEndCommandHandler> _log;

    public ForceEndCommandHandler(IOptions<SalesSyncOptions> options, ILogger<ForceEndCommandHandler> log)
    {
        _options = options.Value;
        _log = log;
    }

    public string Kind => KindName;

    /// <summary>Kanonik yük: yalnız yayın (onay doğrulamadan sonra hep true; özete girmez).</summary>
    public sealed record Payload(Guid SessionId);

    private sealed record Body(Guid? SessionId, bool? Confirm);

    public bool TryParse(
        JsonElement payload,
        IReadOnlyList<SalesCommandExpected> expected,
        [NotNullWhen(true)] out ISalesCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        command = null;
        if (!SalesCommandPayload.TryRead<Body>(payload, out var body, out error)) return false;
        if (body.SessionId is not { } sessionId || sessionId == Guid.Empty)
        {
            error = "sessionId boş olamaz.";
            return false;
        }
        if (body.Confirm is not true)
        {
            error = "confirm true olmalı: yayını bitirmek operatörün onayını ister.";
            return false;
        }
        if (expected.Count > 0)
        {
            error = "force-end sipariş sürümü taşımaz; expected boş olmalı.";
            return false;
        }

        command = new Command(new Payload(sessionId), _options.ForceEndSilence, _log);
        return true;
    }

    private sealed class Command(Payload payload, TimeSpan silence, ILogger log) : ISalesCommand
    {
        public object CanonicalPayload => payload;

        public IReadOnlyList<Guid> OrderIds => [];

        public IReadOnlyList<Guid> SessionIds => [payload.SessionId];

        public async Task<SalesCommandOutcome> ExecuteAsync(SalesCommandScope scope, CancellationToken ct)
        {
            var session = await scope.ReadSessionAsync(payload.SessionId, ct);
            if (session.ReleasedAt is not null) return AlreadyReleased();
            if (session.EndedAt is not null) throw await HandingOverAsync(scope, session, ct);

            if (session.OwnerReplicaId is not { } owner)
            {
                log.LogInformation(
                    "sales/commands: force-end sahipsiz (miras) canlı yayında reddedildi, owner-active; kapatma CLI'la "
                    + "(license={LicenseId}, session={SessionId})",
                    scope.LicenseId, session.Id);
                throw SalesCommandRejection.OwnerActive(session.Id, session.OwnerMachineName, null);
            }

            var lastSeenAt = await scope.ReadLastSeenAsync(owner, ct);
            if (lastSeenAt is null)
                log.LogWarning(
                    "sales/commands: force-end — yayının sahip kopyasının satırı yok, sahip sessiz sayıldı "
                    + "(license={LicenseId}, session={SessionId}, owner={OwnerReplicaId})",
                    scope.LicenseId, session.Id, owner);
            else if (lastSeenAt > scope.Now - silence)
                throw SalesCommandRejection.OwnerActive(session.Id, session.OwnerMachineName, lastSeenAt);

            if (await scope.ForceEndAsync(session.Id, ct)) return new SalesCommandOutcome([], []);

            // Okumadan sonra sahibin kapanışı commit oldu: koşullu UPDATE hiçbir şey yazmadı. Yayının şimdiki hâli
            // cevabı belirler — devrettiyse etkisiz başarı, etiketleri hâlâ sahipteyse Y8.
            var current = await scope.ReadSessionAsync(session.Id, ct);
            log.LogInformation(
                "sales/commands: force-end sahibin eşzamanlı kapanışına çarptı (license={LicenseId}, "
                + "session={SessionId}, released={Released})",
                scope.LicenseId, session.Id, current.ReleasedAt is not null);
            if (current.ReleasedAt is not null) return AlreadyReleased();
            if (current.EndedAt is not null) throw await HandingOverAsync(scope, current, ct);
            throw SalesCommandRejection.Stale("Yayın bu arada değişti; yeniden deneyin.");
        }

        private static SalesCommandOutcome AlreadyReleased() => new([], []) { AlreadyReleased = true };

        private static async Task<SalesCommandRejection> HandingOverAsync(
            SalesCommandScope scope, SessionSnapshot session, CancellationToken ct)
        {
            var ownerLastSeenAt = session.OwnerReplicaId is { } owner ? await scope.ReadLastSeenAsync(owner, ct) : null;
            return SalesCommandRejection.HandingOver(
                session.Id, session.UnsentLabelCount, session.OwnerMachineName, ownerLastSeenAt);
        }
    }
}
