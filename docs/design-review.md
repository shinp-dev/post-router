# Final design review

監査日: 2026-09-14。対象はPR #1の設計文書14ファイル。実装・本番API操作・OAuth発行・動作試験は行っていない。監査時のPR base/headと最終HEADはGitHub上で再確認する。

## 判定

**条件付きの設計として採用可能。4媒体すべてへの自分用CLIによる無人公開という要望は現条件では不成立。**

実装できると断言して未確認仕様を埋めるのではなく、Capabilitiesとrelease gateに外部条件を反映した。X/YouTube/Instagramの段階的MVPを推奨し、TikTok Direct Postはblocked。Instagramのcanonical公式本文を取得できなかった事項はG-IGに集約し、公開機能のrelease条件とした。

## 指摘と反映結果

| Severity | 観点・監査で見つけた問題 | 最終設計への修正・確認 | 残留risk / 判定 |
| --- | --- | --- | --- |
| High | PublicationとJobの両方がRetryWaitingを持ち、429時の戻り先が一意でない | retry/backoffはJobだけに集約。PublicationはPreparing等のworkflow段階を維持 | Job/Publicationを跨ぐtransaction testが必要 |
| High | single workerを全HTTP直列と実装すると長時間uploadが他SNSの予約を塞ぐ | 1 coordinator process内のbounded dispatcher、owner/account直列、期限priorityを定義 | provider側処理時間による公開遅延は残る |
| High | Windows Task Schedulerの既定実行時間上限で常駐workerが停止し得る | ExecutionTimeLimit=PT0Sを明示し、登録後readbackと72時間相当testを追加 | OS task設定の実機差はacceptance test必須 |
| High | backup restore後のquarantineを解除するCLI/判定がなく、運用不能 | restore/status/release契約を追加。未解決publishがあればrelease拒否 | 照合不能な対象は自動復旧できない |
| Medium | Job ownerがpublication/grantだけでstats/purgeをschema上表現できない | 4種nullable FK＋kind別CHECK、WorkerRun/claim情報を追加 | 新kindごとにmigrationが必要 |
| Medium | idempotency hashのcanonical byte列とmaxLateness有無が未定 | post-intent/v1固定writer、文字列/target順/時刻/maxLateness、golden testを定義 | contract変更時はversion追加が必要 |
| Medium | Scheduleにprovider固有consentAtが混入 | ConsentRecordをTargetへ移し、Scheduleを時刻意図に限定 | provider UX証拠の詳細は各Phaseで確定 |
| Medium | Windowsで取得不能なtzdb versionを必須保存し得る | dueAtUtc/offsetを正本とし、zone規則fingerprintはoptional | 過去規則の完全再現は保証しない |
| Medium | worker stop/uninstall/maintenanceの実行契約が不足 | graceful drain、start/stop、maintenance lock、in-flight回復を定義 | 強制終了時はUnknownが残り得る |
| Medium | X動画上限を監査途中で旧DM制約の512MBへ誤修正していた | 現行公式本文を再取得し、通常Postは非Premium 20分/8GB、Premium・verified 125分/16GB、512MBはDMと訂正 | account entitlementとcodec等はX Phaseで実行時検査 |
| High | `Schedule.dueAtUtc` と `Job.dueAt` が同じ「dueAt」で、希望公開時刻とprepare時刻を混同し得る | Schedule=希望公開時刻、Job=次ローカル操作時刻と明記。native準備は前倒し可、local final publishは希望時刻前に送らない | 実公開完了時刻はprovider/networkで遅れ得る |
| High | Expiredを同じPublicationのretry対象にすると、missed policyを越えて意図しない遅延公開が可能 | `post retry` は安全なFailedかつschedule有効時だけ。Expiredは新しい希望時刻・新keyのPostを要求 | 人間が明示的に新規再投稿する場合の重複riskは残る |
| Medium | Instagram `media_publish` response喪失後にPUBLISHED/final IDを必ず復旧できる前提が強すぎた | G-IGに曖昧結果のreconciliation/final Media ID復旧を追加。未確認ならUnknown→NeedsAttention、新container/re-publish禁止 | canonical本文確認まで自動照合能力は未保証 |
| Medium | API調査の「本文/検索」だけでは未確認事項の実装可否をAI Agentが誤読し得る | CONFIRMED / UNCONFIRMED / IMPLEMENTATION-TIME CHECK REQUIREDを明示しProvider別statusを追加 | 公式仕様は継続変更するため出荷時再確認が必要 |
| Low | Analyticsで1h/6h/24h/3d/7d/28dをMVP既定自動予約するのは単一PC用途に過剰 | 構造は複数snapshot対応のまま、MVPは明示 `stats sync` 基本。自動cadenceはProvider Phaseでquota/料金確認後に追加 | 将来cadence追加時のbudget設計は必要 |
| Confirmed | Provider abstractionへendpoint/status/scopeが漏れる | Domainに持たずAdapter client/DTOへ隔離。optional portsとversioned options | 新しい共通の意味が必要ならcore変更はあり得る |
| Confirmed | 最小限だけに揃えて固有機能を失う | YouTube madeForKids/Shorts、IG placement、TikTok consent等をtyped optionsで保持 | 未確認optionはenabledにしない |
| Confirmed | SDK responseをdomainとして保存してしまう | wire DTO→意味変換→Canonical、raw/checkpointは別領域 | Adapter契約testが出荷条件 |
| Confirmed | API version固定で意味も不変と思い込む | definitionVersion、mappingVersion、checkpoint schemaを分離 | 公式changelogの継続確認が必要 |
| Confirmed | publishを汎用retryする | MayPublish/NotReplayableとUnknownを使用。read照合のみ自動 | exactly-onceは保証せず、未公開で止まる可能性 |
| Confirmed | uploadを全て無害とみなす | TikTok最終chunk/URL initを公開境界に分類 | 将来対応時も必須contract |
| Confirmed | adapter内の複数HTTP間でreceiptが失われる | 1 executeStepに1durable境界、handle保存後に次操作 | response後commit前の不確実性は残る |
| Confirmed | lease満了で二重workerが起きる | OS lock保持＋DB CAS、時間だけでtakeoverしない | hung processは人間が停止し照合 |
| Confirmed | native予約失敗時のlocal fallbackが二重投稿になる | executionMode固定、既知video ID照合、無断切替禁止 | provider受理後の電源OFFでも公開は進み得る |
| Confirmed | 過去publishAtによる即公開 | upload見積＋最低5分余裕、送信前再検証、余裕不足は停止 | network受信時刻の厳密制御は不可 |
| Confirmed | PC再起動で予約が消える/遅れて勝手に公開 | SQLite queue、同一user task、ログオン後recover、15分late policy | ログオン前・電源OFFのlocal実行は既定保証外 |
| Confirmed | cancelとdispatchのrace | CancelRequestedとremote確認を分離 | 取消要求だけで取消成功とは言えない |
| Confirmed | 古いbackupでPublishedがPendingへ戻る | restore quarantineで過去Pendingも自動送信禁止 | 失ったreceiptは人間/remote照合が必要 |
| Confirmed | token refreshがaccount単位lockで競合 | AuthGrant単位OS lock、DB generationとencrypted token同時commit | rotation response喪失時は再認証 |
| Confirmed | 即時投稿のretryごとに現在時刻がhashへ入りConflict | Immediateを意味としてhash、初回dueAtだけ保存 | 同じkey別内容は意図通りConflict |
| Confirmed | 予約編集のコマンドと契約が曖昧 | MVPはin-place editなし。取消確認＋新規key | Revision modelは将来用に保持 |
| Confirmed | Raw無期限保存がprovider規約と衝突 | retention class、再認可、期限処理、export/backup削除 | 長期停止中の期限処理は利用者運用が必要 |
| Confirmed | Normalizedが指標意味差・派生規約を潰す | provider値・単位・subject/periodを維持、独自ratio/scoreなし | 同名viewsも同値比較と断言しない |
| Confirmed | stats失敗で投稿成功までFailedになる | StatsSyncRunをPublicationから独立 | analyticsの遅延/欠測は残る |
| Confirmed | Secretsがlog/config/raw/backupへ漏れる | allowlist、vault、機密checkpoint暗号化、backup範囲を明示 | 本文等は通常DBにあり同一user侵害は防げない |
| Confirmed | CLI callbackを全社共通loopbackと誤認 | Instagramのcallback方式はG-IGの公式確認条件。他社は各登録規則 | IG追加運用が必要な可能性 |
| Confirmed | migrationで旧checkpointを新規投稿に変える | version拒否・互換reader・backup/quarantine | 自動down migrationなし |
| Confirmed | 各SNS公式API上でMVPが成立するか | gate別に判定。TTを審査さえすれば使えるとは書かない | 全4媒体無条件MVPは不可 |
| Confirmed | 過剰設計か | 単一binary/DB/worker、broker/service/webhookなし。MVPの自動stats cadenceも削減 | vault/Unknownは必要な複雑性として維持 |

