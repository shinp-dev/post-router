# Temporary public media host

`ITemporaryPublicMediaHost` は、明示的に渡されたローカル `MediaAsset` だけを一時的な public HTTPS URL に変換する Application port。InstagramなどのProvider Adapter、GUI、Worker、投稿状態機械には接続していない。URL Pull型APIや将来の一時プレビューから同じ契約を使える。

## 公開境界

**Stageした素材はDeleteするまでインターネットから取得可能になる。** `ExpiresAt` は論理期限であり、GitHubが期限に自動削除するわけではない。呼び出し側は必要な期間だけ公開し、取得完了や失敗確定後に `DeleteAsync` を呼ぶ。今回、期限監視やbackground cleanupは実装しない。spool全体の自動公開、一覧公開、GUIからのstage/recover/delete、Instagram連携も行わない。

public URLには元ファイル名、ローカルpath、投稿ID、SHA-256、tokenを含めない。asset名は呼び出しごとに生成するランダムなGUIDと、MIMEから決めた `.mp4` または `.jpg` のみ。public URLとopaque handleは秘密情報ではないが、公開URLを知る人は素材へアクセスできるので取り扱いには注意する。

## Lifecycleと復旧

1. `Prepare(asset, expiresAt?)` がシリアライズ可能な `PublicMediaStagingOperation` を作る。呼び出し側はこれを**アップロード前に永続化**する。
2. `StageAsync(asset, operation)` がファイルの存在、size、SHA-256を検証し、GitHubの公開済みRelease Asset一覧をoperation固有の名前で照合してから一度だけuploadする。
3. 戻り値はpublic URL、opaque `StagedPublicAssetHandle`、作成時刻、論理期限、元assetのSHA-256とsizeを持つ。handleを永続化すればprocess再起動後も同じassetを使用・削除できる。
4. upload応答のtimeoutや5xxで成否不明なら、asset一覧で同名の正常なuploadを探す。見つからなければ `github_upload_outcome_unknown` を返し、**自動再uploadしない**。再起動後は永続化したoperationでread-onlyの `RecoverAsync` を呼ぶ。成否不明のoperationを新しいoperationで置き換えて再uploadしない。
5. `DeleteAsync(handle)` はhandleに入れたasset IDからGitHub Releases APIでassetだけを削除する。既に存在しないassetは削除済みとして扱う。Releaseそのものは削除しない。

operationとhandleはJSONとして保存可能なopaque値。内部には実装/version、repository、release、asset IDと名前、URL、元assetのSHA-256/size、時刻を含み、tokenは含まない。上位コードはGitHubのIDやAPI URLを解釈しない。将来R2などへ差し替える場合はhost実装/versionで振り分けられる。今回DB schemaは追加していない。

GitHubへのmediaはRelease Asset APIで送信し、通常のgit commitやGitHub Pagesには追加しない。1つの専用public repositoryとpublished Releaseをstaging areaとして用いる。owner、repository、release tag、論理TTL、最大sizeは `GitHubReleaseMediaHostOptions` から渡す。upload先は `uploads.github.com`、API先は `api.github.com` に固定し、productionのHTTP clientはredirectを追わない。GitHub応答のURLは `https://github.com/{owner}/{repository}/releases/download/{tag}/{opaque-name}` との一致を確認する。[GitHub Release API](https://docs.github.com/en/rest/releases/releases)、[Release Asset API](https://docs.github.com/en/rest/releases/assets)

## Credentialと利用条件

GitHub tokenは`IGitHubMediaTokenSource`からリクエスト時だけ取得する。既存の暗号化 `IVault` にpurpose `github-media-staging-token` で格納し、そのblob IDだけを設定ファイルに保存する。tokenそのものをコード、設定ファイル、DB平文、URL、ログ、例外へ書かない。最低限、対象repositoryのContents write権限を持つGitHub tokenが必要。public repositoryとpublished Releaseは利用者が事前に用意する。実GitHubへのlive acceptanceは未実施。

CLIでは `pub media configure --owner OWNER --repository REPO --tag TAG` で公開先を設定し、`pub media credential set` の非表示プロンプト、または `pub media credential set --token-stdin` でPATをvaultに登録する。PATをprocess引数に直接渡すoptionはない。`pub media status` は設定とPAT登録有無だけを返し、`pub media check` は公開済みReleaseへのアクセスをread-onlyで確認する。`pub media credential clear` でPATを削除できる。GUIでは「設定」画面から同じ登録・削除・接続確認を行う。GUIはPATを再表示せず、browser storageにも保存しない。ownerかrepositoryを変更すると以前のPATは削除される。

GitHubはupload失敗時に `starter` 状態の空assetを残す場合がある。正常な `uploaded` asset以外は回復成功とせず、自動削除や再uploadもしない。利用者がGitHub側の状態を確認してから処理する。[GitHub公式のupload注意事項](https://docs.github.com/en/rest/releases/assets#upload-a-release-asset)
