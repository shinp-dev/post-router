# CLI contract

提案仕様 v1 / 2026-09-14。コマンド名は `pub`、project/repository名はpost-router。名前衝突時は配布名post-router、alias pubとする。

## コマンド

| コマンド | 意味・副作用 |
| --- | --- |
| account add PROVIDER --name ALIAS | app設定とユーザー認証。秘密入力はmasked promptまたはprivate stdin |
| account list / account show ALIAS | 有効権限・期限・gateを表示。tokenは出さない |
| account login ALIAS | 再認証。既存accountのremote identityは勝手に変更しない |
| account remove ALIAS | local解除、将来job停止、保持データ削除方針を適用。remote投稿は消さない |
| provider capabilities ALIAS --refresh | 対象account/content形式の能力。refreshなしはキャッシュ日時表示 |
| config show / config set KEY VALUE | allowlistされた非秘密設定のみ |
| plan --file FILE | ローカル検証とresolved対象表示。SNSへ素材を送らない |
| plan --file FILE --online | 追加でscope・能力のread確認。費用が生じ得るreadも表示 |
| post [inline options] / post --file FILE | 全対象preflight、素材固定、queue登録 |
| post status ID | 保存状態表示。--refreshで照合jobを依頼 |
| post cancel ID --target ALIAS | 予約取消要求。公開済み削除とは別 |
| post retry ID --target ALIAS | 確実に未公開のFailedを、元のscheduleがまだ有効な場合だけ明示的に再開。Unknown/Expiredには無効 |
| post reconcile ID --target ALIAS | 成否照合。確認不足ならNeedsAttentionのまま |
| post attach ID --target ALIAS --remote-id ID | 自分の既存投稿をAPIで所有者検証して関連付ける |
| post duplicate ID --idempotency-key KEY | 再投稿という新しい意図。Unknownが残る対象では明示的リスク受領を要求 |
| post delete ID --target ALIAS --confirm | capabilityがある場合のみremote削除。MVPはX/YouTube |
| queue [--state STATE] | 次回時刻、遅延、worker状態、要対応 |
| stats sync [--post ID] | 永続stats jobを作る。結果が揃うまで待つ--waitを選択可能 |
| stats show --post ID | 保存済みmetrics。同期は暗黙実行しない |
| stats export --format jsonl | provider名・定義・期間付きexport |
| worker run / worker once | 排他を獲得してjob処理。onceは現在dueな有限batch |
| worker install / start / stop / status / uninstall | 自ユーザーOS task登録、起動、graceful drain停止、確認、解除。uninstallだけで実行中processを放置しない |
| doctor [--online] | DB・vault・素材・clock・worker・accountの診断。投稿はしない |
| db backup / db check / db migrate | maintenance drainと排他下で管理 |
| db restore FILE / db restore status / db restore suppress ID / db restore release | 復元後は全送信jobをquarantine。照合・attach、または不明対象の送信抑止後に安全なjobだけ解放 |
| data purge --account ALIAS | ローカルprovider由来データ削除。再収集するには明示再開 |

## 共通投稿入力

`--text` またはUTF-8 `--text-file`、`--image`（複数指定可）、`--video`（1本）、`--title`、`--to`、`--visibility`、`--at`、`--tz`、`--options-file`、`--idempotency-key`。imagesとvideoを同時指定する混在投稿はMVPで拒否する。音声のみ、thread、carouselは将来拡張。

`--at` はAPI request開始時刻ではなく**希望公開時刻**。native schedulingでは準備/upload/予約登録をそれより前に行ってproviderへ同じ希望時刻を渡し、local schedulingではその時刻より前に最終publish requestを送らない。provider processingやnetworkにより実際の公開完了は遅れ得る。この意味はProviderによって変えない。[予約・復旧](scheduling.md)

textはX本文、IG caption、TikTok caption、YouTube descriptionへAdapterがmappingする。YouTube titleをtextから勝手に生成しない。YouTubeの公開範囲・子ども向け設定・必要な開示を入力しない場合は拒否する。Instagramはaccount公開設定に依存し、providerに存在しないprivate指定は拒否する。

### Manifest例

