namespace OrderDeck.Tests.Sync;

/// <summary>
/// Sunucudaki <c>CustomerSyncFields</c>'ın (OrderDeck.LicenseServer/Services/CustomerSync/
/// CustomerSyncFields.cs) BAĞIMSIZ kopyası — sahte sunucunun (<see cref="FakeCustomerServer"/>) gelen
/// birimleri. İstemcinin <c>CustomerSyncState</c>'ini KULLANMAZ: sahte sunucu istemci kodunun
/// yankısı değil, ayrı bir kâhin olmalı (C11 incelemesi M-6) — iki taraf ayrışırsa yakınsama
/// testleri bunu yakalar. Tek fark: TCKN şifrelenmez (<see cref="Tckn"/> düz; üretimde
/// <c>TcknProtected</c>, kuralı aynı — kırpılmaz).
/// </summary>
internal sealed record ServerSyncFields
{
    public string? FullName { get; init; }
    public DateTimeOffset? FullNameChangedAt { get; init; }
    public string? DisplayName { get; init; }
    public DateTimeOffset? DisplayNameChangedAt { get; init; }
    public string? GroupId { get; init; }
    public DateTimeOffset? GroupIdChangedAt { get; init; }

    public string? Address { get; init; }
    public string? City { get; init; }
    public string? District { get; init; }
    public DateTimeOffset? AddressChangedAt { get; init; }
    public bool RecipientPaysActive { get; init; }
    public DateTimeOffset? RecipientPaysChangedAt { get; init; }

    public string? Phone { get; init; }
    public DateTimeOffset? PhoneChangedAt { get; init; }
    public string? Email { get; init; }
    public DateTimeOffset? EmailChangedAt { get; init; }
    public string? Tckn { get; init; }
    public DateTimeOffset? TcknChangedAt { get; init; }
    public bool WhatsAppConsent { get; init; }
    public DateTimeOffset? WhatsAppConsentChangedAt { get; init; }
    public bool SmsConsent { get; init; }
    public DateTimeOffset? SmsConsentChangedAt { get; init; }

    public bool IsBlacklisted { get; init; }
    public string? BlacklistReason { get; init; }
    public DateTimeOffset? BlacklistedAt { get; init; }
    public DateTimeOffset? BlacklistChangedAt { get; init; }

    public string? Notes { get; init; }
    public DateTimeOffset? NotesChangedAt { get; init; }

    public static ServerSyncFields From(FakeCustomerServer.Row p) => new()
    {
        FullName = p.FullName, FullNameChangedAt = p.FullNameChangedAt,
        DisplayName = p.DisplayName, DisplayNameChangedAt = p.DisplayNameChangedAt,
        GroupId = p.GroupId, GroupIdChangedAt = p.GroupIdChangedAt,
        Address = p.Address, City = p.City, District = p.District, AddressChangedAt = p.AddressChangedAt,
        RecipientPaysActive = p.RecipientPaysActive, RecipientPaysChangedAt = p.RecipientPaysChangedAt,
        Phone = p.Phone, PhoneChangedAt = p.PhoneChangedAt,
        Email = p.Email, EmailChangedAt = p.EmailChangedAt,
        Tckn = p.Tckn, TcknChangedAt = p.TcknChangedAt,
        WhatsAppConsent = p.WhatsAppConsent, WhatsAppConsentChangedAt = p.WhatsAppConsentChangedAt,
        SmsConsent = p.SmsConsent, SmsConsentChangedAt = p.SmsConsentChangedAt,
        IsBlacklisted = p.IsBlacklisted, BlacklistReason = p.BlacklistReason,
        BlacklistedAt = p.BlacklistedAt, BlacklistChangedAt = p.BlacklistChangedAt,
        Notes = p.Notes, NotesChangedAt = p.NotesChangedAt,
    };

    /// <summary>Yalnız DAMGALI birimler (sunucu <c>StampedOnly</c>).</summary>
    public ServerSyncFields StampedOnly() => this with
    {
        FullName = FullNameChangedAt is null ? null : FullName,
        DisplayName = DisplayNameChangedAt is null ? null : DisplayName,
        GroupId = GroupIdChangedAt is null ? null : GroupId,
        Address = AddressChangedAt is null ? null : Address,
        City = AddressChangedAt is null ? null : City,
        District = AddressChangedAt is null ? null : District,
        RecipientPaysActive = RecipientPaysChangedAt is not null && RecipientPaysActive,
        Phone = PhoneChangedAt is null ? null : Phone,
        Email = EmailChangedAt is null ? null : Email,
        Tckn = TcknChangedAt is null ? null : Tckn,
        WhatsAppConsent = WhatsAppConsentChangedAt is not null && WhatsAppConsent,
        SmsConsent = SmsConsentChangedAt is not null && SmsConsent,
        IsBlacklisted = BlacklistChangedAt is not null && IsBlacklisted,
        BlacklistReason = BlacklistChangedAt is null ? null : BlacklistReason,
        BlacklistedAt = BlacklistChangedAt is null ? null : BlacklistedAt,
        Notes = NotesChangedAt is null ? null : Notes,
    };

