# Phase 2C — X media posting

確認日: 2026-09-15。

## Status summary

- X text posting: 実装済み
- X JPEG image posting: 実装済み
- X native video posting: **Pending**

公式X API v2は`POST /2/media/upload`で画像をuploadし、返却されたmedia IDを`POST /2/tweets`の`media.media_ids`へ渡す。Postには写真最大4枚、GIFまたは動画は1つを添付できる。画像上限は1ファイル5 MB。動画はすべてchunked uploadが必要で、upload成功後もaccount entitlementによりPost作成が拒否され得る。

参照:

- https://docs.x.com/x-api/media/introduction
- https://docs.x.com/x-api/media/upload-media
- https://docs.x.com/x-api/media/quickstart/media-upload-chunked

## Phase 2C-1

JPEG 1〜4枚を対象とする。現段階では本文1〜280 Unicode scalar valuesを必須とし、media-only Postはまだ公開しない。

各upload完了時にmedia IDと`expires_after_secs`から算出した有効期限を暗号化checkpointへ保存し、次のworker実行で未upload画像だけを送る。media IDが期限切れまたは1分以内に期限切れとなる場合はPost作成へ進まず、画像uploadを先頭から安全に再構築する。旧形式・破損checkpointも公開済みの証拠とは扱わず、同様にuploadから再構築する。API応答に有効期限がない場合は保守的な5分TTLとして扱う。

media upload後の通信断やcrashは孤児mediaを生む可能性があるがPostは公開されないため、`SafeRepeatNoPublication`として再upload可能とする。一方、`POST /2/tweets`送信後の不明状態は従来どおりUnknownとし、自動再POSTしない。

GUIはProvider capabilityが`ImageSet`を示す場合のみJPEG入力を表示し、1〜4枚・各5 MB以下を受け付ける。受信時にJPEG magic bytesを確認し、spool importでSHA-256を確定する。送信直前にもsizeとSHA-256を再確認し、enqueue後に素材が差し替わった場合は送信しない。

## 自動試験

fake HTTP / durable workerで次を検証する。

- 画像upload → checkpoint → Post作成の正常系
- upload応答喪失後の安全な再uploadとPost単一作成
- prepared upload中のcrash/restart後の再queue
- spool素材改変時のintegrity failure（HTTP送信なし）
- media ID期限切れ後の再upload
- Post作成応答喪失時のUnknown遷移と自動再POST禁止
- GUIからのJPEG enqueue、非JPEG拒否、ローカルpath非開示

## 動画を同時実装しない理由

動画はinitialize / append（複数segment）/ finalize / status / Post作成となる。現行coreのattempt上限8は失敗retry用であり、正常なsegment progressをattemptとして数えると大容量動画を誤って打ち切る。Phase 2C-2でprogress stepとfailure retry budgetを分離してから実装する。

## Phase 2C-2 — X native video status: Pending

2026-09-16時点で、Xのnative動画投稿は**Pending**とする。

理由はchunked upload自体の実装難度だけではない。Xの公開仕様では動画processingについて`pending` / `in_progress` / `succeeded` / `failed`のような粗い状態は扱える一方、`failed`になった場合の詳細原因と、それぞれを安全に再実行してよいかどうかを判断できる安定した失敗分類の契約が十分に確認できていない。

原因が不明な`failed`を機械的にqueueへ戻して再upload・再処理すると、入力不適合、entitlement、policy、その他の恒久失敗まで一時障害として反復する可能性がある。さらにPost作成境界まで誤って再試行すると重複投稿や過剰な自動化挙動につながるため、アカウント運用上のリスクもある。

このため、詳細失敗状態とreplay safetyを推測してnative動画投稿を実装・有効化しない。少なくとも以下が満たされるまでPhase 2C-2はPendingを維持する。

- 公式仕様または管理されたlive受入試験から、動画processing failureの分類と再試行可否を十分に根拠付けできること
- initialize / append / finalize / status / Post作成ごとのreplay safetyを定義できること
- 正常なsegment progressとfailure retry budgetを分離できること
- 自動retryの上限・backoffと、`NeedsAttention` / `Unknown`へ停止する境界を定義できること
- 実Xアカウントでの限定受入試験を通せること

Pending中のX Providerの実装済み範囲はtext-onlyおよびJPEG画像投稿までとし、native動画投稿は対応済みとして扱わない。
