# Phase 3 — YouTube video adapter

確認日: 2026-09-16。

## 目的

YouTube Data API v3を使い、動画をまずprivateとしてdurableにuploadし、processing成功と公開条件を確認した後に目的visibilityへ遷移させる。

Provider固有の公開方法はYouTube Adapterへ閉じ込め、公開前の人間承認は共通`Publication Approval Gate`へ委ねる。

## 公式仕様の前提

- resumable uploadはsession開始POSTで`Location` URIを取得し、そのURIへPUTする。
- upload中断後は同じsession URIへ空PUT + `Content-Range: bytes */TOTAL`で状態を確認でき、`308 Resume Incomplete`の`Range`から受領済みbyteを判定できる。
- `308 Resume Incomplete`に`Retry-After`がある場合は、その時刻まで待ってからresumeする。
- 中断したPUTについてclientは「全部届いた」「何も届いていない」のどちらも仮定せず、server側offsetを照会してから再開する。
- session URIが失効した場合は404となる。送信結果が曖昧でないsessionだけ新sessionから安全に再構築できる。
- upload完了時はvideo resourceとvideo IDを得る。
- `videos.list`の`processingDetails.processingStatus`はprocessing progressのpoll用途として公式に定義されている。
- processing失敗の第一原因は`processingDetails.processingFailureReason`で確認し、`suggestions.processingErrors[]`は補助診断として扱う。
- API error bodyの`error.errors[].reason`を既知値だけ正規化し、403を一律に認証失敗とは扱わない。quota、rate limit、scope、policy、channel状態、invalid inputを分離する。
- `videos.update`は指定した`part`内のmutable fieldを上書きする。既存値をbodyから省略すると削除/default化され得るため、status変更直前にremote statusを再取得する。
- `status.publishAt`はprivateかつ未公開の動画だけに設定できる。post-router Phase 3Aはnative schedulingを所有しないため、remote側に`publishAt`が存在する場合はvisibilityを書き換えず`NeedsAttention`へ止める。
- 2020-07-28以降に作成された未監査API projectから`videos.insert`された動画はprivateに制限される。public/unlisted公開はGoogle auditが成立するまでlive acceptance未完了として扱う。
- `videos.insert`のmedia upload上限は256 GBで、`video/*`または`application/octet-stream`を受け付ける。
- upload clientはtitle、description、privacyをユーザーが指定できる必要がある。titleは最大100 Unicode scalar valuesとして扱い、UTF-16 code unit数でemojiを過剰に短く制限しない。
- non-Child-Directed clientからのuploadではMade for Kids指定をupload前に可能にする。post-routerは`madeForKids`を明示必須とし、`selfDeclaredMadeForKids`へ送る。
- YouTube upload入口ではYouTube Terms / Community Guidelines / copyright / privacyに関するupload noticeを表示し、intent側でも`uploadNoticeAcknowledged=true`を明示させる。

参照:

- https://developers.google.com/youtube/v3/guides/using_resumable_upload_protocol
- https://developers.google.com/youtube/v3/docs/videos/insert
- https://developers.google.com/youtube/v3/docs/videos
- https://developers.google.com/youtube/v3/docs/videos/update
- https://developers.google.com/youtube/v3/docs/errors
- https://developers.google.com/identity/protocols/oauth2/native-app
- https://developers.google.com/youtube/terms/required-minimum-functionality
- https://developers.google.com/youtube/terms/api-services-terms-of-service
- https://developers.google.com/youtube/terms/developer-policies
- https://developers.google.com/youtube/v3/revision_history

## Phase 3A — 最小vertical slice

初期実装は動画1本を対象とする。画像/Community投稿は扱わない。

### OAuth

Desktop App OAuth 2.0 + PKCE S256を使用する。

- system browser
- explicit loopback IP redirectは`127.0.0.1`または`::1`のみ許可
- `access_type=offline`
- refresh tokenが必要な接続/reconnectで再同意を明示するため`prompt=consent`
- scopeは`https://www.googleapis.com/auth/youtube.force-ssl`
- refresh tokenを既存vaultへ保存
- `channels.list(mine=true)`で得たYouTube channel IDをremote subjectとしてAccountへ保存
- reconnect時は既存channel IDと一致した場合だけ再接続

installed appはclient secretを安全に保持できない前提なので、client secret依存の設計にしない。

### Upload policy metadata

YouTube targetは`youtube-options/v1`として以下を扱う。

```json
{
  "madeForKids": false,
  "containsSyntheticMedia": false,
  "uploadNoticeAcknowledged": true
}
```

