# Phase 3 — YouTube video adapter

確認日: 2026-09-16。

## 目的

YouTube Data API v3を使い、動画をまずprivateとしてdurableにuploadし、processing成功と公開条件を確認した後にpublicまたはscheduledへ遷移させる。

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
- 2020-07-28以降に作成された未監査API projectから`videos.insert`された動画はprivateに制限される。public/native schedulingはGoogle auditが成立するまでlive acceptance未完了として扱う。
- 2026年のgranular quota移行後、`videos.insert`はVideo Uploads quota bucketで100 calls/dayが既定値として文書化されている。

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
- explicit loopback IPv4 redirect
- `access_type=offline`
- refresh tokenを既存vaultへ保存
- YouTube channel IDをremote subjectとしてAccountへ保存
- reconnect時は既存channel IDと一致した場合だけ再接続

installed appはclient secretを安全に保持できない前提なので、client secret依存の設計にしない。

### Upload

1. spool済み動画のsize/SHA-256を送信前に再検証する。
2. metadataはprivateでresumable sessionを開始する。
3. session URIを暗号化provider checkpointへ保存する。
4. 通常は残りfile rangeをstreaming PUTする。動画全体をメモリへ読み込まない。
5. network loss / crash後はsession status queryでoffsetを確認し、未送信rangeだけを再開する。
6. session 404は公開副作用のない失効として新sessionから再構築する。
7. upload完了でvideo IDを取得し、必ずdurable checkpointしてからprocessing確認へ進む。

通常progressとfailure retry budgetを混同しないため、成功したuploadを細かな固定chunk stepへ分割しない。1回のupload stepで残りrangeをstreamし、中断した場合だけ次回session status queryで復旧位置を確認する。

### Processing

既知video IDに対して`videos.list?part=status,processingDetails,suggestions`をpollする。

- processing / uploaded: Pendingとして再確認
- processed + processing succeeded: 公開準備完了
- upload failed/rejected、processing failed: 公開reasonをsafe categoryへ正規化し、恒久failureは自動再uploadしない
- response/network failure: read-only pollとしてbackoff retry

processing pollは正常進行であり、publication failure retryとは区別する。

### Publication boundary

動画はupload時点ではprivateのままにする。

公開直前のYouTube status updateを`StepEffect.MayPublish`として計画し、共通Approval Gateを通す。

- `Automatic`: 条件成立後そのまま公開stepへ進む。
- `RequireApproval`: `AwaitingApproval`で停止し、承認後に同じvideo IDへの公開stepを再開する。

Immediateはknown video IDをpublicへ変更する。
Scheduledはvideoをprivateのまま未来`publishAt`へ設定する。

過去`publishAt`、max-lateness超過、capability不足、audit未成立を人間承認だけで上書きしない。

## Replay safety

| step | effect | replay policy |
| --- | --- | --- |
| OAuth identity/read | ReadOnly | SafeRead |
| resumable session開始 | CreateRemoteObject相当だが非公開 | response不明時は新session乱造せず、既知sessionがあればstatus確認。sessionを取得できなかった曖昧開始は孤児private resourceの可能性を考慮する |
| session status query | ReadOnly | SafeRead |
| file PUT / resume | UploadOnly | ResumeKnownHandle |
| processing poll | ReadOnly | SafeRead |
| known video IDのpublic/scheduled update | MayPublish | IdempotentExistingObject + known ID reconciliation |

public/scheduled updateのresponseを失った場合、新しい動画を作成しない。既知video IDをGETしてvisibility / publishAtを照合する。

## Acceptance boundary

CIではfake HTTPによりOAuth、resumable recovery、processing分類、approval gate、status update/reconcileを検証する。

本番対応済みと呼ぶには別途以下が必要。

- Google Cloud project
- YouTube Data API有効化
- Desktop OAuth client
- test channelでの本人同意
- quota確認
- private upload実試験
- Google audit成立後のpublic/scheduled実試験

未監査projectではprivate uploadまでをlive acceptance可能範囲とし、public自動公開を成功済みとして扱わない。
