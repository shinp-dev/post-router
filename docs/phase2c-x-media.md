# Phase 2C — X media posting

確認日: 2026-09-15。

公式X API v2は`POST /2/media/upload`で画像をuploadし、返却された`media_id`を`POST /2/tweets`の`media.media_ids`へ渡す。Postには写真最大4枚、GIFまたは動画は1つを添付できる。画像上限は1ファイル5 MB。動画はすべてchunked uploadが必要で、upload成功後もaccount entitlementによりPost作成が拒否され得る。

参照:

- https://docs.x.com/x-api/media/introduction
- https://docs.x.com/x-api/media/upload-media
- https://docs.x.com/x-api/media/quickstart/media-upload-chunked

## Phase 2C-1

JPEG 1〜4枚を対象とする。各upload完了時にmedia ID一覧を暗号化checkpointへ保存し、次のworker実行で未upload画像だけを送る。全media ID取得後だけPost作成へ進む。media upload後の通信断やcrashは孤児mediaを生む可能性があるがPostは公開されないため、`SafeRepeatNoPublication`として再upload可能とする。Post作成送信後の不明状態は従来どおりUnknownとし、自動再POSTしない。

## 動画を同時実装しない理由

動画はinitialize / append（複数segment）/ finalize / status / Post作成となる。現行coreのattempt上限8は失敗retry用であり、正常なsegment progressをattemptとして数えると大容量動画を誤って打ち切る。Phase 2C-2でprogress stepとfailure retry budgetを分離してから実装する。
