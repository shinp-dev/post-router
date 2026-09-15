# Phase 2B — local operations GUI

確認日: 2026-09-15。Phase 1 durable coreとPhase 2A X Adapterに、日常運用向けの薄いPresentation Layerを追加する。

## 方式

ASP.NET Coreの最小ホストが`127.0.0.1`だけで待ち受け、frameworkを使わないHTML/CSS/JavaScriptを配信する。単一ユーザー・単一PC用途では、別Node toolchain、SPA framework、WebSocket、Windows専用desktop frameworkを追加する利点が運用コストを上回らないため採用しない。10秒pollingで十分と判断した。

```text
AI / automation -> CLI -----------+
                                     -> Application -> Domain / ports -> Infrastructure
Human -> Browser -> Local GUI/API -+
```

GUIはSQLite table、vault、X Adapter、HTTP clientへ直接アクセスしない。`OperationsService`、`PostService`、`AccountConnectionService`を利用する。新しい`OperationsService`はCLIや将来の別Presentationからも利用可能なprovider-neutral query/commandであり、HTMLやHTTP型を含まない。

## 画面と操作

- Dashboard: Scheduled、Pending、Processing、Published、Failed、NeedsAttention、Unknown、Cancelled、Expired、認証エラーと接続済みaccount
- 投稿作成: capabilityがtextを許す接続済みaccount、本文、即時/予約、enqueue
- 投稿一覧/詳細: raw Publication/Job state、予定、attempt、remote ID、安全化済みerror、timestamp
- 操作: Domainが許可するcancel、Failedかつ副作用なしと確認できる場合だけretry、Unknown等へのread-only Reconcile
- Account: 非秘密metadata、Connect、Disconnect、Reconnect、Revoke

UnknownはFailedへ畳み込まない。「投稿済みの可能性があり、自動再投稿していない」と表示する。X Reconcileが確定不能な場合も再POSTしない。

## 起動

```powershell
pub gui
pub gui --port 43127 --no-open
```

既定portは`43127`。X Developer Consoleのcallback URLは実際に使うoriginと一致する`http://127.0.0.1:43127/oauth/callback`を登録する。GUIはworkerを内包せず、既存の`worker run`またはWindows Task Scheduler登録済みworkerがqueueを処理する。

## ローカルWeb境界

- KestrelはIPv4 loopback `127.0.0.1`だけへbindし、Hostも`127.0.0.1`だけを許可
- CORSを有効化しない
- mutationはsame-origin `Origin`と、同一originからだけ読めるlaunch単位CSRF token headerの双方を要求
- CSRF tokenはJavaScript memoryだけで扱い、localStorage/sessionStorage/cookieへ保存しない
- CSP、`frame-ancestors 'none'`、`nosniff`、no-referrer、no-store
- request bodyは64 KiBまで。内部exception/stack traceをbrowserへ返さない
- OAuth PKCE verifier/sessionはserver memoryだけに保持し、5分で失効。token DTOをGUIへ公開しない
- account/投稿本文はDOMの`textContent`で描画し、HTMLとして挿入しない

同じOSユーザー権限で動く悪意あるprocessからの保護はloopback Webの境界外であり、既存のOS credential store・ファイルACLを信頼境界とする。LAN/remote access、multi-user、cloud hostingは対象外。

## Application API追加

- provider capability一覧
- dashboard summary
- account非秘密metadata一覧
- publication一覧/詳細
- provider/accountをserver側で解決するtext enqueue
- safe manual retry
- Reconcileの即時queue要求

retryはDomain上の`Failed -> Pending -> Ready`、または`NeedsAttention -> Failed -> Pending -> Ready`を順に検証し、直近publish Attemptが`NotSent`/`NoSideEffect`、過去にAmbiguousなし、remote objectなし、active jobなしの場合だけ同一transactionで再開する。Unknownからretryする経路はない。

## 検証境界

通常CIはKestrelをport `0`のloopbackで起動するHTTP smoke testを実行し、実SNS credentialを要求しない。実X consent/post/refresh/revoke、Windows Credential Manager、X credit/permissionはPhase 2Aから継続して手動受入待ちとする。
