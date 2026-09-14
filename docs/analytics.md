# Analytics and metric semantics

設計 v1 / 2026-09-14。目的は各媒体が返す結果を正確に並べて見られること。異なる意味の数値から単一スコアを作ることではない。

## RawとNormalized

RawMetricResponseは必要なmetrics queryのresponseを保持する。provider、API version、adapter version、取得時刻、query template ID、要求fields、subject、期間、timezone、dimensions、pagination、response hash、retention policyを添える。secret/token/任意profile全文を集めない。未知metric fieldは許される範囲でrawに保持する。

Normalizedはversion付きprojection。canonicalKeyは表示・検索用の緩い分類で、同じキーでも比較可能性を保証しない。providerMetricKey、definitionVersion、unit、subjectKind、timeBasis、aggregation、qualifiers、mappingVersionを必須とする。元値と単位を維持し、別の指標に見える換算や推定を既定で行わない。

| 項目 | 例・意味 |
| --- | --- |
| subjectKind / subjectRef | post、media、video、account。remote objectを参照 |
| providerMetricKey | X public_metrics.impression_count等の出典識別子 |
| canonicalKey | exposure.impressions、engagement.likes等の表示分類 |
| value / unit | 精度を失わない数値、count/seconds/minutes/milliseconds/percent |
| valueStatus | Available、Unsupported、NotAuthorized、NotYetAvailable、NotReturned、Redacted、Error |
| timeBasis | LifetimeAsOf、Period、PointInTime |
| period / retrievedAt | 対象期間と回収時刻は別 |
| qualifiers | organic/paid、unique/total、surface、video format、filtered scope |
| definitionVersion | provider側定義が変わった時点を識別 |
| comparability | ProviderOnly / QualifiedSideBySide。全SNSComparableのdefaultなし |

nullと0を混同しない。空配列、権限不足、未集計、削除済みを別に表示する。lifetime snapshotを日次イベント数としてSUMしない。複数動画で重複視聴があり得るreachを足してunique reachとしない。

## 指標catalogと表示

[公式調査のmetrics表](api-capability-research.md)が取得可能性の根拠。以下は表示上の注意。

| 候補 | 扱い |
| --- | --- |
| views | X media view、TikTok video view、YouTube view、Instagram viewsを別定義で並べる |
| reach / impressions | unique reachと表示回数を別分類。X media viewをpost impressionsへ変換しない |
| likes / comments | 同名でも対象・可視範囲・削除反映・期間が異なるのでqualifierを付ける |
| shares | X repost/quoteは別、YouTube Share buttonのsharesと同値扱いしない |
| saves | X bookmark、IG saved、YouTube playlist追加は別。TikTok未取得を0にしない |
| watch time | providerが返すtotalを単位付きで保持。TikTok標準Displayには期待しない |
| average watch duration | provider返却値をそのまま保持。平均の単純平均や動画長との比を作らない |
| completion rate | 標準共通指標なし。YT averageViewPercentage、X quartileをcompletionと改名しない |
| profile visits | account指標とpost由来clickを別に表示。各投稿に割り振らない |
| followers/subscribers | accountの残高、増減のreportを分ける。前後差を投稿起因の増加と断定しない |

