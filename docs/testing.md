# Testing strategy

設計 v1 / 2026-09-14。本PRは文書のみであり、以下の実装テストを実行済みとは報告しない。

## テストの層

| 層 | 対象 | 方法・合格条件 |
| --- | --- | --- |
| Domain unit | state、idempotency、時刻、value status | network/DBなし。禁止遷移、同一key差分、DSTを検証 |
| Application + fake provider | durable dispatch、recovery、部分成功 | FakeClock/TimeProviderと決定的providerで副作用回数を観測 |
| SQLite integration | transaction、CAS、unique、migration、backup | 実SQLite・一時directory・複数process |
| Provider contract | DTO/endpoint/headers/error/metrics | local fake HTTP server、sanitized fixtures、全adapter共通の障害matrix |
| Windows acceptance | vault、task、再起動、権限 | Windows VM/実機。同一user、ログオン/非ログオンprofileを分ける |
| Optional live contract | 現行APIとの互換 | 人間管理の専用account/app、個別予算・明示実行。通常CIでは送信しない |

xUnit、.NET TimeProvider、ASP.NET Core/Kestrelのloopback fake serverを採用する。fake serverはtest project内のみ。HTTP mocking libraryのメモリ応答だけではTCP切断やchunk offsetの問題を再現できないため、socket切断可能なfixtureも持つ。再現に固定sleepを多用せず、barrier/clock advance/明示signalを使う。

## 必須障害matrix

| Scenario | 注入位置 | 合格条件 |
| --- | --- | --- |
| timeout before connection | 送信前と証明できる接続失敗 | policyに従いretry。duplicateなし |
| timeout after server commit | 公開は成功、responseを切断 | Unknown。自動publish再送0回、照合へ |
| 2xx malformed JSON / missing ID | 公開成功responseの破損 | PublishedにせずUnknown。safe診断 |
| 429 / rate headers | 複数account・同じapp bucket | reset前に再送なし、jitter、quota区別 |
| 5xx provider outage | readと非冪等publishそれぞれ | readはbackoff、publishは副作用の確実性で分類 |
| token expired | 準備時と公開request後 | grant refresh直列化、元publishの安全性を維持 |
| rotating refresh crash | response後DB commit前 | 古いtoken無限retryなし、ReauthRequired |
| duplicate CLI | 同じkeyを同時enqueue | Post/対象jobは1組。同じkey別意図はConflict |
| duplicate worker | 2process、旧process suspend | 1つだけlock取得。lease時刻でtakeoverしない |
| crash before dispatch commit | persist前 | 復旧後に通常実行可能 |
| crash after Prepared | HTTP前/後を区別不能にする | 非冪等操作の盲目的再送なし |
| crash after receipt commit | 次job作成後 | 保存handleから再開。最初のcreateを繰返さない |
| disk full / IO error | response受領直後のDB commit | 後続HTTP停止。結果不明を復旧時に照合 |
| private uploaded | YouTube insert成功 | public要求をPublishedにしない |
| native scheduled + restart | publishAt受理後 | local公開jobを生成しない |
| native response lost | 予約設定のresponse切断 | 同一video IDで照会、local fallbackなし |
| expired upload handle | X/IG/YT個別 | expiryを不存在の証明にしない |
| IG media_publish response lost | 公開request後にresponse切断 | G-IGで確認した強い照合証拠がなければUnknown/NeedsAttention。新container作成・再publishなし |
| TikTok final chunk | upload最終chunk後切断 | 将来adapterでもMayPublish/Unknownとして扱う |
| TikTok policy gate | 本プロジェクトprofile | 公開init/transferへのHTTPが0回 |
| cancelled while dispatching | cancelと公開step競合 | 成否を照合、取消成功の誤表示なし |
| timezone / DST / clock jump | gap/fold/前後補正 | 不正時刻拒否、二重dueなし、late policy適用 |
| PC restart / overdue | deadline内/外・ログオン前後 | 内は再開、未送信で外はExpired。受理済みnativeを取消しない |
| retry expired | Expired publicationにCLI retry | remote HTTP 0回。新しい希望時刻・新しいkeyのPostを要求 |
| old backup restore | receipt欠落 | 全送信quarantine。旧Pending自動再送0回 |
| malformed metric / unknown enum | data type・必須field変更 | raw隔離、誤った0や共通指標生成なし |
| retention deadline offline | 停止後に再起動 | data利用前に期限処理、backup/exportにも削除 |
| partial batch | X成功・YT失敗等 | 成功対象を再投稿/自動削除しない |

