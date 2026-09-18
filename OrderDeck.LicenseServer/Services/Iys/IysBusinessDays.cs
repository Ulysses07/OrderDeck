namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Ticari İletişim Yönetmeliği m.7/11-12: İYS dışında alınan onay <b>üç iş
/// günü</b> içinde kaydedilmezse geçersiz. Son tarih hesabı buradan çıkar.
///
/// <para><b>Resmî tatiller modellenmiyor</b> — yalnız Cumartesi/Pazar atlanıyor.
/// Yani hesaplanan son tarih gerçeğinden bir miktar <i>geç</i> olabilir. Bunun
/// telafisi boru hattının kendisi: push, onay yazıldıktan saniyeler sonra
/// çalışıyor ve son tarihe 24 saat kalanlar admin uyarısına düşüyor. Son tarih
/// bir hedef değil, sessiz kalmayı yasaklayan bir alarm eşiği.</para>
/// </summary>
public static class IysBusinessDays
{
    private static bool IsWeekend(DateTimeOffset d)
        => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>
    /// <paramref name="from"/> üstüne <paramref name="businessDays"/> iş günü ekler.
    /// Başlangıç hafta sonuna denk gelirse sayaç ilk iş gününden başlar: Cumartesi
    /// alınan onayın ilk iş günü Pazartesi'nin <i>kendisidir</i>, ondan sonraki gün
    /// değil. Hafta içi başlangıçta ise başlangıç günü sayılmaz, sayım ertesi
    /// günden yürür.
    /// </summary>
    public static DateTimeOffset Add(DateTimeOffset from, int businessDays)
    {
        var cursor = from;
        var remaining = businessDays;

        if (IsWeekend(cursor))
        {
            while (IsWeekend(cursor)) cursor = cursor.AddDays(1);
            // Taşındığımız gün sayımın ilk iş günüdür.
            remaining--;
        }

        while (remaining > 0)
        {
            cursor = cursor.AddDays(1);
            if (!IsWeekend(cursor)) remaining--;
        }
        return cursor;
    }
}

/// <summary>
/// Doğrulama randevu takvimi. İYS işleme anlık değil: push'tan hemen sonra
/// sormak, henüz işlenmemiş kaydı "RET" sanmaya yol açar. İlk deneme 15 dk
/// sonra, sonra artan aralıklarla.
///
/// <para>Push ve doğrulama işleri aynı diziyi okur — takvim TEK yerde durur.</para>
/// </summary>
public static class IysVerifySchedule
{
    public static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(24),
    };

    /// <summary>
    /// <paramref name="attempts"/> deneme yapılmışken bir sonraki randevu.
    /// Takvim tükendiyse <c>null</c> — artık beklemenin anlamı yok.
    /// </summary>
    public static DateTimeOffset? Next(DateTimeOffset from, int attempts)
        => attempts < Backoff.Length ? from + Backoff[attempts] : null;
}