YouTube viewsの2026年8月の定義変更、Shortsの従来のengagedViewsとの差をcatalogのdefinitionVersionへ記録する。古いrawを新Normalizerへ通しても、過去のprovider計測そのものを新定義で再測定できるわけではない。[YouTube改訂履歴](https://developers.google.com/youtube/v3/revision_history)

YouTube API dataによる独自派生metricsには規約上の制約があるため、MVPは名称整理・型整理・provider値の表示に限定する。engagement rate、独自completion、総合score、net subscriber計算、複数SNS合算の到達数を生成しない。公式APIが返す計算済み指標は、その名称・単位・定義のまま扱う。[YouTube Developer Policies](https://developers.google.com/youtube/terms/developer-policies)

## 収集jobと遅延

公開確認後に初期policyとして1時間、6時間、24時間、3日、7日、28日を予約する。これは製品側defaultであり、SNSがその時刻に全指標を返す保証ではない。API予算・rate limit・媒体の取得可能期間で調整する。即時stats syncは同じcollector jobをenqueueし、外部API料金と対象範囲をplanで示す。

YouTube Analyticsの期間reportは日付・timezone・dimensionの公式組合せでqueryする。データが確定している終了日を保存し、要求終了日まで得られたと偽らない。直近日を後日再取得して別snapshotを作り、latest projectionを選ぶ。データ遅延中のData API lifetime countとAnalytics日次値を同じ表の同じ期間として混ぜない。

X own non-public等の取得可能windowを超えたjobはUnsupportedWindowで停止し、APIに無限requestしない。TikTokのpublic post IDがまだ得られない場合はID回収を待つ。IG metricsのmedia_type/期間/廃止条件はadapter catalogで検査する。

収集失敗はPublicationのPublished状態をFailedに変えない。StatsSyncRunはRequested / Running / Partial / Complete / Failedを別に持つ。quotaは投稿の準備を優先し、stats大量backfillがdue投稿を妨げない。収集粒度はAPIが許すbatchを用い、budget超過なら延期する。

## Rawからの再解釈

raw→decode→provider catalog validation→normalized projectionを独立処理として再実行できるようにする。mapping versionごとにprojectionを追加し、CLIのdefaultは採用済みversion。差分をreviewして切替える。reprocessはremote APIへ接続せず、rawが残っている期間だけ可能。

required field欠落、未知enum、型変更をcontract driftとして記録する。未知の数値fieldを自動で既存canonicalKeyへ割当てない。historical fixtureには取得時のAPI/adapter/definition versionを残す。raw hashは同一payloadの確認用であり、別取得時刻を消してよい理由ではない。

## 保持・再認可・削除

Raw永続保存と「いつでも無期限に再解釈」は同義ではない。provider規約と本人の保存目的が優先される。

| データ | 設計policy |
| --- | --- |
| YouTubeの適切にauthorizedなAnalytics/Reporting/statistics | 必要な履歴は保持可。ただし少なくとも30日ごとに認可継続と対象video存在を確認 |
| その他のYouTube authorized metadata、未認可の限定許容data | 対象規約に従い30日以内にrefreshまたはdelete。全data無期限許可とは解釈しない |
| YouTube利用者からの削除要求 | 7日以内という規約上限より早く処理し、managed backups/exportも対象 |
| 認可取消・account disconnect | 直ちに取得停止とlocal purgeを開始。YouTubeの取消後最大30日の上限を待つ運用にしない |
| X / TikTok / Instagram | 出荷時の該当developer terms・削除義務をG-RETで確認。MVP default raw30日、normalizedも同じpolicyに従う |

YouTube例外を他providerへ一般化しない。保存目的がないprofile/comment本文等は取得対象外。X削除/保護状態の変化なども利用条件に応じてdata削除へ反映する。正確な各社retention規約の確認は各adapter公開のblocking gateであり、設計上30日を選んだだけで全社準拠済みとはしない。[公式保存条件](https://developers.google.com/youtube/terms/developer-policies)

retention engineはgrant/subjectごとにnextPolicyCheckAtを永続化する。長期保持を許すcheckが期限切れならデータ利用を止め、再確認できなければ削除対象にする。長期オフライン前はpurgeする運用が必要。PC電源OFF中に処理できない限界を明記し、再起動時はまず期限処理を実行する。

exportにはprovider、definitionVersion、mappingVersion、retrievedAt、period、retention期限、欠測理由を含める。裸のlikes/views列だけのCSVを既定にしない。MVPの機械可読形式はJSON/JSONL。managed exportは追跡削除し、管理外コピーの期限管理は利用者が行う。
