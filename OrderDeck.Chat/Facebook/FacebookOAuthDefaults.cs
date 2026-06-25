namespace OrderDeck.Chat.Facebook;

/// <summary>
/// Compile-time embedded Facebook App credentials. Mirrors the YouTube
/// equivalent — production values are baked here on the build machine,
/// QA/dev overrides come from <see cref="OrderDeck.Core.Settings.AppSettings"/>.
///
/// <para><b>What lives here:</b></para>
/// <list type="bullet">
///   <item><see cref="AppId"/> — public client identifier (safe to ship).</item>
///   <item><see cref="AppSecret"/> — used in the desktop code → access-token
///     exchange. Like Google's "Desktop app" guidance, this isn't actually
///     secret because it travels in every shipped binary; the audit-relevant
///     credential is the user/page access token in
///     <see cref="EncryptedFacebookTokenStore"/>.</item>
///   <item><see cref="LoginConfigId"/> — Facebook Login for Business
///     configuration id, encodes the permission set we set up in Meta
///     dashboard ("Manage everything on your Page" use case).</item>
/// </list>
///
/// <para><b>Runtime resolution order</b> (see <see cref="FacebookOAuthService"/>):</para>
/// <list type="number">
///   <item><c>AppSettings.FacebookAppId/Secret/LoginConfigId</c> — wins first.</item>
///   <item>The constants here — fall-through for the common install.</item>
///   <item>If both empty → <c>ConnectAsync</c> throws a clear "credentials
///     missing" error so the operator knows to drop values into settings.json
///     or grab a properly-built installer.</item>
/// </list>
/// </summary>
internal static class FacebookOAuthDefaults
{
    /// <summary>Meta App ID — Business type, created 2026-06-24 under
    /// "Emar Global Tekstil Gıda İnşaat Turizm Yazılım ve TİC. LTD. Şti".</summary>
    public static readonly string AppId = "26296352503396523";

    /// <summary>Meta App Secret. Populated on the production build machine
    /// before installer build, kept empty in source control so cloned repos
    /// don't ship credentials tied to an active Meta App.</summary>
    public static readonly string AppSecret = "";

    /// <summary>Facebook Login for Business configuration id. Encodes the
    /// "Manage everything on your Page" use case permission set: business_management,
    /// pages_show_list, pages_read_engagement, pages_read_user_content,
    /// pages_manage_engagement, pages_manage_metadata. Populated on the
    /// production build machine alongside <see cref="AppSecret"/>.</summary>
    public static readonly string LoginConfigId = "";

    /// <summary>Graph API version pinned for the entire integration. Bump
    /// in one place when a new version becomes the default — Meta deprecates
    /// versions after ~2 years.</summary>
    public const string GraphApiVersion = "v22.0";
}
