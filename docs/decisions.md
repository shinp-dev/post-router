# 技術選定・ADR

決定日: 2026-09-14。単一PC・単一ユーザー・Windows優先を評価軸とする。

## ADR-001: C# / .NET 10 LTS

| 候補 | 利点 | この用途での負担 | 判断 |
| --- | --- | --- | --- |
| C# / .NET | 型、async/cancellation、Windows連携、単一配布、テスト可能な時刻 | 配布サイズ、OS vaultにはadapterが必要 | 採用 |
| TypeScript / Node | JSON/HTTP/CLI開発が速い | runtime配布・SQLite/vault native依存・型検証を別途整備 | 次点 |
| Python | 試作とAPI調査が容易 | 配布、環境差、runtime型検証、常駐運用 | 不採用 |
| Go | 単一binary、軽い運用 | Windows secret/task連携とDTO実装の追加工数 | 有力だが不採用 |
| Rust | 堅牢な型と配布 | API glue中心のMVPには実装負担が大きい | 不採用 |

.NET 10 LTSは2028年11月までサポート対象。新規開発でサポート終期が近い.NET 8を選ばない。[Microsoft](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)

.NET Generic Host、HttpClientFactory、System.Text.Json、TimeProviderを利用。CLIはSystem.CommandLine 2系の安定版をPhase 1で固定する。[公式CLI文書](https://learn.microsoft.com/en-us/dotnet/standard/commandline/)

System.CommandLineとSpectre.Console.Cliを比較し、装飾より入出力契約と標準的parseを優先して前者を選ぶ。対話表示のためだけの追加frameworkは不要。配布はself-contained win-x64 ZIPを初期案とし、AOT、MSI、Store配布は後回し。更新時はworkerをdrainし、DB互換性を確認してからbinaryを切り替える。

## ADR-002: SQLite + filesystem

JSONはmanifestとexportには適するが、競合更新・queue・metricsの蓄積ではtransactionを自作する必要がある。PostgreSQLは優秀だが常駐DB管理が過剰。SQLiteをsource of truthにする。[詳細比較](persistence.md)

Microsoft.Data.Sqliteによる明示SQLを選び、queueの条件付き更新を読みやすくする。EF Coreによる広いobject tracking、汎用Repository frameworkは不要。migrationは連番SQL＋checksum履歴の小さいrunner。テーブル再構築やbackfillは専用移行として記述する。

## ADR-003: 単一worker + Windows task

| 候補 | 判断 |
| --- | --- |
| メモリtimerのみ | 再起動で消失するため不採用 |
| OS taskを投稿ごとに登録 | DBと二重の真実になるため不採用 |
| Quartz / Hangfire | 機能は豊富だが投稿状態とjob状態の二重管理を増やす。初期は不採用 |
| SQLite Jobs + Generic Host loop | 採用。単発時刻・再試行・照合のみを扱う |
| Windows Service | system権限・ユーザーvault差・管理者操作が増えるため不採用 |
| クラウドworker | 電源OFF対策になるが、運用範囲を拡大するためMVP外 |

独自実装するのは限定的な永続job loop。cron式・分散lease・leader election・任意workflow DSLは実装しない。

## ADR-004: 暗号化vaultとOS key store

Credential Managerへtoken全体を保存する案は、blob容量・複数値更新・DBとのcommit不整合が課題。master keyだけをCredential Managerへ保存し、AES-256-GCM暗号化payloadをSQLiteへ置く。tokenとgenerationを一緒にcommitできる。暗号方式は標準APIを使い、独自暗号を作らない。

DPAPI CurrentUserでtokenを直接暗号化する案も可能だが、将来のmacOS/Linuxへの契約を揃えやすいenvelope方式を選定。key lossは再認証。平文fallbackはない。

## ADR-005: 公開API仕様の境界

各SNSの公式SDKが必要ならそのprovider内でのみ採用。Google OAuthにはGoogle.Apis.Authを候補として採用し、token保存を独自vault portへ接続する。投稿HTTPは薄いtyped clientとversion別DTO。巨大な統一SNS SDKや無検証JSON pass-throughは採用しない。

capabilityはruntimeの権限とpolicyも含む。新SNSのためだけに既存SNSに空実装を強制する巨大interfaceは作らず、optional portに分割する。

## ADR-006: OAuth callback

XとYouTubeはsystem browser＋loopback。TikTok Desktopもloopbackが公式に存在するため、HTTPSサーバー必須と決めつけない。ただし独自PKCEエンコードやclient secretの扱いはTikTok adapterに隔離する。

Instagramは認証専用の固定HTTPS callbackを使う設計を選ぶ。トークン交換はローカルで行い、callbackは一回のcodeを安全に引き渡すだけ。公共callbackの追加運用はInstagram有効化時だけ。詳細は[security](security.md)。CLI向けloopback例外が対象appで公式に確認できた場合のみADRを更新して簡略化する。

## ADR-007: Normalizedは表現の統一に限定

同じ名前の指標を同じ意味とみなさない。特にYouTubeは派生指標制限があるため、MVPの共通層はprovider値のalias、型、表示単位のメタデータに限定する。独自のengagement score、views当たりlike率、媒体横断合計は作らない。[Analytics](analytics.md)

## ADR-008: Instagram画像の配信元

APIが取得できるHTTPS画像URLを必要とする経路に備え、MediaStaging portを設ける。単一のS3互換実装をInstagram Phaseで用意し、bucket全体公開はせず期限付きGETを利用する。動画は公式のresumable local uploadを第一候補にする。URLの必要性を全providerへ強制しない。未確認のendpoint契約はG-IG gateを通す。
