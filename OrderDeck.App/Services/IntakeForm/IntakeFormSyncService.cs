using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.IntakeForm;

/// <summary>
/// Pulls new IntakeFormSubmission rows from the license server, upserts each
/// as a Customer (platform="form"), advances the cursor in AppSettings.
/// Idempotent: duplicate calls are no-op (server filters by SubmittedAt &gt; since).
/// </summary>
public sealed class IntakeFormSyncService
{
    private readonly LicenseApiClient _api;
    private readonly CustomerRepository _customers;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly IClock _clock;
    private readonly ILogger<IntakeFormSyncService> _log;

    public event EventHandler<int>? SubmissionsSynced;

    public IntakeFormSyncService(
        LicenseApiClient api,
        CustomerRepository customers,
        SettingsStore settingsStore,
        AppSettings settings,
        IClock clock,
        ILogger<IntakeFormSyncService> log)
    {
        _api = api;
        _customers = customers;
        _settingsStore = settingsStore;
        _settings = settings;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// UI freeze fix (2026-05-13): consecutive auth-failure tracking. 25 art arda
    /// 401 her 2 dk = exception storm UI thread'i etkiliyor. Auth hatasında
    /// hosted service backoff arttırsın diye flag expose ediyor.
    /// </summary>
    public bool LastSyncWasAuthFailure { get; private set; }

    /// <summary>
    /// Tek seferlik geriye-dönük düzeltme: FullName kolonu (migration 022) öncesi
    /// kaydolan müşterilerde gerçek Ad Soyad yerelde saklanmamıştı (chat takma adı
    /// korunuyordu). Sunucudaki TÜM form kayıtlarını (cursor'dan bağımsız, baştan)
    /// gezip her birinin gerçek Ad Soyad'ını eşleşen müşteri satırlarına yazar —
    /// yalnızca boş FullName'lere, LastSeenAt/DisplayName'e dokunmadan. Bir kez
    /// çalışır (AppSettings.FullNameBackfillDone), sonra no-op.
    /// </summary>
    public async Task<int> BackfillFullNamesOnceAsync(CancellationToken ct = default)
    {
        if (_settings.FullNameBackfillDone) return 0;

        int totalUpdated = 0;
        DateTimeOffset? since = null;
        var sinceId = Guid.Empty;
        // Sayfalama imleci (SubmittedAt, Id); son sayfa < limit olunca dur.
        // Yalnız damgayla ilerleseydi, tam bir sayfa dolusu kayıt aynı damgayı
        // paylaştığında imleç HİÇ ilerlemez ve döngü tavana kadar aynı sayfayı
        // çekerdi. Güvenlik tavanı yine de duruyor.
        for (var page = 0; page < 500 && !ct.IsCancellationRequested; page++)
        {
            List<IntakeFormSubmissionDto> submissions;
            try
            {
                submissions = await _api.GetFormSubmissionsAsync(since, sinceId, limit: 100, ct);
            }
            catch (LicenseApiException ex)
            {
                _log.LogWarning(ex, "FullName backfill fetch failed: {Code} (will retry next launch)", ex.Code);
                return totalUpdated; // flag'i işaretleme → sonraki açılışta tekrar dener
            }

            if (submissions.Count == 0) break;

            foreach (var sub in submissions)
            {
                var identities = new List<(string Platform, string Username)>();
                void Add(string platform, string? username)
                {
                    if (!string.IsNullOrWhiteSpace(username))
                        identities.Add((platform, username!));
                }
                // channelId varsa onunla, yoksa handle ile (repository ikisini de eşler).
                if (!string.IsNullOrWhiteSpace(sub.YouTubeChannelId))
                    Add("youtube", sub.YouTubeChannelId);
                else
                    Add("youtube", sub.YouTubeUsername);
                Add("instagram", sub.InstagramUsername);
                Add("facebook", sub.FacebookUsername);
                Add("tiktok", sub.TikTokUsername);

                if (identities.Count > 0 && !string.IsNullOrWhiteSpace(sub.FullName))
                    totalUpdated += _customers.BackfillFullNameForIdentities(identities, sub.FullName);
            }

            var last = submissions.OrderBy(s => s.SubmittedAt).ThenBy(s => s.Id).Last();
            since = last.SubmittedAt;
            sinceId = last.Id;

            if (submissions.Count < 100) break; // son sayfa
        }

        _settings.FullNameBackfillDone = true;
        _settingsStore.Save(_settings);
        _log.LogInformation("FullName backfill complete: {Count} row(s) updated", totalUpdated);
        return totalUpdated;
    }

    public async Task<int> SyncOnceAsync(CancellationToken ct = default)
    {
        var since = _settings.LastIntakeFormSync;
        var sinceId = _settings.LastIntakeFormSyncId ?? Guid.Empty;

        List<IntakeFormSubmissionDto> submissions;
        try
        {
            submissions = await _api.GetFormSubmissionsAsync(since, sinceId, limit: 50, ct);
            LastSyncWasAuthFailure = false;
        }
        catch (InvalidCredentialsException ex)
        {
            // Auth token expired — bir sonraki login'e kadar denemek log spam'i
            // ve exception storm yaratıyor. Hosted service'e flag ile bildir,
            // backoff aralığını uzatsın.
            LastSyncWasAuthFailure = true;
            _log.LogWarning(ex, "Intake form sync auth failed: {Code} (backing off)", ex.Code);
            return 0;
        }
        catch (LicenseApiException ex)
        {
            LastSyncWasAuthFailure = false;
            _log.LogWarning(ex, "Intake form sync failed: {Code}", ex.Code);
            return 0;
        }

        if (submissions.Count == 0) return 0;

        var nowUnix = _clock.UnixNow();

        // İmleç (SubmittedAt, Id) çifti; sunucu da bu sıraya göre sayfalıyor.
        // Yalnız en büyük SubmittedAt alınsaydı, aynı damgayı paylaşan kayıtlar
        // sayfa sınırında kesildiğinde kalanları bir daha hiç istenmezdi — ve o
        // satır bir müşteri KAYDI olduğu için eksik kendiliğinden kapanmazdı.
        var ordered = submissions.OrderBy(s => s.SubmittedAt).ThenBy(s => s.Id).ToList();

        foreach (var sub in ordered)
        {
            // Bildirilen platform kimliklerini topla (çoklu-platform).
            var identities = new List<(string Platform, string Username, string? PreferredDisplayName)>();
            void Add(string platform, string? username, string? display = null)
            {
                if (!string.IsNullOrWhiteSpace(username))
                    identities.Add((platform, username!, display));
            }
            // YouTube: doğrulama channelId çektiyse Username=channelId → chat kaydıyla
            // (youtube, channelId) BİREBİR eşleşir. Ama UI'da channelId ASLA görünmesin
            // diye @handle'ı PreferredDisplayName olarak taşırız (DisplayName=@handle).
            // channelId yoksa handle'a düş (repository DisplayName ile köprüler).
            if (!string.IsNullOrWhiteSpace(sub.YouTubeChannelId))
                Add("youtube", sub.YouTubeChannelId, sub.YouTubeUsername);
            else
                Add("youtube", sub.YouTubeUsername);
            Add("instagram", sub.InstagramUsername);
            Add("facebook", sub.FacebookUsername);
            Add("tiktok", sub.TikTokUsername);

            if (identities.Count > 0)
            {
                _customers.UpsertPersonFromIntake(
                    identities,
                    sub.FullName, sub.Address, sub.Phone,
                    sub.Email, sub.Tckn, sub.WhatsAppConsent, sub.SmsConsent,
                    nowUnix, sub.City, sub.District);
            }
            else
            {
                // Eski sunucudan gelen (platform alanları olmayan) gönderim —
                // legacy tek-satır davranışına düş.
                _customers.UpsertFromIntakeForm(
                    sub.Username, sub.FullName, sub.Address, sub.Phone, nowUnix);
            }
        }

        var last = ordered[^1];
        _settings.LastIntakeFormSync = last.SubmittedAt;
        _settings.LastIntakeFormSyncId = last.Id;
        _settingsStore.Save(_settings);

        _log.LogInformation("Intake form sync: {Count} submission(s) processed (cursor → {Cursor}/{CursorId})",
            submissions.Count, last.SubmittedAt, last.Id);

        SubmissionsSynced?.Invoke(this, submissions.Count);
        return submissions.Count;
    }
}
