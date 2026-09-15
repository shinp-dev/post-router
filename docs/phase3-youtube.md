# Phase 3 — YouTube video adapter

確認日: 2026-09-16。

## 目的

YouTube Data API v3を使い、動画をまずprivateとしてdurableにuploadし、processing成功と公開条件を確認した後に目的visibilityへ遷移させる。

Provider固有の公開方法はYouTube Adapterへ閉じ込め、公開前の人間承認は共通`Publication Approval Gate`へ委ねる。

## 公式仕様の前提

- resumable uploadはsession開始POSTで`Location` URIを取得し、そのURIへPUTする。
- upload中断後は同じsession URIへ空PUT + `Content-Range: bytes */TOTAL`で状態を確認でき、`308 Resume Incomplete`の`Range`から受領済みbyteを判定できる。
- session URIが失効した場合は404となり、新しいresumable sessionを開始して最初からuploadし直す。
- upload完了時はvideo resourceとvideo IDを得る。
- `videos.list`の`processingDetails.processingStatus`はprocessing progressのpoll用途として公式に定義されている。
- upload failure/rejection/processing failureには公開されたreason値があり、X native videoより機械的な分類根拠が強い。
- `videos.update`は指定した`part`内のmutable fieldを上書きする。既存値をbodyから省略すると削除/default化され得るため、status変更時は現在statusを取得し、保持すべきmutable fieldを明示して更新する。
- `status.publishAt`はprivateかつ未公開の動画だけに設定でき、設定時は`privacyStatus=private`も同時に指定する。過去時刻を指定すると即時公開になるため、post-routerは過去`publishAt`を送らない。
- 2020-07-28以降に作成された未監査API projectから`videos.insert`された動画はprivateに制限される。public/unlisted公開はGoogle auditが成立するまでlive acceptance未完了として扱う。
- `videos.insert`のmedia upload上限は256 GBで、`video/*`または`application/octet-stream`を受け付ける。

参照:

- https://developers.google.com/youtube/v3/guides/using_resumable_upload_protocol
- https://developers.google.com/youtube/v3/docs/videos/insert
- https://developers.google.com/youtube/v3/docs/videos
- https://developers.google.com/youtube/v3/docs/videos/update
- https://developers.google.com/identity/protocols/oauth2/native-app
- https://developers.google.com/youtube/v3/revision_history

## Phase 3A — 最小vertical slice

初期実装は動画1本を対象とする。画像/Community投稿は扱わない。

### OAuth

Desktop App OAuth 2.0 + PKCE S256を使用する。

- system browser
- explicit loopback IP redirect
- `access_type=offline`
- scopeは`https://www.googleapis.com/auth/youtube.force-ssl`
- refresh tokenを既存vaultへ保存
- `channels.list(mine=true)`で得たYouTube channel IDをremote subjectとしてAccountへ保存
- reconnect時は既存channel IDと一致した場合だけ再接続

installed appはclient secretを安全に保持できない前提なので、client secret依存の設計にしない。

### Upload

1. spool済み動画のsize/SHA-256を送信前に再検証する。
2. metadataはprivateでresumable sessionを開始する。
3. session URIはGoogle upload host allowlistを検証したうえで暗号化provider checkpointへ保存する。
4. 通常は残りfile rangeをstreaming PUTする。動画全体をメモリへ読み込まない。
5. network loss / crash後はsession status queryでoffsetを確認し、未送信rangeだけを再開する。
6. session 404は公開副作用のない失効として新sessionから再構築する。
7. upload完了でvideo IDを取得し、必ずdurable checkpointしてからprocessing確認へ進む。

通常progressとfailure retry budgetを混同しない。成功したresumable progressとprocessing pollは`ConsumesRetryBudget=false`として、通信失敗等だけがfailure retry budgetを消費する。

### Processing

既知video IDに対して`videos.list?part=status,processingDetails,suggestions`をpollする。

- processing / uploaded: Pendingとして再確認
- processed + processing succeeded: 公開準備完了
- upload failed/rejected、processing failed: 公開reasonをsafe categoryへ正規化し、恒久failureは自動再uploadしない
- response/network failure: read-only pollとしてbackoff retry

processing pollは正常進行であり、publication failure retryとは区別する。

### Publication boundary

動画はupload時点ではprivateのままにする。

`private` targetはprocessing成功をもってremote delivery完了とする。`public` / `unlisted` targetは、公開直前に現在のstatusを再取得し、保持すべきmutable fieldを含めて`videos.update(part=status)`する。

このvisibility updateを`StepEffect.MayPublish`として計画し、共通Approval Gateを通す。

- `Automatic`: 条件成立後そのまま公開stepへ進む。
- `RequireApproval`: `AwaitingApproval`で停止し、承認後に同じvideo IDへの公開stepを再開する。
- 承認待ちが長引いてstatus snapshotが古くなった場合は、公開前に再取得してからupdateする。

public/unlisted updateのresponseを失った場合、新しい動画を作成しない。既知video IDをGETし、目的visibilityになっていればPublishedへ確定する。目的visibilityでない場合は自動的に別動画を作ったりblind updateせず、同じIDのread reconciliationを継続する。

### Scheduling scope

現行coreの`AtTime`はlocal workerのJob due timeであり、動画upload自体もdue時刻になってから始まる。Phase 3Aはこのlocal schedulingをそのまま使う。

YouTube native `status.publishAt`は仕様上利用可能だが、事前upload用のPrepare jobと公開予約の二重実行防止をcore側で分離する必要があるため、Phase 3Aでは実装しない。`publishAt`をその場しのぎで送ることはしない。

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
| resumable session開始 | CreateRemoteObject / 非公開 | SafeRepeatNoPublication。応答喪失時に孤児session/private objectが残り得るが公開は起きない |
| session status query | ReadOnly | SafeRead |
| file PUT / resume | UploadOnly | ResumeKnownHandle |
| processing poll | ReadOnly | SafeRead |
| known video IDのpublic/unlisted update | MayPublish | IdempotentExistingObject + known ID reconciliation |

workerのdispatch例外判定も`StepEffect`だけでなく`ReplaySafety`を見る。`SafeRead` / `SafeRepeatNoPublication` / `ResumeKnownHandle`は安全な再開対象で、`NotReplayable`等の公開副作用が不明な操作だけをUnknownへ落とす。

## 入力制約

Phase 3Aでは次をadapterで検証する。

- 動画は1 assetのみ
- MIMEは`video/*`または`application/octet-stream`
- 1 byte以上256 GB以下
- titleは必須、100文字以下、`<`/`>`禁止
- descriptionは5000 UTF-8 bytes以下、`<`/`>`禁止
- visibilityはprivate / unlisted / public
- provider optionsは`youtube-options/v1`の空object

## Acceptance boundary

CIではfake HTTPによりOAuth、resumable recovery、processing分類、approval gate、status update/reconcileを検証する。

本番対応済みと呼ぶには別途以下が必要。

- Google Cloud project
- YouTube Data API有効化
- Desktop OAuth client
- test channelでの本人同意
- quota確認
- private upload実試験
- Google audit成立後のpublic/unlisted実試験
- YouTube Required Minimum Functionalityに沿ったhuman-facing metadata/privacy操作の最終確認

未監査projectではprivate uploadまでをlive acceptance可能範囲とし、public/unlisted自動公開を成功済みとして扱わない。
