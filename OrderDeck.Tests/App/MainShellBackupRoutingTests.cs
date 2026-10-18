using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Tests.Sync;
using Xunit;
using static OrderDeck.Tests.Sync.SalesEditTestBed;

namespace OrderDeck.Tests.App;

/// <summary>
/// Yedek seçimi düzenleme yönlendiricisinden geçer (spec 2026-10-13 §4.6 <c>add-backup</c>, §5.3 "Yedek seçimi", §12;
/// W2-06). Mod Off'ta bugünkü <c>LabelService.AddBackup</c>: tıklamanın içinde biter, istek gitmez. Mod On'da ebeveynin
/// yayını devredilmişse (kalıntı kuyruk, §5.5) tek <c>add-backup</c> komutu: tıklama hemen tüketilir, yanıtın satırı
/// kuyruğa girer ve ebeveynin sayacı artar. Sonucu bilinmeyen komutun "Tekrar dene"si aynı <c>CommandId</c> ve
/// <c>newOrderId</c>'yi gönderir (sunucu bir kez ekler); <c>content-conflict</c> satır içi şerit ve tek çakışma kaydı.
/// Pop-up yok.
/// </summary>
public sealed class MainShellBackupRoutingTests
{
    private sealed class Shell : IDisposable
    {
        public required MainShellTestHarness.Harness H { get; init; }
        public required SalesEditTestBed Bed { get; init; }

        public void Dispose()
        {
            H.Dispose();
            Bed.Dispose();
        }
    }

    private static Shell Build()
    {
        SalesEditTestBed? bed = null;
        var h = MainShellTestHarness.Build(labelEditor: (db, labels) => (bed = new SalesEditTestBed(db, labels)).Router);
        return new Shell { H = h, Bed = bed! };
    }

    /// <summary>Devredilmiş yayının basılmamış etiketi kuyrukta (§5.5 kalıntı kuyruk): sunucuda ve yerelde, yayını
    /// <c>server</c>.</summary>
    private static async Task<(Guid Session, Guid Parent, LabelViewModel Row)> ReleasedParentAsync(Shell s)
    {
        var (session, _, order) = await s.Bed.RemoteLabelAsync(150m, printed: false);
        var row = new LabelViewModel(s.Bed.LabelRepo.GetById(N(order))!, isCustomerBlacklisted: false);
        s.H.Vm.PrintQueue.Add(row);
        return (session, order, row);
    }