初回監査と実装担当視点の再監査を合わせた修正findingは **Critical 0 / High 6 / Medium 8 / Low 1**。`Confirmed` 行は既存設計が要求を満たした確認事項で、finding件数に含めない。High/Mediumはすべて本設計へ反映済みで、実装後のfailure testを残す。

## 実装担当としての再監査

Phase 1の各classを自分で実装する前提で、状態の所有者、DB制約、worker lifecycle、長時間I/O、backup復元、決定的hashを追跡した。主な結論は、retryをPublicationからJobへ移すこと、single workerを単一coordinator processとして定義すること、job ownerをDB FKで閉じること、Phase 1を1A〜1Eへ分割することだった。

この修正後は実装開始時に大きな設計判断をやり直す必要はない。初期並行数、drain timeout、SQLite busy timeout、spool上限等の運用値はPhase 1で計測して固定する。これらはarchitecture変更ではない。Provider初実装で契約が不自然と判明した場合、汎用field追加で隠さずX Adapterの具体例と一緒に設計差分をreviewする。

## Failure Matrix

`Retryable` と `Safe Auto Retry` は同義ではない。外部副作用が不明な操作は、provider障害が一時的でも自動再送しない。

| Failure | Local State | Possible Remote State | Safe Auto Retry? | Reconciliation | Human Action | Duplicate Risk |
| --- | --- | --- | --- | --- | --- | --- |
| validation failure | Failed / enqueue拒否 | なし | No。入力修正後は新しいvalidated attempt | 不要 | 入力/optionを修正 | None |
| auth failure / scope missing | NeedsAttention / ReauthRequired | なし、または既知handleが途中状態 | publishはNo。auth処理のみ可 | 既知handleがあればrefresh後status確認 | 再認証/権限付与 | Low。ただし401後publish盲再送はHigh |
| token expiry | Publication段階維持 / Refresh Job queued | 既存remote objectあり得る | refresh自体は条件付きYes。元操作はreplaySafety次第 | refresh後に元step/handle状態を再確認 | refresh token失効時は再認証 | Low〜High、元publishを再送すると上昇 |
| rate limit | Publication段階維持 / Job delayed | 通常は副作用なし。ただしresponse timing次第 | 読み取り/未送信と証明できる操作のみYes | Retry-After/reset後、必要ならstatus | 通常不要。quota/残高不足は対応 | Low。曖昧publishならHigh |
| provider 5xx before submissionと証明 | Publication段階維持 / Job delayed | なし | Yes、replaySafetyの許す操作のみ | 原則不要 | 繰返し時は診断 | Low |
| provider 5xx after possible submission | Unknown | 成功/失敗/processingのいずれもあり得る | **No** | operation-bound handle/status/read。なければInconclusive | Inconclusiveなら確認・attach・新規再投稿判断 | High |
| timeout before sendと証明 | Publication段階維持 / Job delayed | なし | Yes | 不要 | 通常不要 | Low |
| timeout / send有無不明 | Unknown | 未受理または成功済み | **No** | Provider reconcile。類似投稿検索だけでAbsent認定しない | 照合不能なら判断 | High |
| ambiguous success / response lost | Unknown | Published / Processing / Scheduled | **No** | remote ID/handle/statusを強い証拠で回収 | unresolvedならattachまたは明示的な新規再投稿 | High |
| crash before request / DispatchPrepared前 | Pending / Ready / Claimed recovery | なし | Yes | DB state確認 | 不要 | Low |
| crash after DispatchPrepared | Unknownまたはrecovery待ち | request未送信または成功済み | non-idempotent publishは**No** | checkpoint/handle/status | unresolvedなら判断 | High |
| crash after remote success、DB commit前 | Unknown | 成功済み | **No** | providerからreceipt/ID/status回収 | 回収不能なら判断 | High |
| DB failure after response | worker停止 / Unknown | 成功済み、processing、失敗のいずれも | **No** | DB回復後にremote照合 | storage修復、必要なら手動attach | High |
| worker duplicate | 片方のみlock取得。異常時はclaim競合 | 原則変化なし | 二重workerによる同一publishはNo | OS lock + generation CAS + Attempt確認 | hung ownerを明示停止 | Low。排他を破るとHigh |
| media processing failure | FailedまたはNeedsAttention | upload/container存在、公開なしまたはprovider拒否 | 新規publish自動retryはNo。既知handleのstatus readはYes | processing status / rejection reason | media修正後は新しい意図として再実行 | Medium |
| malformed provider response / API incompatibility | NeedsAttentionまたはUnknown | 副作用なし〜成功済みまで不明 | **No** if side effect possible | raw-safe fixture/known handle/status | adapter更新まで停止 | High |
| expired local schedule | Expired | 未送信 | **No** | 不要 | 新しい希望時刻・新idempotency keyで登録 | None from auto retry |