- `madeForKids`: 必須boolean。`status.selfDeclaredMadeForKids`へ送る。
- `containsSyntheticMedia`: 任意boolean。指定時は`status.containsSyntheticMedia`へ送る。
- `uploadNoticeAcknowledged`: upload noticeを確認した明示フラグとして必須true。Providerへは送らない。

CLIだけではなく、runtimeに登録するYouTube Adapter自身も`madeForKids`の明示と`uploadNoticeAcknowledged=true`をinvariantとして検証する。将来GUI/automationからYouTube uploadを公開するときも、このProvider invariantを迂回せず、同等のhuman-facing noticeを入口に表示する。

### Upload

1. spool済み動画のsize/SHA-256を送信前に再検証する。
2. metadataはprivateでresumable sessionを開始する。Made for Kidsとsynthetic-media指定もinsert metadataへ含める。
3. session URIはGoogle upload host allowlistを検証したうえで暗号化provider checkpointへ保存する。
4. data PUTの直前に同じsessionへstatus queryを行い、server側の確定offsetを取得する。
5. 取得したoffsetから残りfile rangeだけをstreaming PUTする。動画全体をメモリへ読み込まない。
6. network loss / process crash後も同じ「status query → remaining PUT」を再実行するため、古いlocal offsetをblind resendしない。
7. `308`の`Retry-After`が未来時刻なら、そのworker turnではdata PUTへ進まず再実行時刻をdurableに返す。
8. 曖昧なdata PUTが一度もないsessionの404は新sessionから再構築できる。曖昧なdata PUT後にsessionが失効した場合は、video IDなしで安全に照合できないため自動再uploadせず`NeedsAttention`へ止める。
9. upload完了でvideo IDを取得し、必ずdurable checkpointしてからprocessing確認へ進む。
10. Xの通常API timeoutとYouTube大容量upload timeoutを分離し、YouTube uploadは12時間の独立timeoutを持つ。timeout/network interruption後もremote offsetからresumeする。
11. Windows production runtimeではdata PUT中にspool fileのread/share-read handleを保持し、verifyから再openまでのpath replacement/write raceを狭める。SHA-256/size再検証は引き続きinner adapter側で行う。

registered runtimeでは`YouTubeResumeSafeAdapter`がresumable PUTをquery-first unitに包む。coreが`ResumeKnownHandle`のabandoned claimを再queueしても、再実行時にremote offsetを照会してからdataを送るため、crash後のbyte重複/欠落をlocal推測に依存しない。

通常progressとfailure retry budgetを混同しない。成功したresumable progressとprocessing pollは`ConsumesRetryBudget=false`として、通信失敗等だけがfailure retry budgetを消費する。

### Processing

既知video IDに対して`videos.list?part=status,processingDetails,suggestions`をpollする。

- processing / uploaded: Pendingとして再確認
- processed + processing succeeded: 公開準備完了
- upload failed/rejected: `status.failureReason` / `status.rejectionReason`をsafe categoryへ正規化して停止
- processing failed: `processingDetails.processingFailureReason`を第一原因とし、必要なら`suggestions.processingErrors[]`を補助にして停止
- response/network failure: read-only pollとしてbackoff retry
- first remote submissionから7日を超えてprocessingが完了しない場合: 無期限pollせず`NeedsAttention`

processing pollは正常進行であり、publication failure retryとは区別する。恒久failureを自動full re-uploadへ戻さない。

### API error classification

Google API errorのraw message/bodyはGUI・safe errorへ露出しない。既知`reason`だけをwhitelist normalizeする。

- `rateLimitExceeded` / `userRateLimitExceeded` → rate limit。bounded retry対象
- `quotaExceeded` / `dailyLimitExceeded` → quota exhaustion。自動retryせず`NeedsAttention`
- `uploadLimitExceeded` → channel/user upload limit。自動retryせず`NeedsAttention`
- `insufficientPermissions` / auth系 → Authentication / `NeedsAttention`
- `forbiddenPrivacySetting` / `forbiddenLicenseSetting` → policy/setting rejection / `NeedsAttention`
- `channelSuspended` / generic `forbidden` → provider/account state / `NeedsAttention`
- `invalid*` / `mediaBodyRequired` → InvalidInput
- 5xx → 一時障害。ただしvisibility update送信後の5xxは公開副作用不明なので`Unknown`へ落としてread reconciliationする

### Publication boundary

動画はupload時点ではprivateのままにする。

`private` targetもprocessing成功だけでは完了させず、完了直前にremote statusを再取得して実際にprivateであることを確認する。外部操作でpublic/unlistedへ変わっていた場合は`NeedsAttention`へ止める。

`public` / `unlisted` targetは公開直前にremote statusを再取得する。

