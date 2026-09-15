# Implementation plan

2026-09-15更新。Phase 0は設計成果、Phase 1はCore + SQLite + Fake Providerの実装成果。Phase 2以降は将来計画であり、credential発行・SNS投稿を実行する承認や完了記録ではない。

## Phase 0 — 設計と成立性

本PR: README、公式調査、ADR、CLI、Domain、Provider契約、scheduler、security、persistence、analytics、testing、設計監査。X/YouTube/Instagramは条件付きの実用MVP、TikTok Direct Postは本用途でpolicy-blockedとする。

完了条件は文書間整合、根拠と未確認事項の分離、failure recoveryの設計。app審査や本番動作が未完了でも設計結果として「制約あり」と結論できる。

## Phase 1 — Core + SQLite + fake provider

Phase 1を一括実装しない。各sliceはmainへ入れられる縦の動作単位とし、後続sliceが前の不変条件を壊さないことを確認する。

| Slice | 実装範囲 | 完了条件 |
| --- | --- | --- |
| 1A Foundation | **実装済み**: .NET 10 solution、Domain最小型、CLI JSON envelope、embedded migration runner、TimeProvider | build/test、schema version拒否、canonical intent golden test |
| 1B Durable publish | **実装済み**: manifest、素材固定、Post/Target/Publication/Job/Attempt、Fake Provider、DispatchPrepared→receipt→checkpoint | 同一key競合、公開応答喪失、Unknown、部分成功、content/options再ロード |
| 1C Worker recovery | **実装済み**: OS lock、WorkerRun、bounded dispatcher、priority、claim recovery、start/stop、Windows user task XML/readback | 二重worker、長時間処理中の新規due取得、graceful stop、再起動回復、`PT0S`定義 |
| 1D Secrets and auth | **実装済み**: Credential Manager MasterKeyStore、AES-GCM vault、Fake AuthGrant/rotation | secret marker非流出、grant refresh race、purpose binding |
| 1E Metrics and operations | **実装済み**: Raw/projection、retention、backup/restore quarantine、doctor/queue/status | metric意味差、期限超過、spool付きold backup、restore release拒否 |

1Aでは全provider用の空テーブルや空interfaceを先に量産しない。1BでPublication owner、1DでAuthGrant owner、1EでStats/DataDeletion ownerをmigration追加し、Jobの閉じたowner-kind契約を各sliceで拡張する。最初の実Provider前に抽象化を固定しすぎず、X Phaseで得た差分はprovider境界の範囲で契約へ反映する。

Phase 1全体の自動試験は `tests/PostRouter.Tests` に置く。enqueue冪等再試行・公開応答喪失・二重worker・長時間処理競合・refresh race・claim再起動回復・old backup quarantineを検証する。Fakeを公開済みとみなす本番profileは存在しない。SNS API keyやdeveloper accountは不要。Windows Task Schedulerへの実登録、ログオン後再起動、Credential Managerの実機確認はWindows受入試験として別途行う。

人間が用意するもの:
- Windows 11等の.NET 10対応PCと通常ユーザーprofile。task/vaultの確認に使えるログオン環境。
- Gitと.NET 10 SDK、依存package取得が可能な開発環境。具体的patch/package versionは開始時にpin。
- 秘密を含まない短いMP4、JPEG、UTF-8本文のfixture、spool用の空き容量。
- 既定timezone（推奨Asia/Tokyoか実利用地域）、遅延15分policy、保存期間の確認。
- 非ログオン時の実行が必須かの判断。不要なら追加password-logon設定なし。

このPhaseでSNS credential、公開domain、object storage、Google auditは要求しない。ffprobe採用時は配布元/版/licenseを固定するが、動画render機能は作らない。

## Phase 2 — X

Phase 2A実装済み（2026-09-15、実credential受入は未実施）: OAuth PKCE、安全なtoken保存/refresh/revoke、account disconnect/reset/reconnect、text-only create、Post ID保存、Unknown安全策。画像・動画・delete・metricsはPhase 2B以降へ残す。[Phase 2A記録](phase2a-x.md)

理由: text/image/videoの3形式を一つのproviderで検証でき、canonical形式差と非冪等createの回復を早期に評価できる。費用を管理した小さいpilotに限定する。

内容: Native OAuth PKCE、media v2、通常Post、delete、local scheduling、owned/public metrics、rate budget。G-XとG-RETを閉じ、endpoint/account別の制約をcapabilityに反映する。

人間の準備: Developer app、利用目的/契約確認、client ID、固定redirect登録、本人accountの同意、Consoleのcreditと上限予算。秘密入力はvault経由。live試験は別途人間が実行を選択する。

合格条件: 3形式のvalidationと小規模の本人操作試験、lost Post responseのUnknown表示、費用上限、30日内指標window、revoke/purge。長文、thread、GIF、広告APIは見送る。

