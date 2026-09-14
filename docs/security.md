# Security and authentication

設計 v1 / 2026-09-14。単一ユーザーのPCを想定する。SNSアカウントを操作する権限は強いため、秘密と外部副作用の境界を小さくする。

## Threat model

| 脅威 | 対策 | 残る限界 |
| --- | --- | --- |
| repo/config/logからtoken流出 | secretをvaultに限定、出力allowlist、機密URL除去 | 同一ユーザー権限のmalwareや管理者は保護対象外 |
| 悪意あるpath/本文によるcommand injection | shell stringなし、argv、任意コマンド設定なし | media parser自体の脆弱性は更新・隔離が必要 |
| callbackの偽装・code横取り | state、PKCE対応provider、単発callback、期限、provider確認済みredirect | browser/OS侵害は防げない |
| 二重worker・不正なqueue改変 | OS lock、DB transaction、user ACL | 別installationまでglobal lockできない |
| oversized/偽MIME/細工されたmedia | サイズ上限、magic+probe、制限付きprobe | 完全な無害性判定ではない |
| staging URLの漏洩・SSRF | 必要な場合だけ専用prefix、短期read権限、URL入力の制限 | 公開URLを必要とするAPIでは露出を避けられない |
| DB破損・古いbackup復元 | online backup、check、restore quarantine | 失われた公開receiptは自動復元できない |

## Secret storage

採用方式はOS credential storeにinstallation固有の256bit master key、SQLiteのvault_blobsにAES-256-GCM暗号化payloadを保存する方式。token JSONをproject JSONや環境設定へ平文保存しない。

WindowsではWin32 Credential ManagerのGeneric Credentialを、同一ユーザーのローカル永続credentialとして利用する。キー名はpost-router/installation ID/key version。SDKラッパーは小さいInfrastructure境界とし、API呼出しの実機テストを持つ。credential blobには容量制限があるため、長さが変動するOAuth応答全体を直接格納しない。[Microsoft公式](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw)

暗号化は標準.NET暗号APIを使い、自作cipherを作らない。各blobに一意なnonce、key version、authentication tag、暗号文を保存する。AADにinstallation ID・blob ID・purpose・grant IDを結びつけ、入替えを検知する。token世代と暗号文を同じDB transactionで更新できることが直接credential保存に対する利点。master keyのローテーションは新旧keyを併存させ、全blobの再暗号化とbackup方針の確認後に旧keyを削除する。

secret対象はAPI key、client secret、access/refresh token、resumable session URL、署名付きstaging URL、OAuth code、callback取得用secret。app client IDやscope名は非秘密configに保存可。

macOSはKeychain、LinuxはSecret ServiceのMasterKeyStore実装を将来追加する。利用不能時はfail closed。headless Linuxで平文fileへ自動fallbackしない。

OS credential storeは同じユーザーの悪意あるprocessから守る隔離境界ではない。DB/素材/backup/logのuser ACL、端末暗号化、OS更新を基本とする。標準診断でsecretを表示するコマンドは提供しない。account removeはgrant共有を確認してからtokenを削除する。

## CLIとしてのOAuth

| Provider | 採用flow | 更新・条件 |
| --- | --- | --- |
| X | 登録したNative public client、system browser、Authorization Code + PKCE、登録済みloopback redirect | offline.accessを要求、期限前refresh。公開clientにsecretを捏造しない |
| YouTube | Google Desktop app、system browser、loopback listener、PKCE、offline consent | refresh token保存。Testingの短期失効、verificationとupload auditを区別 |
| TikTok | Desktop Login Kit、登録loopback path/port、PKCE。policy gate適合の用途に限る | token exchangeで要求されるclient secretは本人のvault。returned refresh tokenを保存 |
| Instagram | Instagram Login。callback/redirect方式とtoken更新契約はG-IGでcanonical公式本文を確認してから選定 | 一般的なrefresh_tokenモデルへ押し込まない。未確認の寿命・renewal時期を定数化しない |

scope・endpoint・期限は[API調査](api-capability-research.md)が正本。AuthPortはrefresh方法をRefreshToken / RenewableAccessToken / Noneとして宣言するが、Instagramの値はG-IGを閉じるまでUnknown/disabledとする。一般論のOAuthライブラリがTikTokのPKCE表現等を自動解決すると仮定せず、公式test vector相当のfixtureで検証する。

ユーザー自身のapp登録を前提とする。第三者配布のbinaryに共有client secretを埋め込まない。YouTube/Xのpublic clientと、secretを要するproviderを区別する。scopeは投稿/分析/削除ごとに最小化し、分析追加時だけ追加同意を求める。利用者の同意やアプリ審査をスコープ文字列だけで代替しない。

### Loopback callback

loopbackを公式に認めるproviderではlistenerを127.0.0.1等のloopbackだけへbindし、全interfaceで待ち受けない。登録可能なport・pathの規則をproviderごとに検証する。X/Google/TikTokの規則を互いに流用しない。開始前にbindを完了し、ランダムstate・PKCE verifierを生成する。stateは単回・短期有効、限定method、サイズ上限を検査する。

callbackはcodeやqueryをlogに残さず、照合後すぐtoken exchangeを行う。表示ページは外部script/imageを持たず、no-store、no-referrer、最小CSPを指定する。成功/失敗/timeoutでlistenerを閉じる。ブラウザ起動は許可したhttps認可URLのみをOS APIで開く。SNS投稿のブラウザ自動操作は行わない。

### Instagram callbackの境界

Instagram Loginの現行callback/redirect契約はG-IGでcanonical公式本文とMeta App Dashboardを確認する。**固定HTTPS callback、loopback、relayのいずれも確認前に必須構成として採用しない。** 確認結果がローカルcallbackだけで完結できるならrelayを作らない。固定HTTPS callbackが必要な場合に限り、本人管理の最小限の一回限りcode relayを選択肢とする。

