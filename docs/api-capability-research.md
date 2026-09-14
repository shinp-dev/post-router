# 公式API調査とCapability Matrix

確認日: **2026-09-14**。対象: X API v2、TikTok for Developers v2、YouTube Data API v3 / Analytics API v2、Instagram Platform。広告API、Research API、UIだけの機能を通常投稿APIの能力として数えない。

## 証拠の扱い

「本文」は公式ページ本文を取得確認。「検索」は公式ページの検索結果に記載された内容のみ確認。「設計」は本ツールの選定でありproviderの保証ではない。Metaの複数ページは429/取得制限で本文未取得。公式検索結果・Meta公式Postman例を補助に用いたが、未取得部分を確認済みにしない。ブログ、フォーラム投稿、第三者SNS SDKの実装だけを仕様の根拠にしていない。

## Capability Matrix

○=APIあり、条件=権限・審査・形式等の制約あり、対象外=調査対象の通常APIにない/確認できない。**API能力と本プロジェクトで利用可能かは別**。

| Capability | X | TikTok | YouTube | Instagram |
| --- | --- | --- | --- | --- |
| Text-only | ○ 通常Post | 対象APIなし | Community投稿用APIなし | 通常media投稿では不可 |
| Image + text | ○ | 条件 Photo投稿・URL・審査 | 単独画像投稿不可。thumbnailは別機能 | 条件 Professional、JPEG、配信URL |
| Video + text | ○ media upload＋Post | 条件 Direct Post、私用ツール用途はblocked | ○ 動画upload、title等必須 | 条件 Professional、Reels等 |
| Shorts / Reels | 通常動画 | TikTok動画 | 専用insertはなく動画条件によるShorts分類 | REELSとして投稿 |
| Native scheduled publish | 通常v2 Postでは確認なし | Content Posting APIでは確認なし | ○ privateかつ未公開動画のpublishAt | 通常content publishingでは確認なし |
| Local scheduled dispatch | ○ | Direct Postのpolicy/UX gateによりMVP無効 | ○ ただしnative優先 | ○ 有効なupload経路が必要 |
| Analytics | 条件 public/owned/media指標 | 条件 Displayの公開動画基本値。詳細watch等は別製品 | 条件 owner OAuth、DataとAnalyticsを分離 | 条件 Professional Insights、形式・権限別 |
| Delete remote content | ○ 自分のPost | Content Posting/Displayで公開済み削除を確認できず | ○ videos.delete | APIありとの公式記載。login別権限の詳細確認までMVP無効 |
| OAuth更新 | ○ offline.accessでrefresh token | ○ rotating refresh token | ○ refresh token、失効あり | ○ long-lived access tokenを更新。別refresh tokenではない |
| App審査 | developer app・利用契約・権限が必要 | scope審査＋公開Direct Post audit。用途適合も必要 | OAuth検証とupload公開制限解除のauditは別 | 自己管理Standardと外部Advancedで異なる。Advancedはreview |
| Account制約 | user-contextで本人投稿 | creator infoで公開範囲・長さ確認。未auditはprivate制限 | 認可したチャンネル。API keyだけでは投稿不可 | Business/Creator。一般Personalは対象外 |
| Webhook | X Activity/Webhooksあり、契約・購読範囲別 | publish結果通知あり | WebSubでupload/メタデータ更新。metrics pushではない | comments等あり。全metrics通知を前提にしない |
| このMVPの公開配信 | X phaseで有効化 | **保留** | audit通過後 | G-IG解消後 |

native schedulingの「確認なし」はSNSアプリ画面で予約できないという意味ではない。X Ads等の別契約APIを混ぜない。nativeなしでも上位のScheduleIntentは同じで、ローカル側の実行条件を返す。

## X

### 投稿・形式・回復

