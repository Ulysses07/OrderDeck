using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// <see cref="SupportRequestsViewModel"/> — yayıncı destek talepleri (forgot-
/// password fallback). R7-02 sonrası sözleşme: sunucu parola DÖNDÜRMEZ, kendisi
/// SMS doğrulaması başlatır ve status="verification-sent" döner. Bu testler
/// başarıyı yalnız o status'a bağlar; parola hiçbir yüzeye sızmaz.
/// </summary>
public class SupportRequestsViewModelTests
{
    /// <summary>GET → konfigüre edilmiş liste; POST issue-temp-password → konfigüre edilen yanıt.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _listJson;
        public bool ThrowOnGet { get; set; }
        public int IssueCalls { get; private set; }

        /// <summary>issue-temp-password yanıtı. Varsayılan: yeni sözleşme.</summary>
        public Func<HttpResponseMessage> IssueResponse { get; set; } = () => Json(
            JsonSerializer.Serialize(new IssueTempPasswordResponse(null, "verification-sent")));

        public FakeHandler(IEnumerable<SupportRequestDto> list)
            => _listJson = JsonSerializer.Serialize(list.ToArray());

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/panel/support-requests")
            {
                if (ThrowOnGet) throw new HttpRequestException("network down");
                return Task.FromResult(Json(_listJson));
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/issue-temp-password"))
            {
                IssueCalls++;
                return Task.FromResult(IssueResponse());
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private static SupportRequestDto Req(string name, string phone, bool resolved) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, phone,
        "forgot-password", DateTimeOffset.UtcNow,
        resolved ? DateTimeOffset.UtcNow : null);

    private static (SupportRequestsViewModel Vm, FakeHandler Handler) Build(
        IEnumerable<SupportRequestDto> list, FakeHandler? handler = null)
    {
        handler ??= new FakeHandler(list);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://stub") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());
        return (new SupportRequestsViewModel(api), handler);
    }

    [Fact]
    public async Task LoadAsync_populates_items_pending_first()
    {
        var (vm, _) = Build(new[]
        {
            Req("Resolved One", "+905550000001", resolved: true),
            Req("Pending One", "+905550000002", resolved: false),
        });

        await vm.LoadAsync();

        vm.Items.Should().HaveCount(2);
        vm.IsEmpty.Should().BeFalse();
        vm.Items[0].ShopperName.Should().Be("Pending One", "bekleyenler önce sıralanır");
        vm.Items[0].CanIssue.Should().BeTrue();
        vm.Items[1].ShowResolvedLabel.Should().BeTrue();
        vm.Items[0].KindLabel.Should().Be("Parola sıfırlama");
    }

    [Fact]
    public async Task LoadAsync_empty_sets_IsEmpty()
    {
        var (vm, _) = Build(Array.Empty<SupportRequestDto>());

        await vm.LoadAsync();

        vm.Items.Should().BeEmpty();
        vm.IsEmpty.Should().BeTrue();
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_network_error_sets_ErrorMessage()
    {
        var handler = new FakeHandler(Array.Empty<SupportRequestDto>()) { ThrowOnGet = true };
        var (vm, _) = Build(Array.Empty<SupportRequestDto>(), handler);

        await vm.LoadAsync();

        vm.ErrorMessage.Should().NotBeNullOrEmpty();
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task Issue_verification_sent_status_marks_row_sent_and_resolved()
    {
        var (vm, _) = Build(new[] { Req("Pending One", "+905550000002", resolved: false) });
        await vm.LoadAsync();
        var row = vm.Items.Single();

        await vm.IssueTempPasswordCommand.ExecuteAsync(row);

        row.VerificationSent.Should().BeTrue();
        row.IsResolved.Should().BeTrue();
        row.CanIssue.Should().BeFalse("doğrulama başlatılınca buton kaybolur");
        row.ShowResolvedLabel.Should().BeFalse("'Tamamlandı' yerine SMS bilgi paneli görünür");
        row.RowError.Should().BeNull();
    }

    [Fact]
    public async Task Issue_success_requires_status_not_just_http_200()
    {
        // Eski sunucu davranışı: 200 + tempPassword dolu ama status yok.
        // Bu BAŞARI SAYILMAZ — istemci parolayı asla göstermeyeceği için
        // yayıncı "tamam" sanıp talebi kapatırsa shopper kilitli kalır.
        var handler = new FakeHandler(new[] { Req("Pending One", "+905550000002", resolved: false) })
        {
            IssueResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new IssueTempPasswordResponse(
                        $"pw-{Guid.NewGuid():N}", null)),
                    Encoding.UTF8, "application/json"),
            },
        };
        var (vm, _) = Build(Array.Empty<SupportRequestDto>(), handler);
        await vm.LoadAsync();
        var row = vm.Items.Single();

        await vm.IssueTempPasswordCommand.ExecuteAsync(row);

        row.VerificationSent.Should().BeFalse();
        row.IsResolved.Should().BeFalse("başarı kanıtı yokken talep kapatılmaz");
        row.RowError.Should().NotBeNullOrEmpty();
        row.CanIssue.Should().BeTrue("yayıncı tekrar deneyebilmeli");
    }

    [Fact]
    public async Task Issue_http_error_sets_RowError_and_keeps_row_open()
    {
        var handler = new FakeHandler(new[] { Req("Pending One", "+905550000002", resolved: false) })
        {
            IssueResponse = () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        var (vm, _) = Build(Array.Empty<SupportRequestDto>(), handler);
        await vm.LoadAsync();
        var row = vm.Items.Single();

        await vm.IssueTempPasswordCommand.ExecuteAsync(row);

        row.VerificationSent.Should().BeFalse();
        row.IsResolved.Should().BeFalse();
        row.RowError.Should().NotBeNullOrEmpty();
        row.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Issue_ignores_already_resolved_row()
    {
        var handler = new FakeHandler(
            new[] { Req("Resolved One", "+905550000001", resolved: true) });
        var (vm, _) = Build(Array.Empty<SupportRequestDto>(), handler);
        await vm.LoadAsync();
        var row = vm.Items.Single();

        await vm.IssueTempPasswordCommand.ExecuteAsync(row);

        handler.IssueCalls.Should().Be(0, "zaten resolved talebe POST atılmaz");
        row.VerificationSent.Should().BeFalse();
    }

    [Fact]
    public async Task Password_never_reaches_any_row_surface()
    {
        // Sunucu (yanlışlıkla) parola döndürse bile satırın hiçbir public
        // yüzeyinde parola metni bulunmamalı — repo public, ekran görüntüsü
        // riskli; parolanın tek yolu shopper'ın telefonundaki SMS.
        var leaked = $"pw-{Guid.NewGuid():N}";
        var handler = new FakeHandler(new[] { Req("Pending One", "+905550000002", resolved: false) })
        {
            IssueResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new IssueTempPasswordResponse(leaked, "verification-sent")),
                    Encoding.UTF8, "application/json"),
            },
        };
        var (vm, _) = Build(Array.Empty<SupportRequestDto>(), handler);
        await vm.LoadAsync();
        var row = vm.Items.Single();

        await vm.IssueTempPasswordCommand.ExecuteAsync(row);

        row.VerificationSent.Should().BeTrue();
        typeof(SupportRequestsViewModel.SupportRequestRow).GetProperties()
            .Select(p => p.GetValue(row)?.ToString() ?? "")
            .Should().NotContain(s => s.Contains(leaked), "parola hiçbir property'de görünmemeli");
    }
}