## Phase 3 — YouTube / Shorts

理由: 優先目的の同じ動画配信とnative schedulingを追加する。private resumable uploadはfake段階から契約を想定しておき、このPhaseで実APIへ接続する。

内容: Desktop OAuth、resumable private insert、video ID保存、既知IDの公開/予約変更、status、delete、Data基本値＋owner Analytics、definition version/retention。Google.Apis.Authのvault接続とtoken更新競合を検証する。

人間の準備: Google Cloud project、YouTube Data/Analytics有効化、Desktop OAuth client、test channel、必要scope consent、quota、外部配布ならOAuth検証。public uploadを使うには別のupload auditを解消する。

合格条件: G-YT/G-RET、upload session復旧、native予約とlocalの排他、過去publishAtの誤公開防止、Shorts分類は保証としない表示、規約に沿う履歴保持。未auditならprivate試験まででpublic/native公開は出荷しない。Community text/image、収益分析、独自派生scoreは見送る。

## Phase 4 — Instagram

理由: account条件、callback、媒体upload、媒体別insightsが追加される。先行Phaseのqueue/ID回復を流用し、独立した外部条件を解消してから実装する。

**Phase開始gate:** G-IGのcanonical公式本文とMeta App Dashboardを確認し、正確なGraph version、login別scope、callback/redirect方式、token更新、JPEG/Reels上限、container TTL、local/resumableまたはURL uploadの契約、`media_publish`曖昧結果の照合/final Media ID復旧、rate limit、metrics、delete、商用開示・保持条件を調査書へ追記する。未確認の旧数値・旧flowを仮実装してreleaseしない。

内容: 確認済みInstagram Login flow、確認済みcallback/redirect方式、確認済みtoken更新、JPEG単体/Reels、必要な場合だけmedia staging、container→publish、local schedule、確認済みInsights。Facebook LoginやPersonal対応を同時に追加しない。固定HTTPS relayやobject storageを公式条件確認前にPhase 4の必須構成として先行実装しない。

人間の準備: Professional Business/Creator account、Meta appとrole/access。callback、公開domain、media staging、App Reviewの要否はG-IGの確認結果に従って準備する。秘密は本人vault。

合格条件: G-IG/G-RETが閉じたcapabilityだけ有効。`media_publish` response喪失時は確認済みのoperation-bound証拠で安全に照合できる場合だけ回復し、そうでなければUnknown/NeedsAttentionのまま自動再投稿・新container作成をしない。必要なstaging cleanup、token renewal/再認証、Windows taskからの動作を確認する。carousel/Stories、商用開示未確認形式、DELETEは各gate確認後の機能とする。

## Phase 5 — TikTokの限定的対応判断

Direct Postを自分用CLIの通常機能として実装するPhaseではない。policy-blocked descriptorはPhase 1からあり、Phase 5でDisplay APIによる許可された本人public動画の基本stats、またはUpload inboxという手動完了workflowを別々に審査する。

人間の準備: 利用目的に適合するapp/製品の承認、Login Kit設定、必要scope、本人同意。Photo経路が必要ならverified domain。Direct Post再評価には本用途への公式適合の根拠とUX要件の解決が必要。

条件が成立しなければblockedを維持して終了する。4媒体一括指定時に黙ってTikTokだけ別サービスやブラウザへ迂回しない。Displayが利用できてもwatch timeや公開予約が増えるわけではない。

## 実用MVPの境界

| 入る | 条件/対象 |
| --- | --- |
| text-only | X |
| image + text | X、確認済みIG JPEG単体 |
| video + text | X、YouTube、IG Reelsの確認済みprofile |
| bulk enqueue | validationはatomic、公開は対象別部分成功 |
| schedule | YT native、X/IG local、recovery/遅延/不明状態の可視化 |
| analytics | API提供値のprovider別比較、Rawとversioned projection、retention |
| account / queue / doctor | 複数accountを阻害しないidentity、vault、予算・権限診断 |

見送るもの: 全4媒体への無条件無人公開、TikTok Direct Post、PC停止中のlocal実行保証、global exactly-once、画像/動画の自動変換、thread/carousel/Stories、browser投稿代替、全指標共通化、独自マーケティングscore、webhook server、Windows Service、複数PC共有queue、distributed broker。

## 次の変更が必要になった時

provider追加は独立Adapterとoptions/checkpoint/metric catalog、composition root登録、fixture、調査更新。native機能追加はoptional port。未知の共通意味だけDomain versionを上げる。

常時稼働の要求が単一PCを超えた時にだけworker hostの移設を別ADRで検討する。DBを同期folderに置くだけで分散化しない。shorts-cliには変更を要求しない。
