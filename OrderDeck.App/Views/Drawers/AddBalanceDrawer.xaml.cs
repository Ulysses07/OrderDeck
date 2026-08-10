using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Drawers;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// AddBalanceDialog'un çekmece hâli. Yayıncı müşteriye iade bakiyesi tanımlar:
///   - Hatalı ürün (full): tam tutar bakiyeye eklenir
///   - Müşteri iadesi (net): tutar − kargo bakiyeye eklenir
///
/// Kayıt başarılıysa çekmece <c>Close(true)</c> ile kapanır; çağıran ayrıca
/// <see cref="Saved"/>'i okuyabilir (pencere sürümünün sözleşmesi korundu).
/// </summary>
public partial class AddBalanceDrawer : UserControl
{
    private readonly Drawer _drawer;
    private readonly LicenseApiClient _api;
    private readonly Guid _wpfCustomerId;
    private bool _saving;

    /// <summary>Çağıran, kapanış sonrası bakiye listesini tazelemek için okur.</summary>
    public bool Saved { get; private set; }

    private AddBalanceDrawer(Drawer drawer, LicenseApiClient api,
                             Guid wpfCustomerId, string customerLabel)
    {
        InitializeComponent();
        _drawer = drawer;
        _api = api;
        _wpfCustomerId = wpfCustomerId;
        DataContext = new { CustomerLabel = customerLabel };
        UpdatePreview();
    }

    public static AddBalanceDrawer Create(Drawer drawer, LicenseApiClient api,
                                          Guid wpfCustomerId, string customerLabel)
        => new(drawer, api, wpfCustomerId, customerLabel);

    private void OnTypeChanged(object sender, RoutedEventArgs e)
    {
        if (ShippingBlock is null) return;
        ShippingBlock.Visibility = RbNet?.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdatePreview();
    }

    private void OnAmountChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private static bool TryParseDecimal(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return decimal.TryParse(text.Replace(',', '.'),
            NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private void UpdatePreview()
    {
        if (LblPreview is null || LblError is null) return;
        LblError.Text = "";
        LblPreview.Text = "";

        if (!TryParseDecimal(TbAmount.Text, out var amount) || amount <= 0)
            return;

        var tr = CultureInfo.GetCultureInfo("tr-TR");

        if (RbFull.IsChecked == true)
        {
            LblPreview.Text = $"Müşteriye {amount.ToString("N2", tr)} TL bakiye eklenecek.";
            return;
        }

        if (!TryParseDecimal(TbShipping.Text, out var shipping) || shipping < 0 || shipping >= amount)
        {
            LblError.Text = "Kargo tutarı geçersiz (0 ≤ kargo < tutar olmalı).";
            return;
        }

        var net = amount - shipping;
        LblPreview.Text = $"Müşteriye {net.ToString("N2", tr)} TL eklenecek "
            + $"({amount.ToString("N2", tr)} − {shipping.ToString("N2", tr)} kargo).";
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        LblError.Text = "";

        if (!TryParseDecimal(TbAmount.Text, out var amount) || amount <= 0)
        {
            LblError.Text = "Geçerli bir tutar gir.";
            return;
        }

        _saving = true;
        BtnSave.IsEnabled = false;

        try
        {
            if (RbFull.IsChecked == true)
            {
                await _api.AddRefundFullAsync(_wpfCustomerId,
                    new RefundFullRequest(amount, NormalizeReason(TbReason.Text)),
                    CancellationToken.None);
            }
            else
            {
                if (!TryParseDecimal(TbShipping.Text, out var shipping)
                    || shipping < 0 || shipping >= amount)
                {
                    LblError.Text = "Kargo tutarı geçersiz.";
                    return;
                }
                await _api.AddRefundNetAsync(_wpfCustomerId,
                    new RefundNetRequest(amount, shipping, NormalizeReason(TbReason.Text)),
                    CancellationToken.None);
            }
            Saved = true;
            _drawer.Close(true);
        }
        catch (Exception ex)
        {
            LblError.Text = $"Kaydedilemedi: {ex.Message}";
        }
        finally
        {
            _saving = false;
            BtnSave.IsEnabled = true;
        }
    }

    private static string? NormalizeReason(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        return t.Length > 500 ? t[..500] : t;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _drawer.Close(false);
}