- 既に目的visibilityならread confirmationだけでPublishedへ確定する。
- 現在visibilityがprivate以外かつ目的visibilityでもない場合は、外部変更との競合として`NeedsAttention`へ止める。
- `status.publishAt`が存在する場合は、Phase 3Aが所有していないnative scheduleを壊さないため`NeedsAttention`へ止める。
- 上記確認後のfresh snapshotだけを使って`videos.update(part=status)`する。

visibility updateを`StepEffect.MayPublish`として計画し、共通Approval Gateを通す。

- `Automatic`: 条件成立後そのまま公開stepへ進む。
- `RequireApproval`: `AwaitingApproval`で停止し、承認後に同じvideo IDへの公開stepを再開する。
- 承認待ちの長短にかかわらず、実際のvisibility update直前にremote statusを再確認する。

public/unlisted updateのresponseを失った場合、新しい動画を作成しない。既知video IDをGETし、目的visibilityになっていればPublishedへ確定する。目的visibilityでない場合は自動的に別動画を作ったりblind updateしない。同じIDのread reconciliationだけを行い、目的状態を確認できない状態がfailure retry上限まで続いた場合は`NeedsAttention`へ止める。

### Scheduling scope

現行coreの`AtTime`はlocal workerのJob due timeであり、動画upload自体もdue時刻になってから始まる。Phase 3Aはこのlocal schedulingをそのまま使う。

YouTube native `status.publishAt`は仕様上利用可能だが、事前upload用のPrepare jobと公開予約の二重実行防止をcore側で分離する必要があるため、Phase 3Aでは実装しない。remote側でnative scheduleを検出した場合も上書きせず停止する。

将来native schedulingを追加するときは、少なくとも以下を先に実装する。

- due時刻より前にprivate uploadできるPrepare job
- upload完了video IDのdurable保持
- native schedule登録後にlocal publish jobを無効化する単一所有権
- schedule update/cancel/reconcile
- 過去`publishAt`のlocal reject

## Replay safety

| step | effect | replay policy |
| --- | --- | --- |
| OAuth identity/read | ReadOnly | SafeRead |
| resumable session開始 | CreateRemoteObject / 非公開 | SafeRepeatNoPublication。公開は起きないが、session開始応答喪失では孤児sessionが残り得る |
| session status query | ReadOnly | SafeRead |
| query-first file PUT / resume | UploadOnly | ResumeKnownHandle。毎回remote offsetを再確認してからremaining rangeのみ送る |
| processing poll | ReadOnly | SafeRead |
| known video IDのpublic/unlisted update | MayPublish | IdempotentExistingObject + known ID reconciliation |

workerのdispatch例外判定も`StepEffect`だけでなく`ReplaySafety`を見る。`SafeRead` / `SafeRepeatNoPublication` / `ResumeKnownHandle`は安全な再開対象で、公開副作用が不明な操作だけをUnknownへ落とす。

## 入力制約

Phase 3Aでは次をadapter/CLIで検証する。

- 動画は1 assetのみ
- MIMEは`video/*`または`application/octet-stream`
- adapter上限は1 byte以上256 GB以下
- titleは必須、最大100 Unicode scalar values、`<`/`>`禁止
- descriptionは5000 UTF-8 bytes以下、`<`/`>`禁止
- visibilityはprivate / unlisted / public
- YouTube targetは`madeForKids`を明示必須
- `containsSyntheticMedia`は任意boolean
- YouTube targetはupload notice確認後に`uploadNoticeAcknowledged=true`を明示必須

注: 現行の共通`SpoolStore`は別途容量上限を持つ。YouTube APIの256 GB上限とローカルspoolの実運用上限は同一概念ではない。

## Acceptance boundary

CIではfake HTTPによりOAuth、loopback制約、refresh-token再同意条件、resumable recovery、abandoned-claim後のremote offset query、308 Retry-After、safe session-expiry判定、Unicode title境界、Made for Kids metadata、Google error reason分類、processingFailureReason、processing progress/deadline、approval gate、remote visibility競合、native schedule guard、rate-limit、status update/reconcileを検証する。

本番対応済みと呼ぶには別途以下が必要。

- Google Cloud project
- YouTube Data API有効化
- Desktop OAuth client
- test channelでの本人同意
- quota確認
- private upload実試験
- Google audit成立後のpublic/unlisted実試験
- YouTube Required Minimum Functionality / Developer Policies / API Termsに沿ったhuman-facing metadata、privacy、upload noticeの最終確認

未監査projectではprivate uploadまでをlive acceptance可能範囲とし、public/unlisted自動公開を成功済みとして扱わない。
