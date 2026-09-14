# Provider Adapter contract

提案契約 v1 / 2026-09-14。

## Portの分割

全providerに巨大な万能interfaceを実装させない。registryはProviderDescriptorと利用可能なportを登録する。

| Port | 契約 |
| --- | --- |
| ProviderDescriptor | providerKey、adapterVersion、contractVersion、対応options/checkpoint version |
| CapabilityPort | account/content文脈に対するCapabilities。readによる更新可 |
| ValidationPort | Content/Options/Consentを検証。素材送信や公開をしない |
| PublicationPort | planNextStep、executeStep、fetchStatus、reconcile |
| NativeSchedulePort | register/update/cancelのstep計画。unsupportedなら登録しない |
| MetricsPort | metric catalog、query plan、取得、decode/normalize |
| DeletePort | remote削除の計画と結果確認。公開取消と別 |
| AuthPort | login request、code exchange、refresh、revoke、grant検査 |

AuthPortはInfrastructureのvaultを直接使わず、ApplicationのAuthCoordinatorが秘密materialを短時間渡し、返されたsecret更新をcommitする。secret型はToString/serialize不可を原則とする。

## Capabilities

`CapabilityResult` は capability key / Supported, Unsupported, Conditional, Blocked, Unknown / reason code / required inputs / constraints / evidence / checkedAt / expiresAtを返す。

例: publish.videoはSupportedでも、public visibilityはBlocked、analytics.watchはUnsupportedとなり得る。Account role、OAuth scope、app audit、entitlement、地域、現在のcreator制限、staging設定を分ける。通信失敗をUnsupportedとキャッシュしない。

制約はMediaConstraint（MIME、サイズ、時間、解像度、個数）、ScheduleConstraint（native可否、必要lead time、取消範囲）、OperationalConstraint（PC稼働、asset hosting、明示UI同意）、MetricDescriptorで表現する。API field名はここへ漏らさない。

Capabilitiesのキャッシュは助言であり認可証明ではない。enqueue時と危険な実行直前に再検証する。新制約で不適合ならNeedsAttention、勝手な本文・visibility変更は禁止。

## 実行stepとdurable checkpoint

`planNextStep(intent, checkpoint, observation)` は次の1つの操作を返す。

- stepKey: 同じoperationを識別する安定キー。
- effect: ReadOnly / UploadOnly / CreateRemoteObject / MayPublish / UpdateExisting / DeleteExisting。
- earliestAt / latestAt、estimatedCost、timeout、rateLimitBucket。
- replaySafety: SafeRead / ResumeKnownHandle / IdempotentExistingObject / NotReplayable。
- opaque request planと次checkpoint schema。

汎用schedulerが内容を読めないopaque planを無検証で実行するのではない。署名やコード実行を許すDSLはなく、adapter内部で生成した型付き操作だけ。planはsecretを含む可能性があり、ログやCLIへ返さない。

ApplicationはAttempt=DispatchPreparedをDBへcommitしてからexecuteStepを呼び、返却されたReceiptをDBへcommitする。**1回のexecuteStep内で次の公開危険stepまで連続実行しない。** 新しいremote handleを得たら必ず呼出し元へ返し、durable保存の境界を作る。upload chunksもack済みoffsetを保存してから進む。

## 結果型

StepResultはCompleted / Pending / Rejected / Ambiguous。remote handles、opaque checkpoint、observed state、safe diagnostics、rate/quota observationsを含む。Completedでもworkflow完了とは限らない。

Ambiguousはremote側副作用の有無が不明な場合。JSON破損した2xx、応答受領前の切断、非冪等request後の5xxも該当し得る。HTTPだけで分類しない。

ReconcileResultはFound（証拠付きIDと状態）/ ConfirmedAbsent（対象operationを特定して未実行と証明）/ StillPending / Inconclusive / Conflict。timelineに見つからないことだけでConfirmedAbsentとしない。近い本文・時刻・durationは人間向け候補に留める。

## Provider別の実行分割

| Provider | 段階 | 決定的な公開境界 |
| --- | --- | --- |
| X | media init→chunks→finalize/status→Post create→lookup | POST /2/tweets |
| YouTube | resumable session→private upload→ID保存→status updateで公開/予約→poll | ID既知の公開/予約更新。private動画作成にも重複risk |
| Instagram | staging/rupload→container ID保存→status→media_publish→公開確認 | media_publish。container作成は公開成功ではない |
| TikTok（将来条件付き） | creator/consent→initでpublish_id保存→transfer→status | FILE_UPLOAD最終chunk、またはPULL_FROM_URL initが公開を開始し得る |

TikTokを「uploadは常に副作用なし」と扱わない。YouTube native予約とlocal公開を並走させない。InstagramでcontainerがPUBLISHEDだがfinal media ID未回収なら再publishせず、公開済みの証拠を保持してID照合を続ける。

## エラー分類

| code category | publicationへの扱い | retry |
| --- | --- | --- |
| Validation / Unsupported / PolicyBlocked | FailedまたはNeedsAttention | 入力/条件変更までなし |
| AuthExpired | grant refresh job、対象を待機 | refresh成功後、元操作のreplay safetyに従う |
| AuthRevoked / ScopeMissing | ReauthRequired / NeedsAttention | 自動loginなし |
| RateLimited | RetryWaitingとbucketのnextAllowedAt | provider時刻/Retry-After以降 |
| QuotaExhausted | 待機またはNeedsAttention | quota reset後。残高不足は入金待ち |
| TemporaryUnavailable | 安全read等はRetryWaiting | backoff |
| AmbiguousSideEffect | Unknown | read照合のみ |
| MalformedResponse / ContractChanged | 無副作用ならNeedsAttention、副作用不明ならUnknown | 盲目的retryなし |
| RemoteRejected / Removed | Failed/availability更新 | 再投稿なし |
| StorageFailure | worker停止・回復要求 | HTTPを先に進めない |

safeErrorにはcategory、code、短いsanitized message、provider request ID、retryAfter、effectCertainty、recommendedActionを含める。token、request body、full URL、providerの自由文をそのまま入れない。

## HTTPとAPI version

Provider DTOはinternal。missing required IDや未知の公開statusを成功へdefaultしない。未知の非重要fieldは許容し、metricsの再解釈可能な範囲は暗号化Rawへ保持する。仕様変更で必須fieldが消えたら影響providerのcapabilityをdegradedにし、他providerは継続。

version-specific endpoint/DTOと意味変換を分離する。URLをconfigで任意指定させない。fake server向けbase URL注入はtest build/明示test構成のみでproduction profileには持ち込まない。

## 契約の出荷条件

各adapterはfake HTTPの同じfailure matrixに加え、固有のhandle expiry・processing・public制限のテストを持つ。checkpoint旧versionを読めること、secret漏洩がないこと、曖昧応答を自動再送しないことが必須。[テスト](testing.md)

Unsupported port呼出しは明示エラーで返す。空metrics、成功扱いdelete、native予約のlocalへの暗黙切替は禁止。