「exactly-onceテスト合格」とは呼ばない。fake provider側の副作用履歴を観測し、曖昧操作を本ツールが再送していないこと、回復可能なhandleを再利用していることを確認する。API自体の二重処理まで証明するものではない。

## Provider mock / fixtures

Fake providerはcapabilities、dynamic constraint、nativeあり/なし、processing delay、拒否、lost response、remote ID回復可/不可を設定できる。ただの成功固定mockを回復テストに使わない。

各API versionの最小response、未知追加field、必須field欠落、数値→文字列、null、巨大ID、未知status、pagination重複、空結果、partial queryをfixture化する。fixtureには公式URL・確認日・対象versionを添える。recorded fixtureを作る場合はtoken・署名URL・本人情報・本文をsynthetic値に置換し、retention条件を満たす。secret入りrecordingをcommitしない。

Provider clientのAPI response型をDomainへ公開していないことを参照依存のarchitecture testで検査する。対象provider project以外にAPI host/scope/DTO型が出たらreview対象とする。ただしドキュメント・test fixture・composition rootの名前は許容する。

## Analytics test

- 同じcanonicalKeyでも異なるsubject/period/definitionを同値として集約しない。
- lifetime countを複数snapshotにわたって足さない。
- 0、missing、unsupported、permission denied、not yet availableを区別する。
- X同じmediaの複数Post、YT channelとvideo filter、IG account指標を検証する。
- rawからmapping v1/v2を再生成し、原値とpolicy期限を保持する。
- YouTube独自ratio/scoreが出力経路に存在しないことを検査する。
- providerの定義変更前後に境界を表示する。
- retained dataの再認可/存在check失敗、ユーザー削除、backup復元時の再出現を検査する。

## Security / Windows

shell metacharacters、空白・日本語path、先頭dash、device path、UNC、reparse point、偽拡張子、oversized stream、probe timeoutを入力する。子processのargvを観測し、文字列がcommandとして実行されないことを確認する。

ログ/JSON/error/diagnostic bundleへseedしたsecret markerが1つも出ないテストを行う。HTTP handlerのdebug logや例外ToStringを含める。OAuth state不一致、callback再送、port占有、flow timeout、relayの無認証取得を拒否する。

Windows taskはログオン済み、lock画面、再起動後ログオン、本人設定した非ログオンtaskを別caseとして検証する。vaultアクセス不能で平文fallbackしない。異なるSID/SYSTEMでのworker起動をdoctorが説明できること。特権なしで標準profileが動くことを合格条件とする。

## Migration / release gates

直前の出荷schemaと代表的な旧checkpointからのupgrade、途中中断rollback、checksum変更検出、新schemaに旧binaryを当てた拒否を試験する。backupからのrestoreは同一profileで復号とhashを確認しquarantine解除まで実施する。

CIはWindowsを必須、Domain/SQLite/HTTP契約はLinuxも実行してOS差を早期検知する。SNS tokenを通常CIへ入れない。定期live投稿を仕様監視botとして実装しない。公式changelogをreleaseごとに確認し、影響するadapterだけのcontractを更新する。

出荷判定は重要な不変条件と障害matrix、実際のapp権限/gateの確認で行う。coverage数値だけを合格条件にしない。