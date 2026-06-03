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
using OrderDeck.Core.Customers;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// <see cref="SupportRequestsViewModel"/> — yayıncı destek talepleri (forgot-
/// password fallback). Gerçek LicenseApiClient + fake HttpMessageHandler ile
/// load / issue-temp-password / WhatsApp link akışını doğrular.
/// </summary>
public class SupportRequestsViewModelTests
{
    private sealed class FakeLauncher : IUrlLauncher
    {
        public string? LastUrl { get; private set; }
        public void Launch(string url) => LastUrl = url;
    }

    /// <summary>GET → konfigüre edilmiş liste; POST issue-temp-password → sabit parola.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _listJson;
        private readonly string _tempPassword;
        public bool ThrowOnGet { get; set; }
        public int IssueCalls { get; private set; }

        public FakeHandler(IEnumerable<SupportRequestDto> list, string tempPassword)
        {
            _listJson = JsonSerializer.Serialize(list.ToArray());
            _tempPassword = tempPassword;
        }

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
                return Task.FromResult(Json(
                    JsonSerializer.Serialize(new IssueTempPasswordResponse(_tempPassword))));
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

    private static (SupportRequestsViewModel Vm, FakeLauncher Launcher) Build(
        IEnumerable<SupportRequestDto> list, string tempPassword = "tmppw2345",
        FakeHandler? handler = null)
    {
        handler ??= new FakeHandler(list, tempPassword);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://stub") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());
        var launcher = new FakeLauncher();
        var vm = new SupportRequestsViewModel(api, new WhatsAppMessageBuilder(), launcher);
        return (vm, launcher);
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
        var handler = new FakeHandler(Array.Empty<SupportRequestDto>(), "x") { ThrowOnGet = true };
        var (vm, _) = Build(Array.Empty<SupportRequestDto>(), handler: handler);

        await vm.LoadAsync();

        vm.ErrorMessage.Should().NotBeNullOrEmpty();
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task IssueTempPassword_sets_password_and_marks_resolved()
    {
        var (vm, _) = Build(new[] { Req("Pending One", "+905550000002", resolved: false) },
            tempPassword: "abcd2345ef");
        await vm.LoadAsync();
        var row = vm.Items.Single();

        await vm.IssueTempPasswordCommand.ExecuteAsync(row);

        row.TempPassword.Should().Be("abcd2345ef");
        row.HasTempPassword.Should().BeTrue();
        row.IsResolved.Should().BeTrue();
        row.CanIssue.Should().BeFalse("parola üretilince buton kaybolur");
        row.ShowResolvedLabel.Should().BeFalse("parola gösterildiği için 'Tamamlandı' yerine panel görünür");
    }

    [Fact]
    public async Task IssueTempPassword_ignores_already_resolved_row()
    {
        var handler = new FakeHandler(
            new[] { Req("Resolved One", "+905550000001", resolved: true) }, "tmppw2345");
        var (vm, _) = Build(Array.Empty<SupportRequestDto>(), handler: handler);
        await vm.LoadAsync();
        var row = vm.Items.Single();

        await vm.IssueTempPasswordCommand.ExecuteAsync(row);

        handler.IssueCalls.Should().Be(0, "zaten resolved talebe POST atılmaz");
        row.HasTempPassword.Should().BeFalse();
    }

    [Fact]
    public async Task SendWhatsApp_launches_wame_link_with_phone_and_password()
    {
        var (vm, launcher) = Build(
            new[] { Req("Ahmet", "+905551112233", resolved: false) },
            tempPassword: "kod2345xy");
        await vm.LoadAsync();
        var row = vm.Items.Single();
        await vm.IssueTempPasswordCommand.ExecuteAsync(row);

        vm.SendWhatsAppCommand.Execute(row);

        launcher.LastUrl.Should().NotBeNull();
        launcher.LastUrl!.Should().StartWith("https://wa.me/905551112233?text=");
        Uri.UnescapeDataString(launcher.LastUrl!).Should().Contain("kod2345xy");
        Uri.UnescapeDataString(launcher.LastUrl!).Should().Contain("Ahmet");
    }

    [Fact]
    public void SendWhatsApp_without_password_is_noop()
    {
        var (vm, launcher) = Build(Array.Empty<SupportRequestDto>());
        var row = SupportRequestsViewModel.SupportRequestRow.FromDto(
            Req("NoPw", "+905550000009", resolved: false));

        vm.SendWhatsAppCommand.Execute(row);

        launcher.LastUrl.Should().BeNull("parola yokken WhatsApp linki açılmaz");
    }

    [Fact]
    public void BuildMessage_contains_name_and_password()
    {
        var msg = SupportRequestsViewModel.BuildMessage("Zeynep", "kod2345xy");

        msg.Should().Contain("Zeynep");
        msg.Should().Contain("kod2345xy");
        msg.Should().Contain("Parolayı değiştir");
    }
}
