using System;
using System.Collections.ObjectModel;
using System.Windows;
using ClosedXML.Excel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Core;
using LiveDeck.Core.Storage.Repositories;
using Microsoft.Win32;

namespace LiveDeck.App.ViewModels;

public sealed partial class StreamReportViewModel : ViewModelBase
{
    private readonly LabelRepository _labels;
    private readonly SessionRepository _sessions;
    private readonly GiveawayRepository _giveaways;

    [ObservableProperty] private string _durationLabel = "—";
    [ObservableProperty] private int    _totalLabels;
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private int    _uniqueCustomers;

    public ObservableCollection<TopCustomer>     TopCustomers { get; } = new();
    public ObservableCollection<GiveawaySummary> Giveaways    { get; } = new();

    private string? _sessionId;

    public StreamReportViewModel(LabelRepository labels, SessionRepository sessions, GiveawayRepository giveaways)
    {
        _labels = labels;
        _sessions = sessions;
        _giveaways = giveaways;
    }

    public void Load(string sessionId)
    {
        _sessionId = sessionId;

        var totals = _labels.GetSessionTotals(sessionId);
        TotalLabels = totals.PrintedCount;
        TotalAmount = totals.TotalAmount;
        UniqueCustomers = totals.UniqueCustomers;

        TopCustomers.Clear();
        foreach (var c in _labels.GetTopCustomersBySession(sessionId, limit: 10))
            TopCustomers.Add(c);

        Giveaways.Clear();
        foreach (var g in _giveaways.ListSummariesBySession(sessionId))
            Giveaways.Add(g);

        var session = _sessions.GetById(sessionId);
        if (session is not null)
        {
            var endedAt = session.EndedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var seconds = endedAt - session.StartedAt;
            DurationLabel = FormatDuration(seconds);
        }
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0) return "—";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} saat {ts.Minutes} dakika";
        return $"{ts.Minutes} dakika {ts.Seconds} saniye";
    }

    [RelayCommand]
    private void ExportToExcel()
    {
        if (_sessionId is null) return;

        var dlg = new SaveFileDialog
        {
            Filter = "Excel Workbook|*.xlsx",
            FileName = $"livedeck-rapor-{DateTime.Now:yyyy-MM-dd-HHmm}.xlsx",
            InitialDirectory = AppPaths.ReportsFolder
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Rapor");
            ws.Cell(1, 1).Value = "Yayın Raporu";
            ws.Cell(1, 1).Style.Font.Bold = true;

            ws.Cell(3, 1).Value = "Süre";          ws.Cell(3, 2).Value = DurationLabel;

            ws.Cell(4, 1).Value = "Toplam etiket"; ws.Cell(4, 2).Value = TotalLabels;
            ws.Cell(4, 2).Style.NumberFormat.Format = "0";

            ws.Cell(5, 1).Value = "Toplam ciro";   ws.Cell(5, 2).Value = TotalAmount;
            ws.Cell(5, 2).Style.NumberFormat.Format = "#,##0.00 \"TL\"";

            ws.Cell(6, 1).Value = "Tekil müşteri"; ws.Cell(6, 2).Value = UniqueCustomers;
            ws.Cell(6, 2).Style.NumberFormat.Format = "0";

            ws.Cell(8, 1).Value = "En çok alan müşteriler";
            ws.Cell(8, 1).Style.Font.Bold = true;

            ws.Cell(9, 1).Value = "Kullanıcı";
            ws.Cell(9, 2).Value = "Platform";
            ws.Cell(9, 3).Value = "Etiket";
            ws.Cell(9, 4).Value = "Tutar (TL)";
            ws.Range(9, 1, 9, 4).Style.Font.Bold = true;

            int row = 10;
            foreach (var c in TopCustomers)
            {
                ws.Cell(row, 1).Value = c.Username;
                ws.Cell(row, 2).Value = c.Platform;
                ws.Cell(row, 3).Value = c.LabelCount;
                ws.Cell(row, 3).Style.NumberFormat.Format = "0";
                ws.Cell(row, 4).Value = c.TotalAmount;
                ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00 \"TL\"";
                row++;
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(dlg.FileName);

            MessageBox.Show($"Rapor kaydedildi:\n{dlg.FileName}",
                "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Excel'e aktarma başarısız: {ex.Message}",
                "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
