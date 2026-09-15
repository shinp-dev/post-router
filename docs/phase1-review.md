# Phase 1 implementation review

確認日: 2026-09-15。対象: Core + SQLite + Fake Provider。SNS本番APIへの通信・credential発行は対象外。

## 実装した境界

- DomainはHTTP、SQLite、OS APIを参照しない。Application portをInfrastructureが実装する。
- canonical intentとclientRequestIdのbyte一致でenqueueを冪等化する。Content ID、元path、Immediateの現在時刻はidentityに含めない。
- Content、固定MediaAsset、Target optionsをSQLiteへ保存し、worker claim後にProvider入力として再構成する。
- `DispatchPrepared`を外部操作前、receiptを結果後にcommitする。非冪等公開の曖昧結果はUnknownとReconcileへ送り、publishを再送しない。
- SQLite queue、OS worker lock、maintenance shared/exclusive gate、account単位semaphore、global 2枠を分離した。
- Windows Task Scheduler定義は同一ユーザー、LeastPrivilege、IgnoreNew、`ExecutionTimeLimit=PT0S`をXMLへ固定し、登録後にreadbackする。
- token/checkpoint/raw metricsはAES-256-GCMでpurpose-bound暗号化し、master keyは通常Windows profileではCredential Managerに置く。Fakeは明示test profileだけで有効。
- backupはSQLite online snapshot、spool copy、個別SHA-256 manifestを一組にする。restore前に現行setを退避し、復元DBを切替前にquarantine化する。
- metricsは暗号化Rawとversion付きprojectionを分離し、0・未返却・非対応を同一視しない。

## 自動試験で確認する障害

canonical identity、同一key競合、部分成功、公開response喪失、prepared後crash、retry上限、期限超過、取消競合、二重worker、長時間処理中の新規due取得、refresh race、secret marker非流出、素材magic/hash/size、migration checksum/newer schema拒否、raw retention、backup hash、spool復元、restore quarantine/suppress/release、CLI end-to-endを実SQLiteで検査する。

CIはUbuntuとWindowsでlocked restore、Release build、全テストを実行する。コンパイラ警告と推奨analyzer警告はエラー扱い。

## 残る実機gate

コード上のPhase 1は完了とするが、OSイベントを模擬試験だけで「実機確認済み」とはしない。Windows 11通常ユーザーでTask Scheduler install/start/stop/uninstall、ログオン再開、`PT0S` readback、Credential Manager復号、スリープ・ネットワーク復旧を受入確認する。失敗時はSNS Provider Phaseへ進む前にPhase 1へ戻す。

## Phase 2へ持ち越すもの

実Provider capability/validationの最終shape、OAuth login UI、HTTP client、rate bucket、実API DTO、実metrics catalogはX Adapterで具体化する。Fake都合のwire型をDomainへ追加していない。外部exactly-once、電源OFF中のlocal投稿、複数PC workerは保証しない。