POST /2/tweets、DELETE /2/tweets/:id。動画はv2 media initialize / append / finalize / statusを経てmedia IDをPostへ添付する。media IDとPost IDを区別する。作成APIに一般的なidempotency-keyの保証を確認できないため、送信後timeoutはUnknown。文字列一致・時刻一致による自動成功認定はしない。[Create Post（本文）](https://docs.x.com/x-api/posts/create-post)

現行media説明の上限は画像5MB、animated GIF 15MB。動画は非Premiumで20分/8GB、Premium・verifiedで125分/16GB、最短0.5秒。旧来の「全動画140秒/512MB」は現行Post動画の共通上限として採用しない。upload可否とPostへの添付可否は別に検証される。[Media（本文）](https://docs.x.com/x-api/media/introduction)

長文・文字数計算・画像枚数はaccount/endpoint条件をadapterで検証。MVPは通常Post、画像最大4枚、動画1本の限定profileを実機契約テストで確定し、拡張長文・GIF・threadは後回し。ユーザーの本文を自動切り詰めしない。

### 認証・費用・制限

OAuth 2 Authorization Code + PKCE、Native Appを選ぶ。tweet.read / tweet.write / users.read / media.write / offline.access。権限は投稿・media・statsごとに必要な分だけ。access tokenは通常2時間、offline.accessでrefresh tokenを発行。callbackのexact matchが必要。[OAuth（本文）](https://docs.x.com/fundamentals/authentication/oauth-2-0/authorization-code)

現行公開説明は前払いcreditのpay-per-usage。旧Free/Basic/Pro月額を設計へ固定しない。実endpoint単価・許可範囲はDeveloper Consoleで確認する。一般向け公開ページに固定の完全単価表がないため総月額は未確定。[Pricing（本文）](https://docs.x.com/x-api/getting-started/pricing)

確認時の表はPOST /2/tweetsがuser 100/15分、app 10,000/24h、GET /2/tweets/:idがuser 900/15分。実行時のx-rate-limit-*を優先する。rate limitと費用は別。stats pollingにもbudgetを設定する。[Rate limits（本文）](https://docs.x.com/x-api/fundamentals/rate-limits)

商用利用もdeveloper契約・用途制限の対象で、API課金と投稿許可を同一視しない。通常投稿の設定に広告契約を必須としない。v2でもフィールド変更があるためversion表示だけで安定性を保証しない。WebhooksはMVPでは利用せずpollを使う。[X API（本文）](https://docs.x.com/x-api/introduction)

## TikTok

### MVPの成立条件

Direct Postの公式Intended Useは広い利用者向けを求め、自分やチームのaccountへ投稿する私用utilityを不適切としている。**この用途のまま審査通過を予定しない。** またpreview、account表示、公開範囲の手動選択、商用開示、明示同意等のUX要件があり、CLIのflagだけで適合と断定しない。未audit clientは公開制限とuser上限がある。[Content Sharing Guidelines（本文）](https://developers.tiktok.com/doc/content-sharing-guidelines)

設計ではTikTok adapterを認識できるが、direct publish能力はpolicy-blocked。利用目的変更または公式に適合する承認済み経路が確定した時だけ再評価する。外部ツールへの置換・ブラウザ投稿は今回採用しない。

### 投稿・制約

動画は /v2/post/publish/video/init/、video.publish、creator_info/query後に開始。initはtokenごと6回/分。公開可否・最大長はcreator infoの現在値で決まる。[Direct Post（本文）](https://developers.tiktok.com/doc/content-posting-api-reference-direct-post)

動画のtransfer制約はMP4/MOV/WebM、H.264等、23–60fps、各辺360–4096px、最大4GB。Upload endpointは最長10分、実際のDirect Post可能長はcreator情報を優先する。写真はJPEG/WebP、各20MB、URL経由。chunk方式はprovider内に閉じる。[Media Transfer（本文）](https://developers.tiktok.com/doc/content-posting-api-media-transfer-guide)

Photoは /v2/post/publish/content/init/、PHOTOとpost_modeを指定し、PULL_FROM_URLを使用する。ローカル写真を直接binary送信できると仮定しない。所有確認済みdomain/prefixが必要。[Photo（本文）](https://developers.tiktok.com/doc/content-posting-api-reference-photo-post)

Upload API（video.upload）はinboxへ素材を送り、ユーザーがTikTokアプリで編集・公開を完了する別の意味。予約公開の代替ではない。承認を得た場合でも状態はAwaitingUserでありPublishedではない。[Upload開始（本文）](https://developers.tiktok.com/doc/content-posting-api-get-started-upload-content)

publish_idは投稿IDではない。/v2/post/publish/status/fetch/は30回/分、処理・審査に時間保証なし。status/webhookで公開結果とremote IDを回収する。Uploadでは1つの送信から複数投稿となる可能性も考慮する。[Status（本文）](https://developers.tiktok.com/doc/content-posting-api-reference-get-video-status)

### 認証・審査・コスト

Desktop Login Kitはsystem browser、登録loopback URI、PKCEをサポートする。公式Desktop手順にはSHA-256のhexエンコードという差分がある。汎用OAuth helperのbase64urlを機械的に共用しない。[Desktop（本文）](https://developers.tiktok.com/doc/login-kit-desktop/)

access tokenは24時間、refresh tokenは初回365日。更新で新refresh tokenが返れば置換する。client_key / client_secretが必要で、共通secretをOSSや配布binaryに埋め込まない。[Token管理（本文）](https://developers.tiktok.com/doc/oauth-user-access-token-management)

Displayはuser.info.basic / video.listを基本とし、account統計にはuser.info.stats等の個別scope確認が必要。投稿scopeの承認とread製品の用途適合は別々に判断。Displayの主要read endpointは既定600回/分のsliding window。[Rate limits（本文）](https://developers.tiktok.com/doc/tiktok-api-v2-rate-limit)

Content Posting/Displayの調査ページには一般的な従量単価表の記載を確認できなかった。「無料・無制限」とは断定しない。API for BusinessやCommercial Content APIをowner向け無制限analyticsとして流用しない。商用投稿の開示・音楽利用はDirect Postの必須UX。legacy APIは採用せずv2を使う。

## YouTube / Shorts

### 投稿・公開予約

動画はvideos.insert。text-onlyやCommunity画像投稿を行う通常Data APIは提供範囲にない。最大upload 256GB、未検証projectからのuploadはprivate制限。2026年のinsert参照は専用Video Uploads bucketに1 unit/回、既定100回/日と記載。旧1600 unit、2025年の100 unitをそのまま使わない。[Insert（本文）](https://developers.google.com/youtube/v3/docs/videos/insert)

native予約はstatus.publishAt。privateかつ過去に公開していない動画に限る。過去日時を送ると即公開になるため、adapterで再検証する。title/description、privacy、子ども向け・合成・有料promotion開示等は明示入力を保持する。[Video resource（本文）](https://developers.google.com/youtube/v3/docs/videos)

Shorts専用upload endpointはない。2024-10-15以降の縦長/正方形かつ3分以内の動画がShorts対象となる基本条件。1分超で有効なContent ID claimを持つShortsにはblock制約。formatIntent=shortは事前検証用で、分類結果を自前で確定しない。[Shorts条件（本文）](https://support.google.com/youtube/answer/15424877)

本ツールはresumable uploadでprivate動画を作り、session URIを秘密扱いして保存。video IDをdurable保存した後、同じIDに対して公開/予約設定を行う。upload完了と公開を別stepにする。[Resumable（本文）](https://developers.google.com/youtube/v3/guides/using_resumable_upload_protocol)

一般アカウントでの長時間upload等の追加チャンネル制約はdoctorで確認する。API最大256GBが、そのaccountに最大長の権利を与えるわけではない。MVPは短尺のテストprofileから開始する。

### 認証・quota・変更

Desktop OAuth + loopback + PKCEを使う。投稿: youtube.upload、取得: youtube.readonly、公開変更/削除: youtube.force-ssl等の対応scope、analytics: yt-analytics.readonly。実際のendpoint別最小scopeをYouTube Phaseで固定する。一般チャンネルにservice accountやAPI keyだけで投稿しない。[Desktop OAuth（本文）](https://developers.google.com/identity/protocols/oauth2/native-app)

refresh tokenは永久保証ではない。External/Testingでは対象scopeによって7日期限となる。OAuth検証は公開upload auditと別gate。個人利用のOAuth例外があってもupload制限解除を意味しない。[OAuth失効条件（本文）](https://developers.google.com/identity/protocols/oauth2)

料金は従量課金型のXと異なりquota管理中心。専用upload/search bucket等への移行が2026-06-01に記載され、各project Consoleを正とする。割当超過は追加支払だけで解消できるとは限らず、延長申請・auditを考慮する。[Revision history（本文）](https://developers.google.com/youtube/v3/revision_history)

v3固定でも意味は変わる。viewCountには2026-08-24開始の全形式に対する再生開始countへの変更がresource本文にあり、告知日は08-27。2025年のShorts view変更だけで調査を止めない。definition versionと適用期間を記録する。

WebSub通知はuploadとtitle/description更新等で、完全な公開確認やanalyticsの代替ではない。[Push通知（本文）](https://developers.google.com/youtube/v3/guides/push_notifications)

商用利用でも利用者の同意、privacy選択、保管・表示規則が必要。owner統計の履歴保存と他APIデータの保持は異なり、独自の派生metricsにも制限がある。[Developer Policies（本文）](https://developers.google.com/youtube/terms/developer-policies)

## Instagram / Meta

### 対象と認証経路

Business/CreatorのProfessional accountが対象。Instagram Loginを採用し、Facebook Login＋Page連携はMVP外。両loginのscope/token/hostを混在させない。[Instagram Login（検索）](https://developers.facebook.com/documentation/instagram-platform/instagram-api-with-instagram-login)

自己管理accountはStandard、他人のaccountに提供する場合はAdvanced Access / App Reviewを考慮する。[Insights access（検索）](https://developers.facebook.com/documentation/instagram-platform/insights)、[App Review（検索）](https://developers.facebook.com/documentation/instagram-platform/app-review)

OAuth authorization codeを受け、api.instagram.com/oauth/access_tokenで交換。long-lived化し、24時間以上経過・未失効のlong-lived access tokenをrefreshして60日間有効にする。別のrefresh_tokenがあると仮定しない。[Business Login（検索）](https://developers.facebook.com/documentation/instagram-platform/instagram-api-with-instagram-login/business-login)、[Refresh（検索）](https://developers.facebook.com/documentation/instagram-platform/reference/refresh_access_token)

採用scopeはinstagram_business_basic、instagram_business_content_publish、statsはinstagram_business_manage_insights。publish scopeの正式一覧、token交換・callback条件をG-IGで最終照合する。旧business_*名・Facebook Login用instagram_basic等を混用しない。DELETE追加scopeは未確定のためenabledにしない。

### 投稿・制約

media container作成→処理状態確認→media_publishの段階を分ける。container IDと公開media IDは別。Meta公式PostmanのFacebook Login例でもこの段階差を確認できるが、同例のhost/tokenをInstagram Loginへコピーしない。[Meta公式例（本文）](https://www.postman.com/meta/instagram/documentation/6yqw8pt/instagram-api)

JPEG画像、動画、Reels、carousel等のAPI投稿があり、100 API投稿/rolling 24hとの公式記載。text-onlyは対象外。native publish timeパラメータは確認できず、ローカルでmedia_publishを呼ぶ設計とする。[Content publishing（検索）](https://developers.facebook.com/documentation/instagram-platform/content-publishing)

ローカル動画のresumable uploadを扱う公式ガイドが存在する。画像はMetaが取得できるHTTPS配信元を用意する設計。画像・Reelsの詳細最大サイズ/長さ・container TTL・upload session復旧仕様は本文未取得のため数値を確定しない。過去の8MB/1GB/15分/24h等を現行確認値として書き換えない。[Resumable（検索）](https://developers.facebook.com/documentation/instagram-platform/content-publishing/resumable-uploads.md)

DELETEは現行IG Media referenceとchangelogに非広告投稿・Reels等の削除対応がある。昔の「Instagram APIは投稿削除不可」をそのまま採用しない。loginごとの権限名と対象条件を確認するまで、このツールでの削除はdeferred。[IG Media（検索）](https://developers.facebook.com/documentation/instagram-platform/reference/instagram-media)、[Changelog（検索）](https://developers.facebook.com/documentation/instagram-platform/changelog)

### 料金・version・商用・webhook

標準Instagram Platformについて一般的な投稿1回あたりの単価表は今回確認できない。無制限無料とは保証せず、Meta条件に加えて画像stagingの保管・通信費を別に見積もる。

Graph APIはadapterでサポートする明示versionへ固定する。「latest」自動追随は禁止。2026年の公式サイトにv25.0告知が見えるが、それだけで現在の最新versionや全機能対応versionを確定しない。G-IGで対象versionと終了日を確定し、pinする。

商用投稿・paid partnershipの機能更新がある。商用開示の要否・API表現を確認してから当該形式をenabledにする。旧Instagram Basic Display等をPersonal投稿の代替に採用しない。旧metricsとviews移行はmetric単位で判定し、impressions/playsの全形式での現役使用を前提にしない。[Changelog（検索）](https://developers.facebook.com/documentation/instagram-platform/changelog)

Webhookはcomments等の通知を提供するが、Instagram LoginのInsights webhookは非対応との記載。MVPはpollで統一。[Webhook例（検索）](https://developers.facebook.com/documentation/instagram-platform/webhooks/examples)、[Media Insights（検索）](https://developers.facebook.com/documentation/instagram-platform/reference/instagram-media/insights)

## Metrics取得表

「候補」は条件を検証してから有効化。Studio/UIに見える指標でもAPI公開とは限らない。

| 指標・意味 | X | TikTok Display | YouTube | Instagram Insights |
| --- | --- | --- | --- | --- |
| views | media view_count。Post表示数とは別 | video view_count | Data viewCount、Analytics views/engagedViewsは別 | views候補。形式/version確認 |
| reach / impressions | Post impression_count、unique reachではない | 標準video objectになし | 一般reachを約束しない。thumbnail露出・広告impressionsを混ぜない | reach、旧impressionsとは区別 |
| likes | like_count | like_count | Data likeCount / Analytics likes | likes等、形式別 |
| comments | reply_count、全スレッド総数と同義にしない | comment_count | commentCount / comments | comments等 |
| shares | retweet_countとquote_countを分離 | share_count | Analytics shares | shares候補 |
| saves | bookmark_count | 標準objectになし | playlist追加数は保存人数ではない | saved |
| watch time | 一般合計watchを確約しない | なし | estimatedMinutesWatched | Reels total watch候補 |
| average duration | 一般取得を確約しない | なし | averageViewDuration | ig_reels_avg_watch_time候補 |
| completion | playback quartile。100/0は独自派生値 | なし | retentionやaverageViewPercentageは完視聴率ではない | 一律完視聴率を確約しない |
| profile visits | owned user_profile_clicks。訪問人数ではない | なし | 一般profile visitsなし | account/profile関連指標はversion確認 |
| followers/subscribers | account follower count。投稿帰属なし | user.info.stats条件、投稿帰属なし | subscribersGained/Lost、video filter時の意味に注意 | account増減候補。投稿帰属と区別 |

根拠: [X Metrics（本文）](https://docs.x.com/x-api/fundamentals/metrics)、[TikTok Video Object（本文）](https://developers.tiktok.com/doc/tiktok-api-v2-video-object)、[YouTube Metrics（本文）](https://developers.google.com/youtube/analytics/metrics)、[YouTube reports.query（本文）](https://developers.google.com/youtube/analytics/reference/reports/query)、[Instagram Media Insights（検索）](https://developers.facebook.com/documentation/instagram-platform/reference/instagram-media/insights)、[Instagram Account Insights（検索）](https://developers.facebook.com/documentation/instagram-platform/api-reference/instagram-user/insights)。

Xのnon-public/organic/promoted指標には投稿後30日という取得制約があり、media viewは同じ動画を含む複数Post全体の集計という差もある。YouTube期間集計と各SNS lifetime snapshotを足し合わせない。さらに細かいmapping・nullの意味は[Analytics設計](analytics.md)。

## 未確定事項と実装gate

| Gate | 未確定/外部条件 | 解消するPhase・証拠 | 未解消時 |
| --- | --- | --- | --- |
| G-X | Consoleの単価・account entitlement・endpoint最小scope | X PhaseでConsole条件記録＋契約テスト | 予算/対象形式を有効化しない |
| G-YT | OAuth公開状態、upload audit、project quota | YouTube Phaseで別々に確認 | private uploadとfakeを使い、public/native公開はblocked |
| G-IG | login別scope、正式HTTPS callback要件、固定Graph version、画像/Reels上限、container TTL、resumable回復、rate headers、各metrics、保持条件 | Instagram Phase開始時に公式本文・app Dashboardとテストを照合し本書更新 | Instagram公開機能をreleaseしない |
| G-TT | 私用用途不適合、Direct Post UX、Display/Uploadの審査 | 方針変更/公式適合確認が必要。単なる実装テストで解消不可 | Direct Post無効。read/Uploadも別gate |
| G-RET | providerごとの現行データ保持・削除条項 | 各provider Phaseの出荷前確認 | Raw長期保存無効。規約を確認できないproviderの収集は有効化しない |

設計書は条件付きで完成できるが、4媒体完全自動公開という元の希望については**現条件では不成立**と判定する。未確認数値を発明して成立と報告しない。