    /// <summary><c>ScrubPersonal</c>'ın boşalttığı birimler çıkarılır (sunucu <c>WithoutScrubbedUnits</c>).</summary>
    public ServerSyncFields WithoutScrubbedUnits() => this with
    {
        FullName = null, FullNameChangedAt = null,
        DisplayName = null, DisplayNameChangedAt = null,
        Address = null, City = null, District = null, AddressChangedAt = null,
        Phone = null, PhoneChangedAt = null,
        Email = null, EmailChangedAt = null,
        Tckn = null, TcknChangedAt = null,
        WhatsAppConsent = false, WhatsAppConsentChangedAt = null,
        SmsConsent = false, SmsConsentChangedAt = null,
    };
}

/// <summary>
/// Sunucudaki <c>CustomerFieldMerge.Apply</c>'ın (origin/master) BİREBİR kopyası — kolon sınırına
/// kırpma, sunucunun kendi FullName doldurması (istemcinin takma ad kuralı YOK; takma ad gibi duran
/// ad boşa doldurulur, takma addan gerçek ada yükseltilir) ve eşit damgada "ilk gelen kalır" dahil.
/// İstemcinin <c>CustomerUnitMerge</c>'ünden bağımsızdır (C11 incelemesi M-6): sunucu değişirse bu
/// dosya ondan yenilenir. <c>ApplyLegacy</c> (biçim 1) taşınmadı — sahte sunucu biçim 1'i hata sayar.
/// </summary>
internal static class ServerCustomerFieldMerge
{
    public const int NameMax = 200, GroupIdMax = 64, PhoneMax = 20, AddressMax = 500,
        CityMax = 64, EmailMax = 254, NotesMax = 2000, ReasonMax = 500;

    public static bool Apply(FakeCustomerServer.Row t, ServerSyncFields f)
    {
        if (t.PurgedAt is not null) return false;

        return
            Unit(f.DisplayNameChangedAt, t.DisplayNameChangedAt, at => t.DisplayNameChangedAt = at,
                write: () => t.DisplayName = Norm(f.DisplayName, NameMax),
                fill: () => Fill(t.DisplayName, f.DisplayName, NameMax, v => t.DisplayName = v))
          | Unit(f.FullNameChangedAt, t.FullNameChangedAt, at => t.FullNameChangedAt = at,
                write: () => t.FullName = Norm(f.FullName, NameMax),
                fill: () => FillOrUpgradeName(t, f.FullName, f.DisplayName))
          | Unit(f.GroupIdChangedAt, t.GroupIdChangedAt, at => t.GroupIdChangedAt = at,
                write: () => t.GroupId = Norm(f.GroupId, GroupIdMax),
                fill: () => Fill(t.GroupId, f.GroupId, GroupIdMax, v => t.GroupId = v))
          | Unit(f.AddressChangedAt, t.AddressChangedAt, at => t.AddressChangedAt = at,
                write: () =>
                {
                    t.Address = Norm(f.Address, AddressMax);
                    t.City = Norm(f.City, CityMax);
                    t.District = Norm(f.District, CityMax);
                },
                fill: () => FillAddressBlock(t, f))
          | Unit(f.RecipientPaysChangedAt, t.RecipientPaysChangedAt, at => t.RecipientPaysChangedAt = at,
                write: () => t.RecipientPaysActive = f.RecipientPaysActive,
                fill: () => FillFlag(t.RecipientPaysActive, f.RecipientPaysActive, () => t.RecipientPaysActive = true))
          | Unit(f.PhoneChangedAt, t.PhoneChangedAt, at => t.PhoneChangedAt = at,
                write: () => t.Phone = Norm(f.Phone, PhoneMax),
                fill: () => Fill(t.Phone, f.Phone, PhoneMax, v => t.Phone = v))
          | Unit(f.EmailChangedAt, t.EmailChangedAt, at => t.EmailChangedAt = at,
                write: () => t.Email = Norm(f.Email, EmailMax),
                fill: () => Fill(t.Email, f.Email, EmailMax, v => t.Email = v))
          | Unit(f.TcknChangedAt, t.TcknChangedAt, at => t.TcknChangedAt = at,
                write: () => t.Tckn = Norm(f.Tckn),
                fill: () => Fill(t.Tckn, f.Tckn, int.MaxValue, v => t.Tckn = v))
          | Unit(f.WhatsAppConsentChangedAt, t.WhatsAppConsentChangedAt, at => t.WhatsAppConsentChangedAt = at,
                write: () => t.WhatsAppConsent = f.WhatsAppConsent,
                fill: () => FillFlag(t.WhatsAppConsent, f.WhatsAppConsent, () => t.WhatsAppConsent = true))
          | Unit(f.SmsConsentChangedAt, t.SmsConsentChangedAt, at => t.SmsConsentChangedAt = at,
                write: () => t.SmsConsent = f.SmsConsent,
                fill: () => FillFlag(t.SmsConsent, f.SmsConsent, () => t.SmsConsent = true))
          | Unit(f.BlacklistChangedAt, t.BlacklistChangedAt, at => t.BlacklistChangedAt = at,
                write: () =>
                {
                    t.IsBlacklisted = f.IsBlacklisted;
                    t.BlacklistReason = Norm(f.BlacklistReason, ReasonMax);
                    t.BlacklistedAt = f.BlacklistedAt;
                },
                fill: () => FillBlacklist(t, f))
          | Unit(f.NotesChangedAt, t.NotesChangedAt, at => t.NotesChangedAt = at,
                write: () => t.Notes = Norm(f.Notes, NotesMax),
                fill: () => Fill(t.Notes, f.Notes, NotesMax, v => t.Notes = v));
    }