```json
{
  "schemaVersion": 1,
  "clientRequestId": "release-20260915-a",
  "content": {
    "text": "新しい作品を公開しました。",
    "title": "作品紹介",
    "video": "./out.mp4"
  },
  "schedule": {
    "at": "2026-09-15T20:00:00+09:00",
    "timeZone": "Asia/Tokyo",
    "maxLatenessSeconds": 900
  },
  "targets": [
    {"account": "x-main", "visibility": "public"},
    {
      "account": "yt-main",
      "visibility": "public",
      "optionsVersion": 1,
      "options": {
        "madeForKids": false,
        "containsSyntheticMedia": false,
        "hasPaidProductPlacement": false,
        "categoryId": "20",
        "formatIntent": "short"
      }
    },
    {
      "account": "ig-main",
      "visibility": "account-default",
      "optionsVersion": 1,
      "options": {"placement": "reel", "shareToFeed": true}
    }
  ]
}
```

例の開示値は利用者の判断を代行する既定値ではない。optionsはEndpoint名ではなくAdapter所有の安定した入力schema。unknown fieldを無視せずvalidation errorにする。schemaには秘密値を持てない。

## 決定性

- `--to all` はenqueue時点で有効な登録account一覧へ固定し、実行時に再展開しない。blocked対象を黙って除外しない。
- 標準は全対象のvalidation成功をenqueue条件とする。`--allow-partial` は明示選択時だけ。skip理由を記録する。
- enqueueはatomic、SNS公開はatomicではない。成功した対象を他対象の失敗で削除しない。
- noninteractiveのpostはclientRequestId必須。対話時は生成IDを表示する。同じキー＋同じ内容は同じPostを返し、同じキー＋異なる内容はConflict。
- 同じ動画でも異なる日時の投稿は別の意図。file hash単独で重複排除しない。
- `--at "20:00"` は日付不明のためMVPでは拒否。日付＋timezone、またはoffset付きISO 8601を使う。
- Expiredは「元の希望公開時刻を過ぎ、許容遅延も超えた」状態なので `post retry` で遅れて公開しない。公開したい場合は新しい希望時刻・新しいidempotency keyのPostとして登録する。
- enqueue後の入力はimmutable。MVPにin-place予約編集コマンドは設けない。未送信の取消確認後、新しいkeyで登録する。将来の編集は新revisionとし、native予約済みならprovider取消/更新の確認を必要とする。
- 保存成功後にCLI応答を失っても、同じidempotency keyで復帰する。
- post既定はqueue登録で終了。worker稼働中なら処理される。`--wait` は状態を待つだけで、既定timeout後もqueueを取消さない。worker不在は即座に検出して永続化した上でwarningを返し、無期限に待たない。実行済みとも表示しない。

## AI向け出力

`--json` はstdoutに1つのJSON envelope、診断はstderr。schemaVersion、requestId、result、warnings、errorを固定する。IDは常に文字列、日時はoffset付きISO、unknown値はnullとreason。秘密・生HTTP・traceを含めない。非対話中にpromptしない。

| exit | 意味 |
| --- | --- |
| 0 | コマンド成功。enqueueの場合は保存成功であり公開成功ではない |
| 2 | 入力・非対応capability |
| 3 | 認証/審査/設定上の対応が必要 |
| 4 | 一時障害・read失敗。publish再試行可否はresultの状態を確認 |
| 5 | 部分失敗/一部skip（--wait終了時等） |
| 6 | Unknown / NeedsAttention |
| 7 | DB・排他・migration・保存失敗 |
| 130 | CLI待機の取消。既にenqueueされた予約は残る |

`doctor --online` のreadはquota/budgetの対象。利用できない1媒体で他媒体を壊さない。端末上の宣言だけで審査済みにできる無制限のoverrideは提供しない。

## Options fileと即時投稿の補足

--options-fileはschemaVersionとaccounts（解決対象aliasをkeyにしたoptionsVersion/optionsのmap）を持つ。manifestとinlineの混在は拒否し、共通flagとprovider optionsの同一意味の競合も拒否する。options-fileはvisibilityを上書きしない。異なるvisibilityが必要ならmanifestを使う。例のYouTube optionsにはmadeForKids、containsSyntheticMedia、hasPaidProductPlacement等を本人が明示する。

--at省略はImmediateという意図。初回enqueue時刻をSchedule.dueAtUtc（希望公開時刻）にし、初回Jobも即時dueにするが、再入力ごとの現在時刻をidempotency digestへ含めない。同じkeyの即時投稿を再実行しても既存Postへ戻る。
