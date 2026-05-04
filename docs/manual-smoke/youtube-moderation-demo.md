# YouTube Moderation — Demo & Audit Smoke Test

The video uploaded to the YouTube Data API quota-extension form is recorded
following this exact script. Repeat any time the OAuth client, scopes, or
moderation flow change so future submissions stay current.

## Prerequisites

1. **Cloud Console:** an OAuth Client ID of type "Desktop app" exists for the
   `OrderDeck` project. Note the Client ID + Client Secret.
2. **Test YouTube channel:** any account with live-streaming enabled (24-hour
   warm-up if it's brand new).
3. **`%AppData%\OrderDeck\settings.json`** has the credentials wired in:

   ```jsonc
   {
     // ...other settings...
     "YouTubeOAuthClientId": "1234...apps.googleusercontent.com",
     "YouTubeOAuthClientSecret": "GOCSPX-..."
   }
   ```

   These are read on startup; restart the app after editing.

## Recording setup

- **OBS Studio**, 1280×720, 30 fps, no audio. CRF 30 keeps a 4–5 minute clip
  under the form's 10 MB cap.
- Window-capture OrderDeck plus a second source for the browser when the
  consent screen pops, so reviewers see the redirect happen.
- Optional callout text overlay naming each step ("1. Connect YouTube",
  "2. liveChatMessages.list polling", etc.).

## Demo script

| Step | Action in app | What it proves |
| ---: | ------------- | -------------- |
| 1 | Open OrderDeck → **Settings → YouTube** tab. Status reads "Bağlı değil". | Disconnected baseline. |
| 2 | Click **YouTube'a Bağlan**. Default browser opens to Google's consent page. | OAuth client exists, redirect URI accepted. |
| 3 | On the consent screen, expand the scopes list. The two scopes shown are `youtube` and `youtube.force-ssl`. | Matches Privacy Policy declaration. |
| 4 | Click **Allow**. Browser shows a success page; OrderDeck status flips to "Bağlı: <channel name>". | Refresh token persisted (encrypted via DPAPI). |
| 5 | Start a YouTube live broadcast on the test channel (mobile YouTube app or YouTube Studio). | Provides a target broadcast for the next steps. |
| 6 | Type `aldım xl` from a viewer account into that broadcast's chat. The message appears in OrderDeck's chat panel within ~5 s. | `liveChatMessages.list` polling working. |
| 7 | Right-click the message → **YT'de mesajı sil**. Open YouTube Studio's live-chat viewer in a side window — the message disappears. | `liveChatMessages.delete` working. |
| 8 | Type a second viewer message → right-click → **YT'de kullanıcıyı banla**. Confirm the dialog. | `liveChatBans.insert` working. |
| 9 | Verify the banned user appears in YouTube Studio → "Banned users" list. | End-to-end ban succeeded. |
| 10 | Back to **Settings → YouTube → Bağlantıyı Kaldır**. Status returns to "Bağlı değil". | Local token cleared on disconnect. |

End the recording.

## Sanity checks before submitting

- File size ≤ 10 MB (the form rejects anything larger).
- Chat message text + timestamps are legible on a 1080p monitor.
- Browser address bar is visible during the OAuth redirect (proves the
  loopback URI matches what's configured in Cloud Console).
- No personal data leaks: blur viewer display names if they're not yours.

## Notes for reviewers (paste into the form's "How to test" field)

> Reviewers can reproduce the demo with a complimentary OrderDeck license.
> Email `support@orderdeckapp.com` and we will provision the account within
> 24 h. The Settings → YouTube tab guides through the connect flow shown in
> the video; the moderation actions are accessible from any chat row
> originating from a YouTube live broadcast.
