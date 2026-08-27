using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelWhatsAppConversationsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelWhatsAppConversationsControllerTests(ApiFactory f) => _factory = f;

    private sealed record LabelDto(Guid Id, string Name, string Color, DateTimeOffset CreatedAt);
    private sealed record DekontDto(
        string? PayerName, decimal? Amount, DateTimeOffset? PaidAt,
        string? ReferansNo, string ParserConfidence);
    private sealed record ConversationLabelDto(Guid WaLabelId, string Name, string Color, string Source);
    private sealed record ConversationDto(
        Guid Id, string CustomerPhone, string? ProfileName, string Status,
        int UnreadCount, DateTimeOffset? LastMessageAt, DateTimeOffset? WindowExpiresAt,
        List<ConversationLabelDto> Labels, DekontDto? LatestDekont);

    private sealed record SendResponse(
        bool Ok, string? ErrorCode, string? ErrorMessage, Guid? MessageId);

    private sealed record MessageDto(
        Guid Id, string Direction, string Type, string? Body, string Status,
        string? Origin, string? TemplateName, string? MediaMimeType,
        string? ErrorCode, string? ErrorMessage, DateTimeOffset Timestamp);

    private sealed record Seed(HttpClient Client, Guid LicenseId, Guid LabelId, Guid ConversationId);

    private async Task<Seed> SeedAsync(string phone = "905321234567")
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var licenseId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Licenses.Add(new License
            {
                Id = licenseId, CustomerId = customerId,
                LicenseKey = "LDK-WACNV-" + Guid.NewGuid().ToString("N"),
                SkuCode = "STD", ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
            db.WaConversations.Add(new WaConversation
            {
                Id = conversationId, LicenseId = licenseId,
                CustomerPhone = phone, PhoneNumberId = "PNID_1",
                ProfileName = "Ayşe", Status = "open", UnreadCount = 2,
                LastMessageAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var created = await client.PostAsJsonAsync(
            "/api/panel/whatsapp-labels", new { name = "Dekont geldi", color = "#eab308" });
        var label = (await created.Content.ReadFromJsonAsync<LabelDto>())!;

        return new Seed(client, licenseId, label.Id, conversationId);
    }

    [Fact]
    public async Task Lists_conversations_with_no_labels_initially()
    {
        var s = await SeedAsync();

        var list = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");

        var row = list!.Single(c => c.Id == s.ConversationId);
        row.CustomerPhone.Should().Be("905321234567");
        row.ProfileName.Should().Be("Ayşe");
        row.UnreadCount.Should().Be(2);
        row.Labels.Should().BeEmpty();
        row.LatestDekont.Should().BeNull();
    }

    [Fact]
    public async Task Attaching_a_label_by_hand_marks_it_manual()
    {
        var s = await SeedAsync();

        var resp = await s.Client.PostAsync(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/labels/{s.LabelId}", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");

        var label = list!.Single(c => c.Id == s.ConversationId).Labels.Single();
        label.WaLabelId.Should().Be(s.LabelId);
        label.Name.Should().Be("Dekont geldi");
        label.Color.Should().Be("#eab308");
        label.Source.Should().Be("manual");
    }

    [Fact]
    public async Task Attaching_the_same_label_twice_is_harmless()
    {
        var s = await SeedAsync();
        var url = $"/api/panel/whatsapp-conversations/{s.ConversationId}/labels/{s.LabelId}";

        (await s.Client.PostAsync(url, null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await s.Client.PostAsync(url, null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");
        list!.Single(c => c.Id == s.ConversationId).Labels.Should().ContainSingle();
    }

    [Fact]
    public async Task Removing_a_label_works_even_when_the_server_attached_it()
    {
        var s = await SeedAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WaConversationLabels.Add(new WaConversationLabel
            {
                Id = Guid.NewGuid(), LicenseId = s.LicenseId,
                ConversationId = s.ConversationId, WaLabelId = s.LabelId,
                Source = "auto", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await s.Client.DeleteAsync(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/labels/{s.LabelId}");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");
        list!.Single(c => c.Id == s.ConversationId).Labels.Should().BeEmpty();
    }

    [Fact]
    public async Task Filtering_by_label_returns_only_matching_conversations()
    {
        var s = await SeedAsync();

        Guid otherConversationId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            otherConversationId = Guid.NewGuid();
            db.WaConversations.Add(new WaConversation
            {
                Id = otherConversationId, LicenseId = s.LicenseId,
                CustomerPhone = "905339998877", PhoneNumberId = "PNID_1",
                Status = "open", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // İkinci etiket şart: tek etiket olsaydı "bu etiket" ile "herhangi bir
        // etiket" ayırt edilemez, filtre koşulu silinse bile test yeşil kalırdı.
        var second = await s.Client.PostAsJsonAsync(
            "/api/panel/whatsapp-labels", new { name = "İnsan baksın", color = "#ef4444" });
        var otherLabel = (await second.Content.ReadFromJsonAsync<LabelDto>())!;

        await s.Client.PostAsync(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/labels/{s.LabelId}", null);
        await s.Client.PostAsync(
            $"/api/panel/whatsapp-conversations/{otherConversationId}/labels/{otherLabel.Id}", null);

        var filtered = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            $"/api/panel/whatsapp-conversations?labelId={s.LabelId}");

        filtered!.Should().ContainSingle().Which.Id.Should().Be(s.ConversationId);
        filtered.Should().NotContain(c => c.Id == otherConversationId);
    }

    [Fact]
    public async Task The_latest_parsed_dekont_is_surfaced_next_to_the_labels()
    {
        var s = await SeedAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

            // İki dekont: eski ve yeni. Panelde güncel olan işe yarar.
            //
            // Satırların YAZILMA sırası mesaj sırasının bilerek tersi: gecikmeli
            // bir webhook eski mesajı sonradan yazar. Yani eski mesajın CreatedAt'i
            // yeni mesajınkinden büyük. Böylece doğru cevabı yalnızca mesajın
            // Timestamp'ine göre sıralamak verir, satır yazma anına göre değil.
            void SeedDekont(
                string wamId, string payer, decimal amount,
                int timestampMinutesAgo, int createdAtMinutesAgo)
            {
                var messageId = Guid.NewGuid();
                db.WaMessages.Add(new WaMessage
                {
                    Id = messageId, ConversationId = s.ConversationId, LicenseId = s.LicenseId,
                    WamId = wamId, Direction = "in", Type = "document",
                    Status = "received",
                    Timestamp = DateTimeOffset.UtcNow.AddMinutes(-timestampMinutesAgo),
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-createdAtMinutesAgo),
                });
                db.WaDekontExtractions.Add(new WaDekontExtraction
                {
                    WaMessageId = messageId, LicenseId = s.LicenseId,
                    PayerName = payer, Amount = amount,
                    PaidAt = DateTimeOffset.UtcNow.AddMinutes(-timestampMinutesAgo),
                    ReferansNo = "REF" + wamId, PdfHash = "h" + wamId,
                    ParserConfidence = "High",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-createdAtMinutesAgo),
                });
            }

            SeedDekont("wamid.old", "ESKİ GÖNDEREN", 100m, timestampMinutesAgo: 60, createdAtMinutesAgo: 1);
            SeedDekont("wamid.new", "AYŞE YILMAZ", 1250.50m, timestampMinutesAgo: 1, createdAtMinutesAgo: 60);

            await db.SaveChangesAsync();
        }

        var list = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");

        var dekont = list!.Single(c => c.Id == s.ConversationId).LatestDekont;
        dekont.Should().NotBeNull();
        dekont!.PayerName.Should().Be("AYŞE YILMAZ");
        dekont.Amount.Should().Be(1250.50m);
        dekont.ReferansNo.Should().Be("REFwamid.new");
        dekont.ParserConfidence.Should().Be("High");
    }

    [Fact]
    public async Task Another_broadcasters_conversation_is_not_reachable()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync("905441112233");

        var list = await mine.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");
        list!.Should().NotContain(c => c.Id == theirs.ConversationId);

        var resp = await mine.Client.PostAsync(
            $"/api/panel/whatsapp-conversations/{theirs.ConversationId}/labels/{mine.LabelId}", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Another_broadcasters_label_cannot_be_detached()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync("905441112233");

        await mine.Client.PostAsync(
            $"/api/panel/whatsapp-conversations/{mine.ConversationId}/labels/{mine.LabelId}", null);

        await theirs.Client.DeleteAsync(
            $"/api/panel/whatsapp-conversations/{mine.ConversationId}/labels/{mine.LabelId}");

        // Durum kodu iki türlü de NoContent (kaldırma idempotent), o yüzden asıl
        // kanıt etiketin ayakta kalması: koruma olmasa sessizce silinmiş olurdu.
        var list = await mine.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");
        list!.Single(c => c.Id == mine.ConversationId)
            .Labels.Should().ContainSingle().Which.WaLabelId.Should().Be(mine.LabelId);
    }

    private static void SeedMessage(
        LicenseDbContext db, Seed s, string wamId, string direction, string? body,
        int timestampMinutesAgo, int createdAtMinutesAgo)
    {
        db.WaMessages.Add(new WaMessage
        {
            Id = Guid.NewGuid(), ConversationId = s.ConversationId, LicenseId = s.LicenseId,
            WamId = wamId, Direction = direction, Type = "text", Body = body,
            Status = direction == "in" ? "received" : "delivered",
            Origin = direction == "out" ? "panel" : null,
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-timestampMinutesAgo),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-createdAtMinutesAgo),
        });
    }

    [Fact]
    public async Task Messages_come_back_oldest_first_by_the_time_meta_stamped_them()
    {
        var s = await SeedAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

            // Satır YAZILMA sırası mesaj sırasının bilerek tersi: gecikmeli webhook
            // eski mesajı sonradan yazar. Doğru cevabı yalnız Timestamp sıralaması
            // verir; CreatedAt'e göre sıralansaydı sohbet ters görünürdü.
            SeedMessage(db, s, "wamid.a", "in", "Merhaba", timestampMinutesAgo: 30, createdAtMinutesAgo: 1);
            SeedMessage(db, s, "wamid.b", "out", "Buyurun", timestampMinutesAgo: 20, createdAtMinutesAgo: 30);
            SeedMessage(db, s, "wamid.c", "in", "1250 TL gönderdim", timestampMinutesAgo: 10, createdAtMinutesAgo: 20);
            await db.SaveChangesAsync();
        }

        var messages = await s.Client.GetFromJsonAsync<List<MessageDto>>(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/messages");

        messages.Should().NotBeNull();
        messages!.Select(m => m.Body).Should()
            .ContainInOrder("Merhaba", "Buyurun", "1250 TL gönderdim");
        messages!.Select(m => m.Direction).Should().ContainInOrder("in", "out", "in");
        messages!.Single(m => m.Body == "Buyurun").Origin.Should().Be("panel");
    }

    // Uzun bir sohbette istenen "en yeni N", "en eski N" değil: yayıncı ekranı
    // açtığında son yazışmayı görmeli, aylar önceki ilk mesajı değil.
    [Fact]
    public async Task A_limit_keeps_the_newest_messages_not_the_oldest()
    {
        var s = await SeedAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            for (var i = 0; i < 5; i++)
                SeedMessage(db, s, $"wamid.n{i}", "in", $"mesaj {i}",
                    timestampMinutesAgo: 100 - i, createdAtMinutesAgo: 100 - i);
            await db.SaveChangesAsync();
        }

        var messages = await s.Client.GetFromJsonAsync<List<MessageDto>>(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/messages?limit=2");

        messages!.Select(m => m.Body).Should().Equal("mesaj 3", "mesaj 4");
    }

    [Fact]
    public async Task Marking_a_conversation_read_clears_the_badge()
    {
        var s = await SeedAsync();   // UnreadCount = 2 ile geliyor

        var resp = await s.Client.PostAsync(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/read", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");
        list!.Single(c => c.Id == s.ConversationId).UnreadCount.Should().Be(0);
    }

    // Panel bu GET'i yeniden odaklanmada ve arka planda tazeliyor. Okuma yan
    // etkili olsaydı rozet, yayıncı sohbete hiç bakmadan sessizce sıfırlanırdı.
    [Fact]
    public async Task Reading_the_messages_does_not_silently_clear_the_badge()
    {
        var s = await SeedAsync();

        await s.Client.GetFromJsonAsync<List<MessageDto>>(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/messages");

        var list = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");
        list!.Single(c => c.Id == s.ConversationId).UnreadCount.Should().Be(2);
    }

    [Fact]
    public async Task Another_broadcasters_messages_cannot_be_read()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync("905441112233");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            SeedMessage(db, theirs, "wamid.secret", "in", "gizli", 5, 5);
            await db.SaveChangesAsync();
        }

        var resp = await mine.Client.GetAsync(
            $"/api/panel/whatsapp-conversations/{theirs.ConversationId}/messages");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var read = await mine.Client.PostAsync(
            $"/api/panel/whatsapp-conversations/{theirs.ConversationId}/read", null);
        read.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Rozet ayakta kalmalı: koruma olmasa başkasının sohbeti okundu sayılırdı.
        var list = await theirs.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");
        list!.Single(c => c.Id == theirs.ConversationId).UnreadCount.Should().Be(2);
    }

    [Fact]
    public async Task A_label_from_another_broadcaster_cannot_be_attached()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync("905441112233");

        var resp = await mine.Client.PostAsync(
            $"/api/panel/whatsapp-conversations/{mine.ConversationId}/labels/{theirs.LabelId}", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Gönderim için bağlı bir hesap ŞART; ayrıca 24s penceresi
    /// <c>LastInboundAt</c>'ten hesaplandığı için onu da buradan kurguluyoruz.</summary>
    private async Task ConnectWhatsAppAsync(Seed s, DateTimeOffset lastInboundAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<WhatsAppAccountService>();

        db.WhatsAppAccounts.Add(new WhatsAppAccount
        {
            Id = Guid.NewGuid(), LicenseId = s.LicenseId,
            WabaId = "WABA_" + Guid.NewGuid().ToString("N")[..8],
            PhoneNumberId = "PNID_1", DisplayPhoneNumber = "+905550000000",
            AccessTokenProtected = accounts.ProtectToken("token"),
            Status = "active", ConnectedAt = DateTimeOffset.UtcNow,
        });

        var convo = await db.WaConversations.SingleAsync(c => c.Id == s.ConversationId);
        convo.LastInboundAt = lastInboundAt;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_reply_goes_out_and_shows_up_in_the_thread()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s, DateTimeOffset.UtcNow.AddHours(-1));

        var resp = await s.Client.PostAsJsonAsync(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/send",
            new { text = "  Kargonuz yola çıktı  " });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await resp.Content.ReadFromJsonAsync<SendResponse>())!;
        body.Ok.Should().BeTrue();

        var messages = await s.Client.GetFromJsonAsync<List<MessageDto>>(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/messages");

        var sent = messages!.Single(m => m.Id == body.MessageId);
        sent.Direction.Should().Be("out");
        sent.Origin.Should().Be("panel");
        sent.Status.Should().Be("sent");
        // Kenar boşlukları kırpılmalı: kutuya yapıştırılan metnin sonundaki
        // satır sonu, gönderilen mesajda görünmemeli.
        sent.Body.Should().Be("Kargonuz yola çıktı");
    }

    // Pencere kapalıyken Meta 131047 ile reddediyor. Uç bunu ÖNDEN kesip sebebi
    // gövdede söylüyor; 4xx dönseydi panel bunu genel ağ hatasına indirger,
    // yayıncı "neden gitmedi" sorusunun cevabını hiç göremezdi.
    [Fact]
    public async Task A_reply_is_refused_when_the_24h_window_is_closed()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s, DateTimeOffset.UtcNow.AddHours(-25));

        var resp = await s.Client.PostAsJsonAsync(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/send",
            new { text = "merhaba" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await resp.Content.ReadFromJsonAsync<SendResponse>())!;
        body.Ok.Should().BeFalse();
        body.ErrorCode.Should().Be("window_closed");
        body.MessageId.Should().BeNull();

        var messages = await s.Client.GetFromJsonAsync<List<MessageDto>>(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/messages");
        messages!.Should().NotContain(m => m.Direction == "out");
    }

    [Fact]
    public async Task Another_broadcasters_conversation_cannot_be_replied_to()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync("905441112233");
        await ConnectWhatsAppAsync(theirs, DateTimeOffset.UtcNow.AddHours(-1));

        var resp = await mine.Client.PostAsJsonAsync(
            $"/api/panel/whatsapp-conversations/{theirs.ConversationId}/send",
            new { text = "başkasının müşterisi" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var messages = await theirs.Client.GetFromJsonAsync<List<MessageDto>>(
            $"/api/panel/whatsapp-conversations/{theirs.ConversationId}/messages");
        messages!.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_reply_never_reaches_meta()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s, DateTimeOffset.UtcNow.AddHours(-1));

        var resp = await s.Client.PostAsJsonAsync(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/send",
            new { text = "   \n  " });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // Meta'nın gövde sınırı 4096; aşanı Graph reddediyor. Göndermeden kesmek
    // hem boşuna çağrıyı hem "failed" satırını önlüyor.
    [Fact]
    public async Task A_reply_longer_than_metas_limit_is_cut_before_the_call()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s, DateTimeOffset.UtcNow.AddHours(-1));

        var resp = await s.Client.PostAsJsonAsync(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/send",
            new { text = new string('a', 4097) });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var messages = await s.Client.GetFromJsonAsync<List<MessageDto>>(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/messages");
        messages!.Should().BeEmpty();
    }

    // Panel yazma kutusunu bu değerle kilitliyor. Boolean dönseydi pencere
    // önbellekteki cevap bayatlarken kapanır, kutu açık kalırdı.
    [Fact]
    public async Task The_window_deadline_is_exposed_so_the_panel_can_lock_the_box()
    {
        var s = await SeedAsync();

        var before = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");
        // Müşteri hiç yazmadıysa pencere hiç açılmamıştır — "şu an kapalı" ile
        // aynı şey değil, panel bunu ayrı anlatıyor.
        before!.Single(c => c.Id == s.ConversationId).WindowExpiresAt.Should().BeNull();

        var lastInbound = DateTimeOffset.UtcNow.AddHours(-3);
        await ConnectWhatsAppAsync(s, lastInbound);

        var after = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");
        after!.Single(c => c.Id == s.ConversationId).WindowExpiresAt
            .Should().BeCloseTo(lastInbound.AddHours(24), TimeSpan.FromSeconds(1));
    }

    private Task<HttpResponseMessage> SendTemplateAsync(
        Seed s, object payload, Guid? conversationId = null) =>
        s.Client.PostAsJsonAsync(
            $"/api/panel/whatsapp-conversations/{conversationId ?? s.ConversationId}/send-template",
            payload);

    // Şablonun varlık sebebi bu: pencere kapalıyken gönderilebilen tek mesaj
    // türü. Serbest metin burada window_closed alırdı.
    [Fact]
    public async Task A_template_goes_out_even_though_the_window_is_closed()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s, DateTimeOffset.UtcNow.AddHours(-25));

        var resp = await SendTemplateAsync(s, new
        {
            name = "odeme_hatirlatma",
            language = "tr",
            @params = new[] { "Ayşe", "250" },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await resp.Content.ReadFromJsonAsync<SendResponse>())!;
        body.Ok.Should().BeTrue();

        var messages = await s.Client.GetFromJsonAsync<List<MessageDto>>(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/messages");

        var sent = messages!.Single(m => m.Id == body.MessageId);
        sent.Direction.Should().Be("out");
        sent.Type.Should().Be("template");
        sent.Origin.Should().Be("panel");
        sent.TemplateName.Should().Be("odeme_hatirlatma");
        // Onaylı gövde Meta'da duruyor; sohbette "hangi değerlerle gitti"
        // sorusunu yalnız parametreler cevaplayabiliyor.
        sent.Body.Should().Be("Ayşe | 250");
    }

    [Fact]
    public async Task A_template_without_parameters_can_be_sent()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s, DateTimeOffset.UtcNow.AddHours(-25));

        var resp = await SendTemplateAsync(s, new { name = "hello_world", language = "en_US" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<SendResponse>())!.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Another_broadcasters_conversation_cannot_be_sent_a_template()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync("905441112233");
        await ConnectWhatsAppAsync(theirs, DateTimeOffset.UtcNow.AddHours(-25));

        var resp = await SendTemplateAsync(
            mine, new { name = "hello_world", language = "en_US" }, theirs.ConversationId);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var messages = await theirs.Client.GetFromJsonAsync<List<MessageDto>>(
            $"/api/panel/whatsapp-conversations/{theirs.ConversationId}/messages");
        messages!.Should().BeEmpty();
    }

    // Meta parametre içinde satır sonu/sekme/4+ boşluk kabul etmiyor ve hatası
    // (132000) kriptik. Şablon ÜCRETLİ olduğu için yanlış çağrıyı hiç yapmamak
    // yayıncının parasını koruyor.
    [Theory]
    [InlineData("iki\nsatır")]
    [InlineData("sekme\tli")]
    [InlineData("çok    boşluk")]
    public async Task A_parameter_meta_would_reject_never_reaches_it(string value)
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s, DateTimeOffset.UtcNow.AddHours(-25));

        var resp = await SendTemplateAsync(s, new
        {
            name = "odeme_hatirlatma",
            language = "tr",
            @params = new[] { value },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var messages = await s.Client.GetFromJsonAsync<List<MessageDto>>(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/messages");
        messages!.Should().BeEmpty();
    }

    // Boş parametre Meta'da yer tutucuyu boş bırakmıyor, mesajı komple
    // reddettiriyor — üstelik yayıncı alanı doldurmayı unuttuğunu anlamıyor.
    [Fact]
    public async Task An_empty_parameter_never_reaches_meta()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s, DateTimeOffset.UtcNow.AddHours(-25));

        var resp = await SendTemplateAsync(s, new
        {
            name = "odeme_hatirlatma",
            language = "tr",
            @params = new[] { "Ayşe", "   " },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("Odeme_Hatirlatma", "tr")]   // büyük harf — Meta'nın alfabesinde yok
    [InlineData("odeme hatirlatma", "tr")]   // boşluk
    [InlineData("", "tr")]
    [InlineData("odeme_hatirlatma", "türkçe")]
    [InlineData("odeme_hatirlatma", "")]
    public async Task A_malformed_template_reference_is_refused(string name, string language)
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s, DateTimeOffset.UtcNow.AddHours(-25));

        var resp = await SendTemplateAsync(s, new { name, language });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // Bağlı hesabı olmayan lisans için şablon da gönderilemez; sebebi gövdede
    // taşınıyor (serbest metinle aynı sözleşme).
    [Fact]
    public async Task A_template_needs_a_connected_account()
    {
        var s = await SeedAsync();

        var resp = await SendTemplateAsync(s, new { name = "hello_world", language = "en_US" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await resp.Content.ReadFromJsonAsync<SendResponse>())!;
        body.Ok.Should().BeFalse();
        body.ErrorCode.Should().Be("no_account");
    }

    private sealed record DroppedInboundDto(
        int CustomerCount, int MessageCount,
        DateTimeOffset? FirstSeenAt, DateTimeOffset? LastSeenAt);

    private static async Task<DroppedInboundDto> GetDroppedAsync(Seed s) =>
        (await s.Client.GetFromJsonAsync<DroppedInboundDto>(
            "/api/panel/whatsapp-conversations/dropped-inbound"))!;

    private async Task AddDroppedAsync(
        Guid licenseId, string bsuId, int count, DateTimeOffset first, DateTimeOffset last)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.WaDroppedInbounds.Add(new WaDroppedInbound
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, BsuId = bsuId,
            PhoneNumberId = "PNID_1", MessageCount = count,
            FirstSeenAt = first, LastSeenAt = last,
        });
        await db.SaveChangesAsync();
    }

    // Kayıp yokken 404 değil sıfırlı gövde: panel "veri yok" ile "kayıp yok"u
    // ayırt etmek zorunda kalmasın.
    [Fact]
    public async Task Hic_dusen_mesaj_yoksa_sifir_doner()
    {
        var s = await SeedAsync();

        var body = await GetDroppedAsync(s);

        body.CustomerCount.Should().Be(0);
        body.MessageCount.Should().Be(0);
        body.FirstSeenAt.Should().BeNull();
        body.LastSeenAt.Should().BeNull();
    }

    // Satır sayısı kaç MÜŞTERİ, MessageCount toplamı kaç mesaj — ikisi ayrı
    // soru ve uyarı metni ikisini de kullanıyor. Damgalar da iki uçtan:
    // "ne zamandan beri" ile "en son ne zaman" farklı satırlardan gelebilir.
    [Fact]
    public async Task Musteri_sayisi_ile_mesaj_sayisi_ayri_toplanir()
    {
        var s = await SeedAsync();
        var ilk = DateTimeOffset.UtcNow.AddDays(-9);
        var son = DateTimeOffset.UtcNow.AddHours(-2);

        await AddDroppedAsync(s.LicenseId, "US.1", 7, ilk, DateTimeOffset.UtcNow.AddDays(-5));
        await AddDroppedAsync(s.LicenseId, "US.2", 4, DateTimeOffset.UtcNow.AddDays(-3), son);

        var body = await GetDroppedAsync(s);

        body.CustomerCount.Should().Be(2);
        body.MessageCount.Should().Be(11);
        body.FirstSeenAt.Should().BeCloseTo(ilk, TimeSpan.FromSeconds(1));
        body.LastSeenAt.Should().BeCloseTo(son, TimeSpan.FromSeconds(1));
    }

    // Uyarı "senin müşterin sana yazdı" diyor. Başka yayıncının hattındaki
    // kayıp sızsaydı, yayıncıyı hiç yaşamadığı bir kayba inandırırdı.
    [Fact]
    public async Task Baska_yayincinin_kaybi_sizmaz()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync("905441112233");

        await AddDroppedAsync(
            theirs.LicenseId, "US.9", 99,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var body = await GetDroppedAsync(mine);

        body.CustomerCount.Should().Be(0);
        body.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task Kimliksiz_istek_401_alir()
    {
        var anon = _factory.CreateClient();

        var resp = await anon.GetAsync("/api/panel/whatsapp-conversations/dropped-inbound");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
