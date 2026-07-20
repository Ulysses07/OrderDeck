namespace OrderDeck.Chat.YouTube;

/// <summary>
/// Compile-time embedded YouTube Data API key (the <c>AIza…</c> key used by the
/// official gRPC <c>streamList</c> ingestor). Empty by default in source control
/// so cloned/public copies of the repo never carry a live key; the production
/// build machine overrides this file locally before producing installers.
///
/// <para><b>Security note.</b> A key baked into a desktop binary is not secret —
/// anyone can decompile the DLL and read the string. That is inherent to
/// client-side distribution and cannot be fixed by encryption (the decryption
/// key would ship alongside). The real protection lives in Cloud Console:
/// restrict this key to <b>YouTube Data API v3 only</b> and set a daily quota
/// cap, so a leaked key cannot be abused against other APIs or run up unbounded
/// usage. Embedding it here (vs. plaintext <c>settings.json</c>) only raises the
/// friction bar; it is not a confidentiality guarantee.</para>
///
/// <para><b>Override flow (one-time, on the production build machine):</b></para>
/// <list type="number">
///   <item>Paste the real value from Cloud Console → Credentials → API keys:
///     <code>public static readonly string ApiKey = "AIza...";</code>
///   </item>
///   <item>Tell git to ignore your local edit so it never gets pushed:
///     <code>git update-index --skip-worktree OrderDeck.Chat/YouTube/YouTubeApiDefaults.cs</code>
///   </item>
///   <item>Build the installer normally — the key is baked into the shipped
///   binary and end users get the official ingestor working out of the box.</item>
/// </list>
///
/// <para><b>Runtime resolution order</b> (see
/// <c>YouTubeOfficialChatHostedService</c>):</para>
/// <list type="number">
///   <item><c>AppSettings.YouTubeApiKey</c> — explicit override that wins first
///     (handy for QA / a separate Cloud project without a rebuild).</item>
///   <item>The constant here — fall-through for the common end-user install.</item>
///   <item>If both are empty, the official ingestor logs a warning and idles
///     (the classic scraper keeps working).</item>
/// </list>
/// </summary>
internal static class YouTubeApiDefaults
{
    /// <summary>YouTube Data API v3 key (<c>AIza…</c>). See class docs.</summary>
    public static readonly string ApiKey = "";
}