relayが必要になった場合も認証時だけ使い、queue/token/投稿/analyticsを扱わない。CLIが認証済みのrelay管理APIへflowを登録し、高entropy stateと別の取得secretを設定する。callbackは登録stateへのcodeを短期memory保存し、CLIがTLS経由で取得secretをAuthorization headerに載せて一回だけ取得する。stateだけではcodeを読めない。取得後・timeoutで消去する。保存期限はprovider code期限より短くする。

relay管理credentialはvaultに置く。作成APIの認証、rate limit、固定callback、payload上限、アクセスlogのquery除去、cache禁止が必要。任意URLへcodeを転送するopen redirectやSNS tokenの代理交換は実装しない。relay運営者はcodeを見られる信頼境界となるため、第三者の無料relayを暗黙採用しない。

image/media stagingも同様に、確認済みInstagram upload契約で必要な場合だけ導入する。ローカルだけで常に全機能が成立すると説明せず、逆に未確認の外部インフラを先行して必須化もしない。安全なcallback契約を確認できなければInstagram loginはrelease blockedにする。X/YouTubeだけの利用にrelayを要求しない。

## Token refresh race

AuthCoordinatorだけがtoken更新を行う。AuthGrant ID単位のOS interprocess lockを取得してからDBのgenerationと有効期限を再読込する。別processが既に更新していれば新tokenを利用し再refreshしない。Account単位のlockでは共有grantのraceを防げない。

refresh responseを暗号化し、token blob・expiresAt・generationを1 transactionで更新する。APIが新refresh tokenを返さない場合、仕様上継続可能なら既存値を保持する。返した場合は置換する。CAS不一致は上書きせず再読込する。secret読出しのlock順はgrant lock→短いDB transactionとし、network中にDB write lockを保持しない。

providerがtokenをrotateした直後、DB保存前にcrashすると新tokenを失う。この分散transactionは解消できない。古いtokenの無限refreshをせず、providerの回復契約がない場合はReauthRequiredへ移す。worker復旧で公開操作を繰り返して解決しない。

refreshはexpiry前の余裕時間とjitterを持ち、再起動後も期限を再確認する。renewable access token型のproviderでは、**公式に確認した更新可能時期だけ**をAdapterが使う。401はgrant状態を再読込し、安全なreadを再試行できるが、公開の成否確認を飛ばす理由にはならない。

## File / process / staging

予約素材はcanonical path解決後に読取handleを開き、通常fileか確認し、streamしながらhashを計算してprivate spoolへコピーする。コピー後の固定fileをprobe・uploadする。入力pathの後日再読込、ワイルドカード展開、shellによる変数展開をしない。Windows device path、pipe、UNC/ネットワークpath、directory、意図しないreparse pointはMVPで拒否する。予約完了前の元file削除は不問、spool削除は明示管理する。

ffprobe等を採用する場合は固定実行fileをProcessStartInfo.ArgumentListで起動し、shellを無効にする。引数境界とoption終端を適切に使い、timeout、出力bytes上限、同時数、protocol制限を設ける。userが自由なfilter/commandを設定できる機能は持たない。probeは検証のみで、勝手な再encodeや本文加工をしない。

mimeは拡張子だけで決めずmagicとprobeの整合を取る。global spool予算とprovider/accountの動的size/duration/codec上限を両方確認する。巨大fileは全メモリに載せずstreamする。素材hashでTOCTOUを検出し、upload直前にもspoolの同一性を確かめる。

IG image等で公開URLが必要だと公式契約で確認できた場合だけ、本人設定のobject storageに推測困難なobject keyでstagingする。短期read URLはprovider取得・再試行期間を満たすTTLとし、成功確認・期限到来後に削除する。bucket一覧・書込は公開しない。URLをsecretとして扱い、queryをlogしない。TikTokのverified domain条件を署名URLで回避したことにはしない。

MVPは任意外部URLのimportを提供しない。APIが返すupload URLはhttps、確認済みprovider host/path契約、redirect方針をAdapterで検査する。DNS/private IPへの遷移、credential付redirectは拒否する。固定host一覧の変更はversioned Adapter修正とする。認証headerを別hostへ自動転送しない。

## Log / error / observability

構造化logのallowlistはtimestamp、level、event code、local IDs、provider、safe status、duration、attempt、sanitized request ID。本文・caption・file path・HTTP headers/body・token・full callback/upload URLは既定で出さない。console errorも同じsanitizerを通す。provider errorの自由文はそのまま表示せず、既知codeから人間向け説明を生成する。

--verboseでもsecret除外を解除しない。診断bundleはqueue件数、version、capability reasons、safe errorsのみで、raw metrics/DB/config全量は含めない。local logは容量rotationと14日default保持。外部telemetryはMVPなし。秘密を引数で渡す--token等は作らず、対話の非表示入力か専用secret入力経路を使う。

## Webhookとデータ削除

MVPはpollingで、外部webhook listenerを起動しない。将来追加時はprovider署名のraw body検証、timestamp/replay対策、challenge handshake、event ID dedup、payload上限をAdapterで実装する。WebSub等の方式差も別契約にする。eventは状態照合のきっかけで、未検証payloadだけでPublishedにしない。

account disconnectは新規job停止、進行中/remote予約の残存提示、token revoke可能なら実行、local credential削除、provider別data purgeの順。token失効で予約済みYouTube動画まで消えたとは表示しない。規約上の削除要求は[Analytics](analytics.md)と[永続化](persistence.md)のretention engineへ引き継ぐ。