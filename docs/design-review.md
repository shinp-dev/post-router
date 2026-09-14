# Final design review

監査日: 2026-09-14。対象は本PRの設計文書。実装・本番API操作・OAuth発行・動作試験は行っていない。

## 判定

**条件付きの設計として採用可能。4媒体すべてへの自分用CLIによる無人公開という要望は現条件では不成立。**

実装できると断言して未確認仕様を埋めるのではなく、Capabilitiesとrelease gateに外部条件を反映した。X/YouTube/Instagramの段階的MVPを推奨し、TikTok Direct Postはblocked。Instagramの本文未確認事項はG-IGに集約し、公開機能のrelease条件とした。

## 指摘と反映結果

| 観点・監査で見つけた問題 | 最終設計への修正・確認 | 残留risk / 判定 |
| --- | --- | --- |
| Provider abstractionへendpoint/status/scopeが漏れる | Domainに持たずAdapter client/DTOへ隔離。optional portsとversioned options | 新しい共通の意味が必要ならcore変更はあり得る |
| 最小限だけに揃えて固有機能を失う | YouTube madeForKids/Shorts、IG placement、TikTok consent等をtyped optionsで保持 | 未確認optionはenabledにしない |
| SDK responseをdomainとして保存してしまう | wire DTO→意味変換→Canonical、raw/checkpointは別領域 | Adapter契約testが出荷条件 |
| API version固定で意味も不変と思い込む | definitionVersion、mappingVersion、checkpoint schemaを分離 | 公式changelogの継続確認が必要 |
| publishを汎用retryする | MayPublish/NotReplayableとUnknownを追加。read照合のみ自動 | exactly-onceは保証せず、未公開で止まる可能性 |
| uploadを全て無害とみなす | TikTok最終chunk/URL initを公開境界に分類 | 将来対応時も必須contract |
| adapter内の複数HTTP間でreceiptが失われる | 1 executeStepに1durable境界、handle保存後に次操作 | response後commit前の不確実性は残る |
| lease満了で二重workerが起きる | OS lock保持＋DB CAS、時間だけでtakeoverしない | hung processは人間が停止し照合 |
| native予約失敗時のlocal fallbackが二重投稿になる | executionMode固定、既知video ID照合、無断切替禁止 | provider受理後の電源OFFでも公開は進み得る |
| 過去publishAtによる即公開 | upload見積＋最低5分余裕、送信前再検証、余裕不足は停止 | network受信時刻の厳密制御は不可 |
| PC再起動で予約が消える/遅れて勝手に公開 | SQLite queue、同一user task、ログオン後recover、15分late policy | ログオン前・電源OFFのlocal実行は既定保証外 |
| cancelとdispatchのrace | CancelRequestedとremote確認を分離 | 取消要求だけで取消成功とは言えない |
| 古いbackupでPublishedがPendingへ戻る | restore quarantineで過去Pendingも自動送信禁止 | 失ったreceiptは人間/remote照合が必要 |
| token refreshがaccount単位lockで競合 | AuthGrant単位OS lock、DB generationとencrypted token同時commit | rotation response喪失時は再認証 |
| 即時投稿のretryごとに現在時刻がhashへ入りConflict | Immediateを意味としてhash、初回dueAtだけ保存 | 同じkey別内容は意図通りConflict |
| 予約編集のコマンドと契約が曖昧 | MVPはin-place editなし。取消確認＋新規key | Revision modelは将来用に保持 |
| Raw無期限保存がprovider規約と衝突 | retention class、再認可、期限処理、export/backup削除 | 長期停止中の期限処理は利用者運用が必要 |
| Normalizedが指標意味差・派生規約を潰す | provider値・単位・subject/periodを維持、独自ratio/scoreなし | 同名viewsも同値比較と断言しない |
| stats失敗で投稿成功までFailedになる | StatsSyncRunをPublicationから独立 | analyticsの遅延/欠測は残る |
| Secretsがlog/config/raw/backupへ漏れる | allowlist、vault、機密checkpoint暗号化、backup範囲を明示 | 本文等は通常DBにあり同一user侵害は防げない |
| CLI callbackを全社共通loopbackと誤認 | Instagramの固定HTTPS relayを条件化、他社は各登録規則 | IG追加運用とG-IG確認が必要 |
| migrationで旧checkpointを新規投稿に変える | version拒否・互換reader・backup/quarantine | 自動down migrationなし |
| 各SNS公式API上でMVPが成立するか | gate別に判定。TTを審査さえすれば使えるとは書かない | 全4媒体無条件MVPは不可 |
| 過剰設計か | 単一binary/DB/worker、broker/service/webhookなし | vault/Unknownは必要な複雑性として維持 |

## 文書の整合性確認

必須10成果物に加え、技術選定・CLI・Analytics・監査を独立文書として整理した。全ファイルはMarkdownのみ。内部相対リンクの解決、code fenceの対応、manifest JSONの構文、必要文書の存在を機械的に確認する。実装のunit/integration/live testは未実施であり、設計書の検査と区別する。

状態・冪等性・native/local責務・scope/gate・retentionの相互参照を手動照合した。API調査の本文確認と検索確認を区別し、未確認制限値を実装可能な定数へ昇格していない。

## 未解消の出荷gate

- G-X: Console単価・予算・account entitlement・endpoint最小scope。
- G-YT: OAuth状態とupload auditを個別確認、project quota、保持条件。
- G-IG: 正式version/scope/callback、画像/Reels制約、TTL、resumable回復、metrics、rate/保持条件の公式本文。
- G-TT: 本用途のDirect Post不適合。Display/Uploadも個別用途・審査の確認。
- G-RET: 各provider最新の保持・削除義務。未確認providerの収集は有効化しない。

これらは設計の隠れたTODOではなく、機能を有効化する明示条件。Phase 1のfake providerにはSNS側のgateが不要。

## 推奨する開始判断

Phase 1を開始できる。Phase 2以降は対応するgateが閉じた形式/権限だけを出荷する。要件が「必ず4媒体に無人公開」へ固定された場合は、実装を進める前に用途と公式に適合する提供形態を再検討する。ブラウザ代替や私用制限の回避は採用しない。
