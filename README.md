# post-router

人間が用意したコンテンツを、公式APIで配信・予約し、結果を回収する単一ユーザー向けCLI。

**状態: Phase 1 Core + SQLite + Fake Provider 実装済み / 2026-09-15。X / YouTube / Instagram / TikTokの本番Adapterは未実装です。**

Phase 1は配信基盤の不変条件を実装・検証する段階であり、SNSへ通信しない。Fake Providerは `POST_ROUTER_PROFILE=test` を明示した場合だけ登録され、通常profileでFake結果を本番公開済みとして扱う経路はない。

## Phase 1 quick start

.NET 10 SDKを用意し、依存関係をlock fileどおり復元する。

```powershell
dotnet restore PostRouter.sln --locked-mode
dotnet build PostRouter.sln -c Release --no-restore
dotnet test PostRouter.sln -c Release --no-build --no-restore

$env:POST_ROUTER_PROFILE = "test"
dotnet run --project src/PostRouter.Cli -- --data-dir ./.local post --file contracts/examples/release.fake.json
dotnet run --project src/PostRouter.Cli -- --data-dir ./.local worker once
dotnet run --project src/PostRouter.Cli -- --data-dir ./.local queue
```

Phase 1で実装済みの主な操作は `account list`、`post/status/cancel`、`queue`、`worker once/run/stop/install/start/status/uninstall`、`stats sync/show/purge-expired`、`doctor`、`db backup/check/restore` と復元後の `status/suppress/release`。`worker install` 系はWindowsの同一ユーザーTask Schedulerを利用する。

## 設計上の結論

C# / .NET 10 LTS、System.CommandLine、SQLiteを採用する。1つの実行ファイル `pub.exe` が通常CLIと `worker run` を提供し、Windowsタスクスケジューラから同じユーザーでworkerを起動する。単一workerとは、installationごとに実行調整を担うprocessを1つにする意味であり、1つのHTTP処理で全queueを直列に塞ぐ意味ではない。worker内では対象ごとの直列性を保ちながら、期限優先の小さな並行実行を行う。SNS別AdapterがAPI・認証・制限・メトリクス解釈を所有する。

4媒体を設計対象とするが、**4媒体すべてへの私用CLIによる無人公開投稿は、現行の公式API条件ではMVPとして約束できない。** TikTok Direct Postには私用アップロードツールを不適切とする規定がある。審査に出せば必ず解決する問題として扱わない。[TikTok公式ガイドライン](https://developers.tiktok.com/doc/content-sharing-guidelines)

実用MVPはX、監査承認後のYouTube、ProfessionalアカウントのInstagramを順に実現する。TikTokは能力・認証・結果回収の設計を用意し、Direct Postはpolicy-blockedとする。Display APIや手動完了型Uploadも、それぞれ利用目的と審査の確認が必要。

## 将来の本番利用例

PowerShellでもそのまま扱いやすい一行形式。対象アカウント、公開範囲、YouTube固有必須値は登録・投稿時に明示する。

```text
pub account add x --name x-main
pub account list --json
pub plan --file release.json --json
pub post --file release.json --json
pub post --text-file caption.txt --video ./out.mp4 --to x-main,yt-main --title "作品紹介" --visibility public --at "2026-09-15T20:00:00+09:00" --options-file targets.json --idempotency-key release-20260915-b --json
pub queue --json
pub post status <post-id> --json
pub stats sync --post <post-id> --json
pub stats show --post <post-id> --layout provider-columns
pub doctor --online --json
```

以下はPhase 2以降の目標UXであり、まだ実行できない。本番用 `release.json` の仕様は[CLI仕様](docs/cli.md)を参照。`targets.json` にはYouTubeの子ども向け設定など、媒体固有の意味を持つ設定を記述する。全対象の必須設定・能力検証を通ってからqueueへ登録する。text-onlyを4媒体へ指定すると非対応対象を示して全体を拒否する。勝手な画像化・動画化・本文切り詰めはしない。

`--at` は希望公開時刻。YouTubeは事前アップロード後のnative予約、X/Instagramはローカル実行を使う。厳密な同時公開、電源OFF中のローカル投稿、ネットワーク越しのexactly-onceは保証しない。成否不明時は照合を優先し、自動再投稿しない。

## 責務

| ツール | 責務 |
| --- | --- |
| shorts-cli / ved | 動画制作・編集・render |
| post-router / pub | 配信、予約、認証、状態管理、取得可能な結果の保存 |

連携境界は完成ファイル。shorts-cliへの依存・変更は不要。AIによる文章生成、返信・DM・フォロー・いいね操作、スクレイピング、ブラウザ投稿代替は対象外。

## 設計書

| 文書 | 内容 |
| --- | --- |
| [API調査・Capability Matrix](docs/api-capability-research.md) | 確認日・根拠・API上の可否・未確認事項 |
| [アーキテクチャ](docs/architecture.md) | 境界、依存方向、配置 |
| [技術選定](docs/decisions.md) | 比較、採用理由、却下案 |
| [CLI仕様](docs/cli.md) | 操作、JSON契約、部分成功 |
| [ドメインモデル](docs/domain-model.md) | 投稿意図・配信先・公開実体の分離 |
| [Provider契約](docs/provider-contract.md) | 能力、投稿、回復、エラー |
| [予約・復旧](docs/scheduling.md) | 状態機械、成否不明、再起動 |
| [認証・安全性](docs/security.md) | 秘密情報、OAuth、脅威 |
| [永続化](docs/persistence.md) | DB、migration、backup、retention |
| [Analytics](docs/analytics.md) | Raw・Normalized、定義差、保存方針 |
| [テスト](docs/testing.md) | 障害注入と出荷ゲート |
| [実装計画](docs/implementation-plan.md) | Phase、外部条件、人間の準備 |
| [最終設計監査](docs/design-review.md) | 指摘、修正、残留リスク |
| [Phase 1実装監査](docs/phase1-review.md) | 実装境界、自動試験、実機gate |

公式APIの調査と設計判断は区別する。Instagramは公式本文の一部取得に制限があり、細部の未確認事項をAPI調査書のG-IGに明記した。設計完了は審査通過・本番動作確認を意味しない。
