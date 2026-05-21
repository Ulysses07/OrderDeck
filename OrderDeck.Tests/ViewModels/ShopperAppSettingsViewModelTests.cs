using System;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.ViewModels;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public class ShopperAppSettingsViewModelTests
{
    private static readonly Guid TestLicenseId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string SerializeResponse(ShopperCodeResponse r)
    {
        return JsonSerializer.Serialize(new
        {
            code = r.Code,
            updatedAt = r.UpdatedAt,
            canChangeAt = r.CanChangeAt,
            licenseId = r.LicenseId
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    /// <summary>
    /// Builds a LicenseApiClient backed by a fake handler.
    /// GET /api/panel/shopper-code → returns <paramref name="getResp"/> as JSON 200.
    /// PUT /api/panel/shopper-code → throws <paramref name="saveThrows"/>, returns
    ///   <paramref name="saveReturns"/> as JSON 200, or returns 400 Problem if saveThrows
    ///   is a <see cref="ShopperCodeValidationException"/>.
    /// </summary>
    private static LicenseApiClient BuildClient(
        ShopperCodeResponse? getResp = null,
        Exception? saveThrows = null,
        ShopperCodeResponse? saveReturns = null)
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                if (getResp is null)
                    return FakeHttpMessageHandler.Empty(404);

                return FakeHttpMessageHandler.Json(200, SerializeResponse(getResp));
            }

            if (req.Method == HttpMethod.Put)
            {
                if (saveThrows is ShopperCodeValidationException vex)
                    return FakeHttpMessageHandler.Problem(400, vex.ErrorCode);

                if (saveThrows is not null)
                    return FakeHttpMessageHandler.Empty(500);

                if (saveReturns is not null)
                    return FakeHttpMessageHandler.Json(200, SerializeResponse(saveReturns));

                // Default: return empty 500 (should not be reached in normal tests)
                return FakeHttpMessageHandler.Empty(500);
            }

            return FakeHttpMessageHandler.Empty(404);
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        return new LicenseApiClient(http, new LicenseTokenStore());
    }

    private static ShopperAppSettingsViewModel CreateVm(LicenseApiClient api)
        => new(api, NullLogger<ShopperAppSettingsViewModel>.Instance);

    // ─── Load tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Load_populates_code_and_cooldown_state()
    {
        var resp = new ShopperCodeResponse(
            "royal",
            DateTimeOffset.UtcNow.AddDays(-3),
            DateTimeOffset.UtcNow.AddDays(4),
            TestLicenseId);
        var api = BuildClient(getResp: resp);
        var vm = CreateVm(api);

        await vm.LoadAsync(default);

        vm.CodeInput.Should().Be("royal");
        vm.CanEditCode.Should().BeFalse(); // cooldown active
        vm.CooldownMessage.Should().NotBeNullOrEmpty();
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Load_first_time_no_cooldown()
    {
        var resp = new ShopperCodeResponse(null, null, null, TestLicenseId);
        var api = BuildClient(getResp: resp);
        var vm = CreateVm(api);

        await vm.LoadAsync(default);

        vm.CodeInput.Should().BeEmpty();
        vm.CanEditCode.Should().BeTrue();
        vm.CooldownMessage.Should().BeNull();
    }

    [Fact]
    public async Task Load_sets_error_when_server_unreachable()
    {
        // getResp = null → handler returns 404, which GetExpectingJsonAsync will throw on
        var api = BuildClient(getResp: null);
        var vm = CreateVm(api);

        await vm.LoadAsync(default);

        // 404 maps to either the 404 branch or the generic catch
        // (depends on how LicenseApiClient raises it); either way ErrorMessage is set.
        vm.ErrorMessage.Should().NotBeNullOrEmpty();
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task Load_cooldown_expired_sets_canEditCode_true()
    {
        var resp = new ShopperCodeResponse(
            "mezat42",
            DateTimeOffset.UtcNow.AddDays(-8),
            DateTimeOffset.UtcNow.AddDays(-1), // already past
            TestLicenseId);
        var api = BuildClient(getResp: resp);
        var vm = CreateVm(api);

        await vm.LoadAsync(default);

        vm.CanEditCode.Should().BeTrue();
        vm.CooldownMessage.Should().BeNull();
    }

    // ─── Save validation error mapping ────────────────────────────────────────

    [Theory]
    [InlineData("empty",    "Kod boş olamaz.")]
    [InlineData("length",   "Kod 3-20 karakter olmalı.")]
    [InlineData("format",   "Sadece küçük harf ve rakam (a-z, 0-9).")]
    [InlineData("reserved", "Bu kelime sistem tarafından ayrılmış.")]
    [InlineData("profanity","Bu kelime uygun değil.")]
    [InlineData("cooldown", "Henüz 7 günlük bekleme süresi dolmadı.")]
    [InlineData("taken",    "Bu kod başka bir yayıncı tarafından kullanılıyor.")]
    [InlineData("unknown-thing", "Bilinmeyen hata: unknown-thing")]
    public async Task Save_maps_each_validation_errorCode_to_turkish_message(string code, string expected)
    {
        var api = BuildClient(saveThrows: new ShopperCodeValidationException(code));
        var vm = CreateVm(api) ;
        vm.CodeInput = "test";

        await vm.SaveCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().Be(expected);
    }

    // ─── Save success ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_success_updates_state_and_shows_kaydedildi()
    {
        var afterSave = new ShopperCodeResponse(
            "newcode",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7),
            TestLicenseId);
        var api = BuildClient(saveReturns: afterSave);
        var vm = CreateVm(api);
        vm.CodeInput = "newcode";

        await vm.SaveCommand.ExecuteAsync(null);

        vm.StatusMessage.Should().Be("Kaydedildi.");
        vm.ErrorMessage.Should().BeNull();
        vm.CanEditCode.Should().BeFalse(); // cooldown just started
        vm.CooldownMessage.Should().NotBeNullOrEmpty();
    }

    // ─── Local empty-code guard ───────────────────────────────────────────────

    [Fact]
    public async Task Save_empty_code_shows_validation_locally_without_calling_api()
    {
        // Build with no GET/save responses configured → any HTTP call would 404/500
        var api = BuildClient(); // no responses needed — should not be hit
        var vm = CreateVm(api);
        vm.CodeInput = "   ";

        await vm.SaveCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().Be("Kod boş olamaz.");
        vm.IsLoading.Should().BeFalse();
    }

    // ─── IsLoading invariant ──────────────────────────────────────────────────

    [Fact]
    public async Task Load_resets_isLoading_to_false_after_success()
    {
        var resp = new ShopperCodeResponse(null, null, null, TestLicenseId);
        var api = BuildClient(getResp: resp);
        var vm = CreateVm(api);

        await vm.LoadAsync(default);

        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task Save_resets_isLoading_to_false_after_validation_error()
    {
        var api = BuildClient(saveThrows: new ShopperCodeValidationException("format"));
        var vm = CreateVm(api);
        vm.CodeInput = "bad code!!";

        await vm.SaveCommand.ExecuteAsync(null);

        vm.IsLoading.Should().BeFalse();
    }
}
