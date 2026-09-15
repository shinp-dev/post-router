# Publication approval gate

確認日: 2026-09-16。

## 目的

公開直前に人間の承認を要求できる共通境界を、Provider固有APIから分離して提供する。

承認は「YouTubeのprivate動画をpublicにする」等のProvider固有操作ではなく、**まだ公開されていないPublicationを公開境界へ進めてよいという許可**として扱う。

## 所有層

Approval GateはDomain / Applicationの共通概念であり、Provider Adapter内部へ持ち込まない。

Adapterは次の操作をProvider固有に計画するだけとする。

- X: `POST /2/tweets`
- YouTube: 既知video IDのvisibilityをpublicまたはscheduledへ変更する操作
- Instagram: `media_publish`

公開を開始し得るstepは共通契約上`StepEffect.MayPublish`として表現し、Application側のApproval Gateを通過してからdispatchする。

## Policy

`ApprovalPolicy`は次の2値とする。

- `Automatic`: 既定値。通常どおり公開stepへ進む。
- `RequireApproval`: 公開境界の直前で停止し、人間の承認を待つ。

ApprovalPolicyと公開時刻は別概念である。承認は「今すぐ公開せよ」という命令ではない。予約投稿を事前承認しても、元のdue timeより前には公開しない。

## Intent binding

承認はPublicationの現在のcanonical intent hashに紐付ける。

本文、media、visibility、Provider options、schedule policy、approval policy等が変わりintent hashが変わった場合、以前の承認を新しいPublication intentへ流用してはならない。

旧canonical intentにapproval policyが存在しない場合は互換性のため`Automatic`として扱う。

## State

手動承認が必要なPublicationが`MayPublish`へ到達した場合、Provider呼出しやdispatch attemptを作成する前に次へ遷移する。

`Ready / Preparing / Pending -> AwaitingApproval`

対応するPublish jobは`Blocked`となる。

承認後は、公開前提条件がまだ成立している場合に限り、同一Publicationを`Ready`へ戻してjobを再queueする。

`AwaitingApproval`は既存の`AwaitingUser`とは別の状態である。`AwaitingUser`はProvider workflow自体が人間の操作完了を要求する場合に使い、`AwaitingApproval`はpost-router自身の公開許可gateとして使う。

## Safety rules

- 承認前にProviderの`MayPublish` stepを送信しない。
- 承認をCapabilityの代わりにしない。`Approved && CapabilitySupported && Due`が成立して初めて公開可能。
- 承認待ち中のCancelはProviderへ到達させず`Cancelled`へ終了する。
- 予約時刻を過ぎた承認待ちPublicationを、承認操作だけで即時公開しない。公開時刻超過の扱いはschedule policyに従う。
- 同一intent hashへの重複承認は冪等に扱う。
- 誰がいつどのintent hashを承認したか追跡できるdurable audit eventを残す。
- 公開step送信後の曖昧結果は既存のreplay safetyに従い、承認済みであることを理由にblind retryしない。

## Provider defaults

現時点の既定は次とする。

- X text: `Automatic`
- X JPEG image: `Automatic`
- X native video: Pendingのため未適用
- YouTube: `Automatic`を既定とするが、`RequireApproval`を選択可能にする。upload / processingはprivateのまま自動進行し、公開境界だけgate対象とする。

アカウント単位の既定値や投稿単位overrideは将来追加できるが、Provider Adapter契約そのものには含めない。
