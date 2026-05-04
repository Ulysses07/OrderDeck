namespace OrderDeck.Chat.YouTube;

/// <summary>
/// Compile-time embedded YouTube OAuth client credentials. Empty by default
/// in source control so cloned/public copies of the repo never carry
/// credentials tied to an active Cloud Console project; the production build
/// machine overrides this file locally before producing distributable
/// installers.
///
/// <para><b>Override flow (one-time, on the production build machine):</b></para>
/// <list type="number">
///   <item>Open this file and paste the real values from Cloud Console →
///   Credentials → OAuth 2.0 Client IDs (Desktop app):
///     <code>
///     public static readonly string ClientId = "1234...apps.googleusercontent.com";
///     public static readonly string ClientSecret = "GOCSPX-...";
///     </code>
///   </item>
///   <item>Tell git to ignore your local edits so they never get pushed:
///     <code>git update-index --skip-worktree OrderDeck.Chat/YouTube/YouTubeOAuthDefaults.cs</code>
///   </item>
///   <item>Build the installer normally — the credentials are baked into the
///   shipped binary, end users get YouTube moderation working out of the box.</item>
/// </list>
///
/// <para><b>Runtime resolution order</b> (see <see cref="YouTubeOAuthService"/>):</para>
/// <list type="number">
///   <item><c>AppSettings.YouTubeOAuthClientId/Secret</c> — explicit override
///     that takes priority. Handy for QA / per-tester credentials without a
///     rebuild.</item>
///   <item>The constants here — fall-through for the common path on a normal
///     end-user install.</item>
///   <item>If both are empty, <c>YouTubeOAuthService.ConnectAsync</c> throws
///     a clear "credentials missing" error so the operator knows to either
///     edit settings.json or get a properly built installer.</item>
/// </list>
/// </summary>
internal static class YouTubeOAuthDefaults
{
    /// <summary>OAuth 2.0 Client ID, Desktop application type. See class docs.</summary>
    public static readonly string ClientId = "";

    /// <summary>OAuth 2.0 Client Secret. See class docs. Note that desktop
    /// app secrets are not actually secret per Google's official guidance —
    /// they ship inside every distributed binary anyway. Encryption here would
    /// add friction without raising the bar for an attacker with file-system
    /// access. The audit-relevant secret to protect is the user's refresh
    /// token, which IS encrypted via DPAPI in
    /// <see cref="EncryptedYouTubeTokenStore"/>.</summary>
    public static readonly string ClientSecret = "";
}