## 文書の整合性確認

README、architecture、provider contract、domain model、persistence、scheduling、security、testing、implementation plan、API research、capability matrix、CLI、analytics、design reviewを相互参照した。状態、時刻、冪等性、native/local責務、scope/gate、retention、partial successの語彙を照合した。

Provider固有endpoint/DTO/status/OAuth response/metrics fieldはProvider client/Adapter側へ閉じ、Application/Domainはcanonical intent、capability、step、receipt、error categoryだけを扱う。versioned wire DTO変更はprovider内部、共通意味が変わる時だけDomain contract変更を許す。

SQLiteはqueue claim、Attempt作成、receipt/remote ID/checkpoint保存を短いtransactionで処理し、外部HTTPをtransaction内へ保持しない。retry待機はJobだけが所有し、Publicationは公開workflow段階を保持する。WorkerRunとowner FKによりcrash後のclaim回復を一意にする。DB→HTTP→DBのfailure windowはUnknown/reconcileで扱う。単一PC向けにdistributed queue、broker、microservice、event sourcing、CQRS、plugin loader、Kubernetesは導入しない。

## 未解消の出荷gate

- G-X: Console単価・予算・account entitlement・endpoint最小scope、media categoryごとの現行size/duration等。
- G-YT: OAuth状態とupload auditを個別確認、project quota、保持条件。
- G-IG: login別scope、callback/redirect、固定Graph version、画像/Reels制約、TTL、local/resumable回復、media_publish曖昧結果の安全な照合/final Media ID復旧、metrics、rate、delete、token更新、保持条件のcanonical公式本文。
- G-TT: 本用途のDirect Post不適合。Display/Uploadも個別用途・審査の確認。
- G-RET: 各provider最新の保持・削除義務。未確認providerの収集は有効化しない。

