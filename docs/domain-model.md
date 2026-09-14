# Domain model

設計契約 v1 / 2026-09-14。

## Aggregateとidentity

| Model | 主要属性 | 不変条件 |
| --- | --- | --- |
| Account | id、providerKey、alias、remoteSubjectId、remoteAccountId、authGrantId、status、capabilitySnapshotId | aliasは表示用。remote IDは文字列。provider＋app＋remote accountが実体 |
| AuthGrant | id、appRegistrationId、subject、grantedScopes、vaultRef、generation、expiresAt、refreshStatus | 複数accountが1grantを共有可能。secretはDomainへ持たない |
| Post | id、clientRequestId、currentRevision、createdAt、label | 人間が作った一回の配信意図。外部SNSのPostとは異なる |
| PostRevision | postId、revision、contentId、targetsDigest、requestedAt、intentHash | enqueue後immutable。差し替えはrevisionを追加 |
| Content | id、kind、text、title、mediaAssets | TextOnly / ImageSet / Video。意味的入力でありwire DTOではない |
| MediaAsset | id、sha256、sizeBytes、detectedMime、duration、width、height、storageRef | 予約は固定コピーのみ参照。original pathを後日読み直さない |
| Target | id、postId、revision、accountId、visibilityIntent、optionsSchema、optionsVersion、optionsPayload | provider固有optionsはadapter所有の検証済み契約。secret不可 |
| ConsentRecord | id、targetId、kind、policyVersion、recordedAt、evidenceDigest | providerが要求する投稿同意・開示確認。Scheduleへ混ぜずTargetに結び付ける。tokenや画面内容は保存しない |
| Schedule | id、mode、requestedLocalTime、zoneId、offset、dueAtUtc、maxLateness、zoneRulesFingerprint | dueAtUtcは希望公開時刻。曖昧DSTを黙って補正しない。prepare/poll等の実行時刻はJob.dueAtへ置く。OSに版番号がなければ規則fingerprintは任意 |
| Publication | id、targetId、scheduleId、state、executionMode、createdAt、firstSubmittedAt、confirmedAt、publishedAt、lastError | 公開workflowの正本。Targetごとに1つ。結果が不明ならPublishedにもFailedにも確定しない。試行回数はAttemptから算出する |
| RemoteObject | id、publicationId、kind、providerObjectId、parentObjectId、visibility、remoteCreatedAt、remotePublishedAt、observedAt | upload/container/publish handle/final objectのIDを混同しない。1 Publicationに複数可 |
| Job | id、kind、ownerType、ownerId、priority、dueAt、state、generation、attemptNo、workerRunId | 投稿・照合・stats・refresh・cleanupの永続実行単位。ownerはPublication/AuthGrant/StatsSyncRun/DataDeletionのいずれか1つ。dueAtは次のローカル操作時刻 |
| Attempt | id、jobId、publicationId、stepKey、dispatchState、startedAt、finishedAt、requestDigest、effectCertainty、safeError | HTTP前のintentとHTTP後のreceiptを区別 |
| ProviderCheckpoint | publicationId、adapterKey、schemaVersion、payloadRef、updatedAt | Adapterのみ解釈。機密URLを含み得るので暗号化 |
| StatsSyncRun | id、scope、state、requestedAt、finishedAt、safeError | 投稿公開状態とは独立した収集run。partialを表現 |
| MetricsSnapshot | id、accountId、subjectRef、provider、apiVersion、retrievedAt、period、dimensions、rawId、mappingVersion | 投稿/媒体/accountと時間窓を保持。値0と未取得は別 |
| MetricObservation | snapshotId、providerMetricKey、canonicalKey、definitionVersion、value、unit、status | 定義を潰さない。数値以外にunsupported等の状態を持つ |

Account status: Ready / ReauthRequired / PolicyBlocked / Disabled / Removed。単なるtoken期限切れでAccountをRemovedにしない。

## Publication状態

Pending、Preparing、Ready、Publishing、Processing、ScheduledRemote、Published、Unknown、AwaitingUser、NeedsAttention、Failed、Expired、CancelRequested、Cancelled。

