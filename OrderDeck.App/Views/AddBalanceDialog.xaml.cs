using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Views;

/// <summary>
/// Yayıncı müşteriye iade/refund bakiyesi tanımlar. İki tip:
///   - Hatalı ürün (full): tam tutar bakiyeye eklenir
///   - Müşteri iadesi (net): tutar − kargo bakiyeye eklenir
/// </summary>
public partial class AddBalanceDialog : Window
{
    private readonly LicenseApiClient _api;
    private readonly Guid _wpfCustomerId;
    private bool _saving;

    /// <summary>Caller dialog kapandıktan sonra bakiye listesini tazelemek için kontrol eder.</summary>
    public bool Saved { get; private set; }

    public AddBalanceDialog(LicenseApiClient api, Guid wpfCustomerId, string customerLabel)
    {
        InitializeComponent();
        _api = api;
        _wpfCustomerId = wpfCustomerId;
        DataContext = new { CustomerLabel = customerLabel };
        UpdatePreview();
    }

    private void OnTypeChanged(object sender, RoutedEventArgs e)
    {
        if (LblShipping is null || TbShipping is null) return;
        var net = RbNet?.IsChecked == true;
        LblShipping.Visibility = net ? Visibility.Visible : Visibility.Collapsed;
        TbShipping.Visibility = net ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreview();
    }

    private void OnAmountChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private bool TryParseDecimal(string text, out decimal value)
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

        if (RbFull.IsChecked == true)
        {
            LblPreview.Text = $"Müşteriye {amount.ToString("N2", CultureInfo.GetCultureInfo("tr-TR"))} TL bakiye eklenecek.";
            return;
        }

        if (!TryParseDecimal(TbShipping.Text, out var shipping) || shipping < 0 || shipping >= amount)
        {
            LblError.Text = "Kargo tutarı geçersiz (0 ≤ kargo < tutar olmalı).";
            return;
        }

        var net = amount - shipping;
        var tr = CultureInfo.GetCultureInfo("tr-TR");
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
            DialogResult = true;
            Close();
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

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