    private static bool Assign(Shell s, LabelViewModel parent, string username = "yedek_alici", string text = "ben de aldım")
    {
        s.H.Vm.BeginAddBackupCommand.Execute(parent);
        return s.H.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm(username, text));
    }

    [Fact]
    public async Task Mod_Onda_devredilmis_ebeveyne_tek_add_backup_gider_ve_yeni_yedek_kuyruga_girer()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var (session, parent, row) = await ReleasedParentAsync(s);

        var consumed = Assign(s, row);

        consumed.Should().BeTrue("tıklama hemen tüketilir");
        s.H.Vm.IsInBackupSelectionMode.Should().BeFalse("yedek modu komutu beklemeden kapanır");
        await s.H.Vm.BackupAssignment;

        var call = s.Bed.Server.CommandCalls.Should().ContainSingle().Subject.Command;
        call.Kind.Should().Be("add-backup");
        call.Expected.Should().BeNullOrEmpty("komut var olan satırı yazmaz");
        var payload = call.Payload!.Value;
        var newId = payload.GetProperty("newOrderId").GetGuid();
        payload.GetProperty("parentOrderId").GetGuid().Should().Be(parent);
        var customer = payload.GetProperty("customer");
        var local = s.H.CustomerRepo.FindByPlatformAndUsername("instagram", "yedek_alici");
        local.Should().NotBeNull("müşteri yerelde AddBackup'ın kuralıyla açıldı");
        customer.GetProperty("id").GetString().Should().Be(local!.Id);
        customer.GetProperty("platform").GetString().Should().Be("instagram");
        customer.GetProperty("username").GetString().Should().Be("yedek_alici");
        payload.GetProperty("messageText").GetString().Should().Be("ben de aldım");
        Absent(payload, "price").Should().BeTrue("fiyat ve kod sunucudaki ebeveynin güncel değeri (yerel ayna bayat olabilir)");
        Absent(payload, "code").Should().BeTrue();

        var backup = s.H.Vm.PrintQueue.Should().ContainSingle(r => r.Label.IsTentativeBackup).Which;
        backup.Label.Price.Should().Be(150m, "ebeveynin fiyatı");
        backup.Label.Code.Should().Be("A1", "ebeveynin kodu");
        backup.Id.Should().Be(N(newId));
        backup.Label.ParentLabelId.Should().Be(N(parent));
        backup.Label.SessionId.Should().Be(N(session));
        backup.Label.ProductId.Should().BeNull("add-backup ürün kopyalamaz (§12)");
        row.BackupCount.Should().Be(1);
        s.Bed.Local(newId).SyncedAt.Should().NotBeNull("yanıtın satırı onaylı yazıldı: gönderim kuyruğuna girmez");
        s.Bed.Local(newId).ServerVersion.Should().Be(1);
        s.Bed.Server.Order(newId)!.OriginReplicaId.Should().Be(s.Bed.Envelope.ReplicaId);
        s.H.Vm.BackupProblem.Should().BeNull();
        s.H.Dialogs.Shown.Should().BeEmpty("pop-up yok");
    }

    private static bool Absent(System.Text.Json.JsonElement payload, string name) =>
        !payload.TryGetProperty(name, out var value) || value.ValueKind == System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task Akis_gecikmisken_yedek_ebeveynin_sunucudaki_guncel_fiyatini_alir()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var (_, parent, row) = await ReleasedParentAsync(s);
        // Başka bilgisayar kodun fiyatını düzeltti (set-code-price); bu bilgisayarın akışı henüz inmedi.
        s.Bed.Server.MutateOrder(parent, o => o with { Price = 120m });
        s.Bed.Local(parent).Price.Should().Be(150m, "ön koşul: yerel ayna bayat");

        Assign(s, row);
        await s.H.Vm.BackupAssignment;

        var backup = s.H.Vm.PrintQueue.Should().ContainSingle(r => r.Label.IsTentativeBackup).Which;
        backup.Label.Price.Should().Be(120m, "Y kâğıdı ve ödeme isteği güncel fiyatı sayar; S×K'de iki fiyat doğmaz");
        s.Bed.Server.Order(Guid.ParseExact(backup.Id, "N"))!.Row.Price.Should().Be(120m);
    }

    [Fact]
    public async Task Zaman_asiminda_tekrar_dene_ayni_CommandId_ve_newOrderId_ile_gider_sunucu_bir_kez_ekler()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var (_, _, row) = await ReleasedParentAsync(s);
        s.Bed.Server.LoseNextResponse(FakeSalesServer.Commands);

        Assign(s, row);
        await s.H.Vm.BackupAssignment;

        s.H.Vm.BackupProblem.Should().Be("Yedek eklenemedi: Bu değişiklik için sunucu bağlantısı gerekli.");
        s.H.Vm.CanRetryBackup.Should().BeTrue();
        s.H.Vm.IsInBackupSelectionMode.Should().BeFalse("başarısızlıkta yedek modu kapanır");
        s.H.Vm.PrintQueue.Should().NotContain(r => r.Label.IsTentativeBackup);
        row.BackupCount.Should().Be(0);

        await s.H.Vm.RetryBackupCommand.ExecuteAsync(null);

        var calls = s.Bed.Server.CommandCalls.Select(c => c.Command).ToList();
        calls.Should().HaveCount(2);
        calls[1].CommandId.Should().Be(calls[0].CommandId);
        var newId = calls[0].Payload!.Value.GetProperty("newOrderId").GetGuid();
        calls[1].Payload!.Value.GetProperty("newOrderId").GetGuid().Should().Be(newId);
        var log = s.Bed.Server.CommandLog(calls[0].CommandId!.Value)!;
        log.Applications.Should().Be(1, "sunucu yedeği bir kez ekler");
        log.Replays.Should().Be(1);
        s.H.Vm.PrintQueue.Should().ContainSingle(r => r.Label.IsTentativeBackup).Which.Id.Should().Be(N(newId));
        row.BackupCount.Should().Be(1);
        s.H.Vm.BackupProblem.Should().BeNull();
        s.H.Vm.CanRetryBackup.Should().BeFalse();
    }

    [Fact]
    public async Task Zaman_asiminda_ayni_kisiyi_yeniden_secmek_de_ayni_niyeti_gonderir()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var (_, _, row) = await ReleasedParentAsync(s);
        s.Bed.Server.LoseNextResponse(FakeSalesServer.Commands);
        Assign(s, row);
        await s.H.Vm.BackupAssignment;

        Assign(s, row);
        await s.H.Vm.BackupAssignment;

        var calls = s.Bed.Server.CommandCalls.Select(c => c.Command).ToList();
        calls.Should().HaveCount(2);
        calls[1].CommandId.Should().Be(calls[0].CommandId, "aynı kişi aynı ebeveyne: aynı niyet, yinelenen yedek yok");
        s.H.Vm.PrintQueue.Should().ContainSingle(r => r.Label.IsTentativeBackup);
        row.BackupCount.Should().Be(1);
    }

    [Fact]
    public async Task Content_conflict_satir_ici_serit_ve_tek_catisma_kaydi_yeniden_gonderilmez()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var (_, _, row) = await ReleasedParentAsync(s);
        s.Bed.Server.EnqueueProblem(FakeSalesServer.Commands, 409, "content-conflict");

        Assign(s, row);
        await s.H.Vm.BackupAssignment;

        s.H.Vm.BackupProblem.Should().Be("Yedek eklenemedi: İşlem kaydedilemedi (iç hata); tekrar dene.");
        s.H.Vm.CanRetryBackup.Should().BeFalse("istemci hatası aynı niyetle yeniden gönderilmez");
        s.Bed.Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog").Should().Be(1);
        s.H.Vm.IsInBackupSelectionMode.Should().BeFalse();
        s.H.Vm.PrintQueue.Should().NotContain(r => r.Label.IsTentativeBackup);
        row.BackupCount.Should().Be(0);
        s.H.Dialogs.Shown.Should().BeEmpty("pop-up yok");
    }

    [Fact]
    public async Task Mod_Onda_cevrimdisiyken_devredilmis_ebeveynin_yedegi_eklenmez_istek_gitmez()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var (_, _, row) = await ReleasedParentAsync(s);
        var http = s.Bed.HttpCount;
        s.Bed.Online = false;

        Assign(s, row);
        await s.H.Vm.BackupAssignment;

        s.H.Vm.BackupProblem.Should().Be("Yedek eklenemedi: Bu değişiklik için sunucu bağlantısı gerekli.");
        s.H.Vm.CanRetryBackup.Should().BeFalse("hiç istek gitmedi; operatör yeniden seçer");
        s.Bed.HttpCount.Should().Be(http);
        s.H.Vm.PrintQueue.Should().NotContain(r => r.Label.IsTentativeBackup);
    }

    [Fact]
    public void Mod_Offta_bugunku_yerel_yedek_tiklamanin_icinde_biter_ve_istek_gitmez()
    {
        using var s = Build();
        MainShellTestHarness.EnqueueLabel(s.H.Vm, "@buyer", 199m);
        var parent = s.H.Vm.PrintQueue[0];

        var consumed = Assign(s, parent, "@yedek", "ben de");

        consumed.Should().BeTrue();
        s.H.Vm.BackupAssignment.IsCompleted.Should().BeTrue("mod Off'ta beklemeden biter");
        var backup = s.H.Vm.PrintQueue.Should().ContainSingle(r => r.Label.IsTentativeBackup).Which;
        backup.Label.ParentLabelId.Should().Be(parent.Id);
        parent.BackupCount.Should().Be(1);
        s.Bed.Local(backup.Id).SyncedAt.Should().BeNull("bugünkü gibi gönderim kuyruğuna girer");
        s.Bed.HttpCount.Should().Be(0);
        s.H.Vm.BackupProblem.Should().BeNull();
    }
}