    private static bool Unit(DateTimeOffset? incomingAt, DateTimeOffset? targetAt,
        Action<DateTimeOffset> setStamp, Action write, Func<bool> fill)
    {
        if (incomingAt is { } at)
        {
            if (targetAt is { } current && at <= current) return false;
            write();
            setStamp(at);
            return true;
        }
        return targetAt is null && fill();
    }

    private static bool FillOrUpgradeName(FakeCustomerServer.Row t, string? incomingName, string? incomingDisplayName)
    {
        if (Fill(t.FullName, incomingName, NameMax, v => t.FullName = v)) return true;
        var incoming = Norm(incomingName, NameMax);
        if (incoming is null || SameText(incoming, t.FullName)) return false;
        bool IsNickname(string? s) =>
            SameText(s, t.DisplayName) || SameText(s, incomingDisplayName) || SameText(s, t.Username);
        if (!IsNickname(t.FullName) || IsNickname(incoming)) return false;
        t.FullName = incoming;
        return true;
    }

    private static bool FillAddressBlock(FakeCustomerServer.Row t, ServerSyncFields f)
    {
        var address = Norm(f.Address, AddressMax);
        var city = Norm(f.City, CityMax);
        var district = Norm(f.District, CityMax);
        var targetEmpty = IsBlank(t.Address) && IsBlank(t.City) && IsBlank(t.District);
        if (!targetEmpty)
        {
            if (!Compatible(t.Address, address) || !Compatible(t.City, city) || !Compatible(t.District, district))
                return false;
            if (!SameText(t.Address, address) && !SameText(t.City, city) && !SameText(t.District, district))
                return false;
        }
        return Fill(t.Address, address, AddressMax, v => t.Address = v)
             | Fill(t.City, city, CityMax, v => t.City = v)
             | Fill(t.District, district, CityMax, v => t.District = v);
    }

    private static bool FillBlacklist(FakeCustomerServer.Row t, ServerSyncFields f)
    {
        if (!f.IsBlacklisted || t.IsBlacklisted) return false;
        t.IsBlacklisted = true;
        if (IsBlank(t.BlacklistReason)) t.BlacklistReason = Norm(f.BlacklistReason, ReasonMax);
        t.BlacklistedAt ??= f.BlacklistedAt;
        return true;
    }

    private static bool FillFlag(bool current, bool incoming, Action setTrue)
    {
        if (current || !incoming) return false;
        setTrue();
        return true;
    }

    private static bool Fill(string? current, string? incoming, int max, Action<string?> set)
    {
        if (!IsBlank(current)) return false;
        var value = Norm(incoming, max);
        if (value is null) return false;
        set(value);
        return true;
    }

    private static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);

    private static bool Compatible(string? current, string? incoming)
        => IsBlank(current) || incoming is null || SameText(current, incoming);

    internal static bool SameText(string? a, string? b)
        => !IsBlank(a) && !IsBlank(b) && Key(a!) == Key(b!);

    private static string Key(string s)
        => s.Trim().ToLowerInvariant().Replace('İ', 'i').Replace('ı', 'i');

    internal static string? Norm(string? s, int max = int.MaxValue)
        => IsBlank(s) ? null : Clip(s, max);

    internal static string? Clip(string? s, int max)
    {
        if (s is null || s.Length <= max) return s;
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut];
    }
}
