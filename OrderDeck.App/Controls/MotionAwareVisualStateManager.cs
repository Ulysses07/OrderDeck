using System.Windows;

namespace OrderDeck.App.Controls;

/// <summary>
/// Düğme şablonlarının durum yöneticisi: görsel durum geçişlerini <see cref="MotionPolicy"/>'ye
/// bağlar. Hareket kapalıyken durum yine değişir, yalnız geçiş süresi uygulanmaz (değerler anında
/// yerine oturur).
///
/// <para>Şablonun kök öğesine <c>VisualStateManager.CustomVisualStateManager</c> olarak verilir.
/// Durumsuzdur.</para>
/// </summary>
public sealed class MotionAwareVisualStateManager : VisualStateManager
{
    /// <summary>İstenen geçiş gerçekten oynatılsın mı.</summary>
    internal static bool Effective(bool requested) => requested && MotionPolicy.IsEnabled;

    /// <inheritdoc />
    protected override bool GoToStateCore(
        FrameworkElement control,
        FrameworkElement stateGroupsRoot,
        string stateName,
        VisualStateGroup group,
        VisualState state,
        bool useTransitions)
    {
        // Özel yönetici, şablonda TANIMLI OLMAYAN durumlar için de çağrılır: düğme her durum
        // değişiminde "Unfocused", "Valid", "Unchecked" gibi adları da ister. O zaman grup ve durum
        // null gelir; taban sınıfa verilirse ArgumentNullException atar. Varsayılan yönetici bu
        // durumda sessizce false döner; aynısını yapıyoruz.
        if (group is null || state is null) return false;

        return base.GoToStateCore(control, stateGroupsRoot, stateName, group, state, Effective(useTransitions));
    }
}
