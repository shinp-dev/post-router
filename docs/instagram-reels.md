# Instagram Reel publishing (Instagram Login)

確認日: **2026-09-18**。根拠はMeta公式の[Content Publishing](https://developers.facebook.com/docs/instagram-platform/instagram-api-with-instagram-login/content-publishing)と同ページからリンクされた[IG User media](https://developers.facebook.com/docs/instagram-platform/instagram-graph-api/reference/ig-user/media)、[media_publish](https://developers.facebook.com/docs/instagram-platform/instagram-graph-api/reference/ig-user/media_publish)の資料。接続済みInstagram Professional accountの`instagram_business_content_publish`を使用する。Facebook LoginのPage token、Page連携、`graph.facebook.com`は使用しない。

| 操作 | Instagram LoginのHTTP要求 | 確認結果 |
| --- | --- | --- |
| Reel container作成 | `POST https://graph.instagram.com/v26.0/{IG_ID}/media` | `media_type=REELS`、公開HTTPSの`video_url`、`caption`を送信し、`id`（container ID）を受け取る。 |
| 状態取得 | `GET https://graph.instagram.com/v26.0/{CONTAINER_ID}?fields=status_code` | 読み取り専用。`IN_PROGRESS`、`FINISHED`、`ERROR`、`EXPIRED`、`PUBLISHED`。 |
| 公開 | `POST https://graph.instagram.com/v26.0/{IG_ID}/media_publish` | `creation_id={CONTAINER_ID}`を送信し、成功応答の`id`（Instagram media ID）を保存する。 |

通常のGraph API呼び出しは`Authorization: Bearer`で既存Instagram LoginのIG User access tokenを送信する。Metaのtoken lifecycleで必須のGET query例外を投稿APIへ広げない。上記のAPI version `v26.0`は確認日に公式本文の例で示されたもの。実行時にはredirectを追従せず、raw URL、token、response body、caption、staging URLをログやsafe errorへ出さない。

`share_to_feed`は[IG User media reference](https://developers.facebook.com/docs/instagram-platform/instagram-graph-api/reference/ig-user/media)にReelのパラメータとして記載され、trueでFeedとReels、falseでReelsのみ。**UNCONFIRMED:** このreferenceの例はFacebook Login寄りで、Instagram Login専用本文で同パラメータの適用を独立には確認できない。GUIの初期値はtrueとし、送信はこの共通referenceに基づく。実機受入で確認する。

動画は既存spoolの実ファイル検査で非空のMP4のみ受け付ける。公式Reel資料にはcodec、duration、frame rate、サイズの条件があるが、**UNCONFIRMED:** 取得したInstagram Login専用本文から2026-09-18時点の完全な数値組を確定できなかった。FFmpegを追加して推測検査は行わない。GitHub Release Asset側の上限より保守的に1 GiBをローカル上限とし、Metaの形式拒否は安全な固定コードへ写す。

Metaの公開制限はIG accountにつき移動24時間で100件。`GET /{IG_ID}/content_publishing_limit`で現在使用量を確認できる。コンテナは作成から24時間以内に公開しなければ`EXPIRED`になる。公式は約1分間隔で最長5分程度のstatus pollingを推奨する。Post Routerは一定間隔でread-only pollingし、過度な再照会を避ける。`FINISHED`を確認してから1分以上経過した場合は公開前に再照会する。`FINISHED`は動画取得と公開準備が終わった状態なので、一時公開Assetは以後削除してよい。通常経路ではmedia IDの永続保存後に削除し、cleanupだけを再試行する。

## 応答喪失と復旧

`media_publish`に公式idempotency keyは確認できない。公開POST送信後のtimeout・切断では、同じ`creation_id`を自動再送しない。container statusの`PUBLISHED`は公開済みの読み取り専用証拠になるが、同status応答からmedia IDを取得できる公式手段は**UNCONFIRMED**。media IDの取得を保証できない限りPost Routerの`Published`へは進めず、`Unknown`または`NeedsAttention`で手動確認を促す。Retry操作もpublishを再実行しない。`FINISHED`だけでは未公開の証明にならず、再送しない。

container作成POSTの応答喪失も同様に二重作成を避ける。operation-bound container IDの読み取り専用回収手段は**UNCONFIRMED**なので、無条件再送せず手動確認にする。staging operation、container ID、media IDはそれぞれdurableに記録する。staging uploadの復旧だけは既存`ITemporaryPublicMediaHost.RecoverAsync`を使う。

| 境界 | 応答・障害 | Post Routerの動作 |
| --- | --- | --- |
| GitHub staging | 応答喪失 | 同じoperation IDをread-only recoverし、二重uploadしない。 |
| container作成 | 明確な400/401 | safe codeで停止。429は後で再試行する。 |
| container作成 | timeout、切断、クラッシュ | `Unknown`からread-only reconcileへ。container IDを回収できなければ`NeedsAttention`。 |
| container status | `IN_PROGRESS` | 約1分後に再照会。5分を超えれば確認待ち。 |
| container status | `ERROR`、`EXPIRED`、未知の値 | safe codeで停止。 |
| media_publish | 明確な400/401/429 | safe codeで停止し、同じcontainerを自動再公開しない。 |
| media_publish | 送信後timeout、切断、クラッシュ | `Unknown`からstatusのみ照会。media IDを確定できなければ`NeedsAttention`。Retryは不可。 |
| media_publish | 正常応答のmedia ID | IDとcleanup継続jobを同一DB transactionで保存する。 |
| GitHub cleanup | delete失敗、cleanup中のクラッシュ | media IDを保持し、deleteだけ再試行する。`media_publish`へ戻らない。 |

HTTP送信に入る前の認証失敗は副作用なしとして扱う。`HttpClient.SendAsync`開始後のtimeoutは「request未送信」と証明できないため、たとえ実際には未送信でも公開結果不明として止める。これは二重投稿を避けるための意図的な安全側判定。

## 実機受入

接続済みInstagram Professional accountと既存GitHub staging設定を確認する。ユーザーがGUIで短いMP4とcaption、`share_to_feed`を選びQueue登録し、workerを実行する。ステージング、container `FINISHED`、公開、media ID保存、`Published`、GitHub Asset削除を順に確認する。**実Reel投稿はユーザーの明示操作でのみ開始する。**
