using FluentAssertions;
using OrderDeck.LicenseServer.Services.Iys;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

public class IysBusinessDaysTests
{
    // 2026-09-14 Pazartesi, 2026-09-19 Cumartesi, 2026-09-20 Pazar.
    private static DateTimeOffset Utc(int y, int m, int d)
        => new(y, m, d, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Hafta_ici_uc_is_gunu_dogrudan_eklenir()
    {
        // Pazartesi + 3 iş günü = Perşembe
        IysBusinessDays.Add(Utc(2026, 9, 14), 3).Should().Be(Utc(2026, 9, 17));
    }

    [Fact]
    public void Hafta_sonu_atlanir()
    {
        // Perşembe + 3 iş günü = Salı (Cmt/Paz sayılmaz)
        IysBusinessDays.Add(Utc(2026, 9, 17), 3).Should().Be(Utc(2026, 9, 22));
    }

    [Fact]
    public void Cumartesi_baslangici_once_pazartesiye_tasinir()
    {
        // Cumartesi başlayan sayaç ilk iş gününü Pazartesi sayar → Çarşamba
        IysBusinessDays.Add(Utc(2026, 9, 19), 3).Should().Be(Utc(2026, 9, 23));
    }

    [Fact]
    public void Ilk_dogrulama_randevusu_onbes_dakika_sonra()
    {
        var t = Utc(2026, 9, 14);
        IysVerifySchedule.Next(t, 0).Should().Be(t.AddMinutes(15));
        IysVerifySchedule.Next(t, 1).Should().Be(t.AddHours(1));
        IysVerifySchedule.Next(t, 3).Should().Be(t.AddHours(24));
    }

    [Fact]
    public void Takvim_tukenince_randevu_verilmez()
    {
        IysVerifySchedule.Next(Utc(2026, 9, 14), 4).Should().BeNull();
    }
}
