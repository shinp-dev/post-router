# Phase 2A — X text provider

確認日: 2026-09-15。Phase 1 durable coreの上に、最初の実ProviderとしてXのtext-only投稿を追加した。

## 実装範囲

- Native App向けOAuth 2.0 Authorization Code + PKCE（S256、loopback callback、state検証）
- scope: `tweet.read tweet.write users.read offline.access`
- access token / refresh tokenは既存AES-GCM vaultへ保存。SQLiteのaccount metadataにはremote subject、alias、display name、client ID、scope、期限だけを保存
- access token期限5分前のrefreshと、Phase 1のgrant単位process間lock/CASによるrotation
- `POST https://api.x.com/2/tweets` によるtext-only公開Post
- create responseのPost IDを`remote_objects`へ保存し、PublicationをPublishedへ遷移
- `account connect/status/disconnect/reset/revoke/reconnect`
- disconnectは履歴・metrics・queueを残したままaccountをDisconnectedにし、grantと暗号化blobを同一transactionで削除
- reconnectは元のX user IDと一致した場合だけ同じAccount IDを再有効化。別userへの誤送信を拒否

Phase 2Aでは画像・動画・delete・metricsを有効化しない。X固有endpoint、DTO、scope、PKCE、HTTP error mappingは`XProvider`境界内に置く。

## 公式仕様の根拠

- [OAuth 2.0 Authorization Code + PKCE](https://docs.x.com/fundamentals/authentication/oauth-2-0/authorization-code): Native Appはpublic client、access tokenは通常2時間、`offline.access`でrefresh token、redirectはexact match
- [User access token flow](https://docs.x.com/fundamentals/authentication/oauth-2-0/user-access-token): token endpoint、refresh、`POST /2/oauth2/revoke`
- [Create Posts](https://docs.x.com/x-api/posts/create-post): `POST /2/tweets`、Bearer user token、201 responseの`data.id`
- [Rate limits](https://docs.x.com/x-api/fundamentals/rate-limits): createはuser 100/15分、app 10,000/24時間。実行時はresponse headerを優先
- [Pricing](https://docs.x.com/x-api/getting-started/pricing): 現行はcreditによるpay-per-usage。実単価と利用可否はConsoleを正とする

create endpointには、このoperationへ結び付くidempotency keyを公式request contract上確認できない。送信開始後のtimeout、接続断、5xx、2xx malformed response、process crashは副作用が曖昧になり得る。

## 二重投稿保証

HTTP送信前のvalidation/auth/rate rejectionは安全に再試行できる。送信開始後に結果を確定できない場合はPublicationをUnknownへ移し、元のpublish jobをDoneにしてread-only Reconcileだけを作る。Xのtimeline類似検索はoperation-boundな証拠ではないため、自動で成功/不在と認定せず、同じcreateを再POSTしない。したがって二重投稿回避を優先できるが、lost responseからremote Post IDを自動回収できる保証はない。

## CLI受入手順

Developer ConsoleでNative App、OAuth 2.0、callback URL、必要creditを準備する。次はcallbackを`http://127.0.0.1:8765/callback`として登録した例。

```powershell
pub account connect x --client-id <client-id> --redirect-uri http://127.0.0.1:8765/callback --alias x-main
pub account list
pub account status --account <account-id>
pub post --file contracts/examples/release.x-text.json
pub worker once
pub post status <post-id>
pub account disconnect --account <account-id>
pub account reconnect --account <account-id> --redirect-uri http://127.0.0.1:8765/callback
pub account revoke --account <account-id>
```

OAuth URLは既定browserで開く。callback listenerは明示port/pathだけを最大5分待ち、state不一致を拒否する。client secretはNative Appでは使用・保存しない。

## 検証状態

| 項目 | 状態 |
| --- | --- |
| fake HTTPでOAuth exchange / identity / refresh / revoke contract | 自動検証対象 |
| queue→worker→X adapter→201→Published/Post ID保存 | 自動検証対象 |
| disconnect中のqueue停止、claim後の再確認、同一subject reconnect | 自動検証対象 |
| 401/400/429/5xx/malformed response/Unknown | 自動検証対象 |
| token/AuthorizationのSQLite・CLI・safe error非流出 | 自動検証対象 |
| Windows Credential Manager実機 | Windows受入確認待ち |
| X OAuth consent / refresh / revoke | 実credential確認待ち |
| X本人accountへの実投稿 | 実credentialと課金承認待ち |
| lost create responseのremote ID自動回収 | Provider contract上保証不能 |

通常CIは実SNS credentialを要求しない。実投稿は本人account・予算・明示的な投稿内容を用意した手動受入試験として分離する。