これらは公開workflowの共通語彙。providerの生enumはここへ持ち込まず、adapterが証拠の強さと一緒にmappingする。待機・backoff・claimはJobの状態とdueAtで表し、PublicationにRetryWaitingを持たせない。これにより429がPreparing/Publishing/Processingのどこで起きたかを失わない。Publishedは要求した公開先・visibilityで公開が確認された状態。private upload成功をpublic Publishedと呼ばない。

DeletedはPublicationの公開履歴を消す状態ではなく、RemoteObjectの現在availabilityとして記録する。過去にPublishedだった事実と、現在Deleted/Unavailableであることは両立する。

## Contentとprovider options

共通visibilityはPublic / Unlisted / Private / AccountDefaultの意図。SNS固有の可視範囲はprovider optionsに置き、無理な意味変換を拒否する。

OptionsEnvelopeはschemaId / version / canonical serialized payload / digestを持つ。opaqueな保存単位であって、任意API requestの逃げ道ではない。adapter-owned schemaでunknown keyを拒否し、scope、endpoint、Authorization等を指定できない。

TikTokのprivacy選択・商用開示、YouTube madeForKids・formatIntent、IG placement等を表現可能にする。providerが明示同意の証拠を要求する場合はConsentRecordに残し、汎用Scheduleへprovider事情を入れない。coreは内容を解釈せずAdapterのrequired inputs/validation resultを扱う。

## IDsと時刻

ローカルIDはUUID、remote IDはopaque string。64bitを超える値や先頭0を数値化しない。publishedAtがremoteから得られない場合はnullとし、observedAtを代入しない。希望時刻、送信時刻、remote作成時刻、公開確認時刻、統計期間は別。

`Schedule.dueAtUtc` は希望公開時刻、`Job.dueAt` は次のローカル処理時刻。native schedulingの事前upload/登録JobはScheduleより早く実行できるが、希望公開時刻そのものをprepare時刻へ書き換えない。

Post全体の状態は子Publicationの集約表示（AllPending / InProgress / AllPublished / Partial / NeedsAttention等）。集約を子への指示として使わない。一部成功をall-successにしない。

## 冪等性と改訂

clientRequestIdはinstallation内でunique。intentHashはschema名 `post-intent/v1`、本文・titleの正確なUnicode scalar列、順序付き素材hash、account UUIDでsortしたTargetのvisibility/options、Schedule.mode、AtTimeならdueAtUtc、maxLatenessを固定順のUTF-8 JSON writerでserializeしてSHA-256を算出する。文字列をtrim・Unicode正規化・改行変換しない。object key順、整数表現、UTC表現をgolden testで固定する。raw path、CLI引数の並び順、表示alias、Immediate enqueue時の現在時刻はhashへ入れない。hashだけで同一判定せず、保存したcanonical intent bytesも一致確認する。

同一キー同一意図は既存Post。同一キー別意図はConflict。途中のSNS失敗でPostを新規作成しない。failed対象のretryは、remote副作用がないと確定し元Scheduleがまだ有効な場合だけ同じPublicationとAttempt履歴を使う。ExpiredやUnknownは同じPublicationでpublish retryしない。意図的な再投稿は新しいPostとkey、元PostへのduplicateOfを保存する。

## Metricsのsubject

RemoteObjectがmediaの場合とfinal Postの場合を区別する。X media viewとPost impressions、YouTube channel全体とvideo filter、IG account reachは異なるsubject。対象Publicationへ関連付けても帰属範囲は変えない。

metrics snapshotは複数ページ・複数queryの一括成功を表すものと、個別responseのRaw recordを区別する。不完全収集はpartialとし、欠けた値を0で埋めない。

即時投稿はSchedule.mode=Immediate、日時指定はAtTimeとする。Immediateの初回実行時刻は保存するがintentHashには現在時刻を入れず、modeとmaxLatenessを含める。PostRevisionは将来拡張を阻害しない保存境界であり、MVPに編集コマンドがあることを意味しない。

## Job ownership

JobのownerTypeとkindの組合せは閉じた表で管理する。Publish/Prepare/Poll/Reconcile/DeleteはPublication、RefreshはAuthGrant、StatsCollectはStatsSyncRun、PurgeはDataDeletionをownerにする。DBでは対応するnullable FKを1つだけ持たせるCHECK制約で表現し、文字列ownerIdだけに依存しない。新job kindの追加はmigrationとApplication handler登録を必要とし、未知kindを汎用実行しない。