これらは設計の隠れたTODOではなく、機能を有効化する明示条件。Phase 1のfake providerにはSNS側のgateが不要。

## 推奨する開始判断

**READY FOR PHASE 1。** Core + SQLite + Fake ProviderのPhase 1を開始できる。Phase 2以降は対応するgateが閉じた形式/権限だけを出荷する。要件が「必ず4媒体に無人公開」へ固定された場合は、実装を進める前に用途と公式に適合する提供形態を再検討する。ブラウザ代替や私用制限の回避は採用しない。

## PR全体の軽量監査への追記（2026-09-14）

監査対象head `bd34d066f17f6e8b0f26f454a2e2062d7fb63c6d` の全14文書を横断し、追加のMedium 2件 / Low 1件を確認した。これは上記の修正finding集計とは別の追加監査であり、公式APIの再調査・実装試験ではない。

| Severity | 指摘 | 仕様への反映 | 残る検証 |
| --- | --- | --- | --- |
| Medium | 公開JobがRefresh待ちのまま枠/lockを占有すると更新処理を妨げ得る | schedulingに依存待ち・許可解放・実行可能候補の契約、securityにmaintenance/grant/DBの順序を追加 | Phase 1C/1Dの共有grant・枠飽和テスト |
| Medium | maintenanceが通常CLIの書込やspool変更を排除する契約が不明確 | persistenceで全process共通の共有/排他gate、drain順序、Busy応答、旧DB handleとschema再検査を明記 | backup/migrate/restoreと別CLIの競合テスト |
| Low | AttemptにPublication必須と読める属性が残り非投稿Jobと不整合 | DomainのAttemptをJob経由の所有者参照に揃え、effect/replaySafety保存を明記 | Refresh/Stats/Purgeの保存・復旧とFK検証 |

3件とも設計上の指摘は反映済み。受入条件はtesting.mdへ追加した。動作保証は実装後の試験に残る。Phase 1開始可の判断とProvider別の出荷gateは変更しない。
