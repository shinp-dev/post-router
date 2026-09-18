# Instagram OAuth connection

Post Router uses **Instagram API with Instagram Login** for Professional Business and Creator accounts. A Facebook Page and Facebook Login for Business are not part of this flow. This change connects accounts only; Instagram publishing is unavailable, and Instagram is absent from publication target choices.

## Meta specification checked on 2026-09-18

- [Business Login](https://developers.facebook.com/docs/instagram-platform/instagram-api-with-instagram-login/business-login) defines `https://www.instagram.com/oauth/authorize`, `POST https://api.instagram.com/oauth/access_token`, `GET https://graph.instagram.com/access_token`, and `GET https://graph.instagram.com/refresh_access_token`. The code exchange sends the same `redirect_uri` used in authorization. The short token response includes granted `permissions`; the application requires both requested permissions before connecting. Meta documents `state` as an optional CSRF value; Post Router requires and validates it.
- [Instagram API with Instagram Login in Meta's Postman collection](https://www.postman.com/meta/instagram/folder/6raa77c/instagram-api-with-instagram-login) confirms `instagram_business_basic` and `instagram_business_content_publish` and that this mode does not require a Facebook Page. The older `business_*` scope names were deprecated on 2025-01-27.
- [Get Started](https://developers.facebook.com/docs/instagram-platform/instagram-api-with-instagram-login/get-started) describes `/me` fields `id`, `user_id`, and `username`. `user_id` is the Professional Instagram account ID; `id` is app scoped. Post Router requests all three and uses `user_id` when returned. The user has separately confirmed that `/me?fields=id,username` works with a manually generated token; that token is not used here.
- Business Login describes refreshing an unexpired long token after it is at least 24 hours old. Post Router refreshes only when the shared AuthCoordinator sees an imminent expiry. An expired token requires reconnection. There is no periodic background refresh.
- No official Instagram Login PKCE or remote revoke endpoint was confirmed in those materials. Post Router does not invent either. Disconnect deletes local encrypted account credentials; it does **not** revoke remote authorization. Remove app access in Instagram/Meta separately if remote revocation is needed.

## Callback and credential handling

The registered redirect URI is exactly `https://auth.shinp-studio.com/instagram/callback`. The externally managed Cloudflare Tunnel forwards this HTTPS origin to `http://127.0.0.1:8765`. Post Router binds only `127.0.0.1:8765`; it does not install, configure, or manage cloudflared. A forwarded `Host: auth.shinp-studio.com` is accepted on that loopback listener. The listener is opened before the browser, processes one callback, and closes on completion, failure, timeout, cancellation, or application shutdown. It requires a 256-bit random state with exact matching and a five-minute deadline. Browser callback pages contain only static success or failure text.

App ID is nonsecret settings data. App Secret is registered from a local file, standard input, or a hidden CLI prompt into the existing encrypted vault. Account tokens and the App Secret needed for reconnect live in encrypted vault blobs. The JSON settings file holds only App ID and an opaque vault blob ID. No credential is returned by GUI APIs or CLI status. The browser authorization URL contains App ID, scope, redirect URI, and state only.

**Meta公式Instagram Login token exchange仕様に従うための限定的例外:** Meta requires App Secret and the short token in the **GET query** of `https://graph.instagram.com/access_token`; the documented refresh endpoint likewise requires the long token in the GET query of `https://graph.instagram.com/refresh_access_token`. These are the only two exception endpoints. The query is formed only for an ephemeral HTTPS request to the fixed `graph.instagram.com` host and path. The dedicated client disables redirect following, cookies, and raw `HttpRequestOut` diagnostic events. The app never logs a request URI, query, HTTP body, or raw Meta response. .NET 10's default HTTP EventSource query redaction is verified by a test; the requests fail closed if `System.Net.Http.DisableUriRedaction` is enabled. The general rule remains: **do not put App Secret or access tokens into URLs for other API calls**. `/me` uses a bearer Authorization header.

## Local acceptance

1. Ensure the existing `post-router-auth` Cloudflare Tunnel forwards `https://auth.shinp-studio.com` to `http://127.0.0.1:8765`. Do not paste its token into Post Router or a chat.
2. Start the GUI with your intended data directory. On the Accounts page, save the Instagram App ID, select a local text file containing the App Secret, and register it in the vault. The callback URL is displayed for comparison with Meta settings.
3. Click **Instagramに接続** and authorize the Professional account in the opened browser window. The callback window should report completion, and the Accounts page should show `Connected`, username, and provider account ID. For the current personal acceptance account, verify the displayed username is `shinpstudio`; that name is not hard coded.
4. Disconnect and reconnect if desired. Disconnect removes local account credentials only. To remove the configured App Secret as well, use **App Secretを削除**.

Equivalent CLI commands (the App Secret never appears in a process argument):

```text
post-router account instagram configure --app-id <Instagram-App-ID>
post-router account instagram secret set --secret-file <local-secret-file>
post-router account instagram status
post-router account instagram connect
post-router account instagram reconnect --account <account-id>
post-router account instagram disconnect --account <account-id>
post-router account instagram secret clear
```

`secret set --secret-stdin` and a hidden prompt are also supported. Never place the secret itself on a command line. Live OAuth remains a user acceptance step; automated tests use fake HTTP responses and do not contact Meta.
