# Persistence

設計 v1 / 2026-09-14。

## 候補比較と決定

| 候補 | durability / 同時実行 | 運用・可読性 | 判断 |
| --- | --- | --- | --- |
| JSON全量 | atomic renameだけでは複数file transaction・競合・重複防止が難しい | 人間/AIには読みやすいが履歴増大で全量書換 | export/manifestのみ |
| JSONL event log | appendは容易だがindex、recovery、projection、compactionを自作 | 追記履歴が読める | SoT不採用 |
| SQLite | transaction、unique、foreign key、crash recovery、複数reader | 単一file群、導入server不要 | 採用 |
| PostgreSQL | 強い同時実行と運用機能 | 常駐server・認証・backup負荷 | 単一PCには不要 |
| embedded key-value DB | 高速だがrelationやmigration/queryを別途設計 | 調査/運用時の読出し負荷 | 利点不足 |

SQLiteを投稿・queue・remote ID・metricsのsource of truthとする。JSONは投稿manifest、非秘密config、CLI --json、stats JSONL exportに使う。AIは安定したCLI JSONを読み、SQLite fileを直接編集しない。

Microsoft.Data.Sqliteで明示SQLを実行する。ORMの追跡状態をqueueの排他保証に使わない。write transactionは短く、HTTPはtransaction外。WAL、synchronous=FULL、foreign_keys=ONを各connectionで設定し、busy timeoutは短時間のDB競合にだけ使う。network filesystem、OneDrive等の同期folder、共有DBでの複数PC実行は非対応。[SQLite WAL公式](https://www.sqlite.org/wal.html)

FULLはOS・storageがflush要求を守る前提であり、壊れたhardwareまで保証しない。WALは複数readerと単一writerであり、無制限の並列writeではない。

## 保存場所

Windows defaultはユーザーのLocalAppData配下のpost-router。db、spool、backups、logsを分け、user ACLを設定する。repoやshorts-cliのproject folderへ秘密やqueueを置かない。別data directoryは初期化時に明示し、worker taskにも同じinstallation ID/pathを固定する。

Linux XDG、macOS Application Supportのmappingは将来Infrastructureへ追加する。configはversion付きJSON、provider account alias、timezone、spool上限、worker policy、app ID等のみ。secret_refは参照IDであり復号キーではない。

## 論理schema

これは設計でありSQL migrationは未実装。

| Table | 主なcolumnと制約 |
| --- | --- |
| installations | id、schema_version、created_at、restore_epoch、quarantined |
| app_registrations | id、provider、public_client_id、redirect_profile、safe_config_json |
| accounts | id、app_id、provider、remote_account_id、alias、grant_id、status、unique(app_id,provider,remote_account_id)、unique(alias) |
| auth_grants | id、app_id、subject、scopes_json、vault_blob_id、generation、expires_at、status |
| vault_blobs | id、purpose、key_version、nonce、tag、ciphertext、created_at |
| posts | id、client_request_id unique、intent_hash、canonical_intent、current_revision、created_at、duplicate_of |
| post_revisions | post_id、revision、content_id、intent_json、created_at、PK(post_id,revision) |
| contents / media_assets | 本文/title/形式、hash、mime、size、duration、private storage_ref、reference count |
| targets | id、post_id、revision、account_id、options_schema/version/json、visibility、unique(post_id,revision,account_id) |
| consent_records | id、target_id、kind、policy_version、recorded_at、evidence_digest |
| schedules | id、mode、due_at_utc、original_local、zone_id、offset、zone_rules_fingerprint nullable、max_lateness |
| publications | id、target_id unique、schedule_id、state、execution_mode、timestamps、safe_error_json、generation |
| remote_objects | id、publication_id、account_id、kind、provider_object_id、parent_id、observed_state、timestamps、unique(account_id,kind,provider_object_id) |
| worker_runs / worker_control | run ID、process/started/stopped/heartbeat、stop_requested、maintenance_reason |
| jobs | id、kind、publication_id/auth_grant_id/stats_sync_run_id/data_deletion_job_idのnullable FK、priority、step_key、state、due_at、resume_state、generation、attempt_count、worker_run_id、claimed_at |
| attempts | id、job_id、step_key、dispatch_state、request_digest、effect、replay_safety、effect_certainty、receipt_ref、safe_error、timestamps |
| provider_checkpoints | publication_id、adapter_key、schema_version、vault_blob_id、updated_at |
| rate_limit_buckets | provider/app/account/endpoint key、remaining、reset_at、next_allowed_at、observed_at |
| capability_snapshots | account_id、adapter_version、checked_at、expires_at、safe_payload |
| raw_metric_responses | id、provider/api_version、request_descriptor、subject、retrieved_at、vault_blob_id、payload_hash、retention_class、expires_at |
| stats_sync_runs | id、scope_json、state、requested_at、finished_at、safe_error |
| metrics_snapshots | id、account_id、subject_ref、provider、api_version、retrieved_at、period_start/end、provider_timezone、dimensions_json、completeness、mapping_version、created_at |
| snapshot_raw_links | snapshot_id、raw_id、page/query role |
| metric_observations | snapshot_id、provider_key、canonical_key、definition_version、value_decimal/text、unit、value_status、dimensions_json |
| policy_checks | grant/account、policy_version、authorization_checked_at、subject_existence_checked_at、next_check_at |
| data_deletion_jobs | scope、reason、deadline、state、backup_cutoff |
| audit_events | event_id、local IDs、safe actor/action、timestamp、safe details |
| schema_migrations | version、name、checksum、applied_at、app_version |

remote_objectsのproviderは`account_id -> accounts.provider`で決まり、identityはaccount＋kind＋opaque IDでDB上も一意にする。`remote_objects.account_id` はPublication→Targetのaccountと一致することをApplicationが挿入時に検証し、別accountのremote IDをattachしない。upload IDとmedia IDが同じ文字列でもkindが違えば別扱いする。1:nの返却を表現し、joinで曖昧な帰属を作らない。receipt/checkpointの機密部分はvault blob、非機密のremote IDは通常columnとする。

MetricsSnapshotはDomain契約どおりprovider / api_version / retrieved_atを直接保持する。複数Raw responseを1 snapshotへlinkしても、どのProvider/API世代・取得時点のprojectionかをjoin推測だけに依存させない。`created_at` はprojection行の作成時刻であり、remote取得時刻の代用にしない。

必須indexはjobs(state,priority,due_at)、jobs(worker_run_id,state)、publications(state)、targets(post_id)、remote_objects(account_id,kind,provider_object_id) unique、raw(expires_at)、snapshot(subject,period,retrieved_at)、observation(provider_key)。大きな時系列を全件読んでstats showしない。doubleでcountを丸めず、巨大countはdecimal文字列対応のCLI schemaとする。

unique client_request_idとintent_hash照合を同一transactionで実行する。hash衝突だけをidentityにせず、canonical_intent bytesも比較する。jobsはkindに対応するowner FKが1つだけnon-nullとなるCHECKを持つ。Publish/Prepare/Poll/Reconcile/Delete=publication、Refresh=auth_grant、StatsCollect=stats_sync_run、Purge=data_deletion_jobとする。active jobのowner＋kind＋step_key重複を防ぐ部分unique indexとgeneration CASを使う。成功済みPublicationにpublish jobを作れない不変条件もApplicationで検証する。Attemptにはcrash recoveryで必要なeffect/replay_safetyを保存し、adapter更新後の再計画だけから推測しない。

## Filesystemとのcommit境界

素材はprivate tmpへstream copyしhash・validation・flush後、同一volumeのspoolへatomic renameする。その後DB transactionで参照を登録する。DB commit失敗時は未参照fileが残り得るが、queueが存在しない素材を参照する順序を避ける。startupでtmp/orphanを猶予期間付き清掃する。copy/renameの途中で失敗したものを完成素材と見なさない。

spoolをユーザーが消した場合、該当未公開対象をNeedsAttentionとして止める。remote IDがある既公開対象まで再作成しない。DBとfileを同一transactionにできないことをrecoveryで補う。

## Raw storageと容量

MVPは小規模運用として、圧縮後暗号化したmetrics responseをSQLite BLOBへ保存する。HTTP応答bytes上限と展開後上限を設ける。認証responseのraw保存はしない。headersはrequest ID、API version、rate metadata等allowlistだけ。生のAuthorization、Cookie、署名URLは除く。

大きなmediaはDB外。rawの長期増大はretention、圧縮、query限定、収集間隔で抑える。100 Publication/月×20 snapshot×20KBならraw約40MB/月という容量試算を置くが、実測値として扱わない。default DB警告2GB、管理spool上限は明示configで設定し、disk不足ならenqueue/HTTP継続を止める。大容量対応が必要になってからraw blob portのfile backendを検討し、初期から別object DBを持たない。

## Migration

連番の埋込SQL migration、immutable checksum、schema_migrations tableを採用する。手書きSQLの対象DB versionとbackup方法をreviewする。既存migrationを書き換えず次versionで修正する。provider checkpoint/options/metric mapping versionはDB schema versionとは別。

db migrateはworker stop/drain後にmaintenance lockを取得し、online backup、空き容量、整合性を確認する。worker_controlの停止要求だけで排他取得済みとはみなさず、OS lock解放を確認する。transaction内で適用可能な変更は全体commit。table再構成はcopy・制約/件数確認・swapし、失敗時rollbackする。transaction外が必要な操作は明示段階とrecovery markerを持つ。

新binary起動時に古いschemaなら送信を開始せずmigrationを案内する。新しすぎるschemaなら読書を拒否する。未読checkpointは新規publishに変換せずNeedsAttention。down migrationはMVPなし。復旧はbackup restoreだが必ずquarantineとなる。

### Maintenanceの全書込経路への適用

maintenance lockはworker専用ではなく、installationごとのprocess間共有/排他gateとする。通常の書込操作は共有利用権、backup / migrate / restoreは排他利用権を必須とする。CLI enqueue / cancel / attach / account・token更新、workerのclaim / receipt、spoolの作成・削除、cleanup等も例外にしない。共有利用権は操作対象のDB・素材への変更開始前に取得し、filesystemとDBのcommit境界を完了するまで保持する。workerのremote操作はDispatchPrepared→HTTP→receipt commitを1つの共有利用区間とし、途中で解放して保守操作に割り込ませない。依存Job待ちや次のdueAtまでの待機では解放する。

保守処理はまずworkerへdrainを要求し、停止・OS worker lock解放を確認してからmaintenanceの排他利用権を取得する。workerのreceipt commitに必要な共有利用権を、排他保持中に待たせてdrainする順序は禁止する。排他取得待ち中は新規共有利用を止め、進行中の通常書込だけを完了させる。待ち切れなければ保守操作を失敗終了し、lockを破って続行しない。新workerも共有利用権を得るまではclaim・送信を開始しない。

排他中の別CLIの書込はBusy / Maintenanceとして終了コード7で拒否し、DB・spoolへ変更を残さない。DBを開く通常CLIは読取も共有利用権を持ち、restoreで旧DBのhandleが残ることを防ぐ。status等の読取は短い区間ごとに接続を閉じて解放し、CLIの--waitや対話入力待ち全体には保持しない。排他解放後に接続し直す場合はschema versionを再検査する。一般の共有利用権取得はgrant lockやDB transactionより前に行い、共有保持から排他へのupgradeをしない。

backupはDB snapshot、spool manifest/hash、参照素材のコピーが完了するまで排他を保持する。migrate / restoreも対象DBの検査・置換・接続終了まで同じgate下で行う。SQLiteの単一writer制約だけを、複数processとfilesystemを跨ぐ保守排他の代わりにしない。

## Backup / corruption / restore

稼働中のDB file単体copyは禁止。db backupはworkerをdrainしてmaintenance lockを取得し、固定されたDB snapshotとspool manifest/hash、参照素材を1つのbackup setにする。SQLite online backup APIを使うが、DB取得後にworkerがspoolを変えないことをmaintenance lockで保証する。WALを無視したcopyをbackup成功と呼ばない。quick_checkを起動時、integrity_checkをdb check/restore時に行う。異常時はremote mutationを止め、元DBを上書き修復しない。

default backupは同一PCのACL保護領域、7日rotation。DBには本文・ID等の平文個人情報があり、token暗号化だけでbackup全体が秘密化されたとは説明しない。外部持出しは利用者管理の暗号化volume等を必要条件とする。

master keyはDB backupへ平文同梱しない。同一profileで復号可能かrestore時に確認する。別PCでkeyがない場合、token/checkpointは復号できず再認証とremote照合が必要。keyを失ったupload sessionは安全に新uploadへ置き換えられない。MVPではportable key exportを提供しない。

restoreは全jobをquarantineし、復元時点より後のremote副作用が存在し得ることを提示する。ローカル記録が古い場合の自動再投稿をしない。[復旧詳細](scheduling.md)

## Restoreの実行契約

db restore FILEは既存workerをdrainし、現在DBの退避backupを作ってから、入力backupのmanifest/hash・DB integrity・master key復号可否を検査する。成功後も通常workerを起動せずinstallations.quarantined=1とする。restore statusは送信副作用を持ち得るPublicationを、既知remote IDあり、強い照合可能、照合不能、確実に未送信の候補に分類して表示する。

quarantine中に許すremote操作はread/reconcileだけ。利用者は確認済みremote IDをpost attachで所有確認して関連付けるか、db restore suppressで対象をNeedsAttentionに固定して全mutation jobをCancelledにする。suppressはremoteに存在しないと断定せず、不確実性と理由を監査履歴へ残す。別の新規投稿はquarantine解放後に新keyで行う。restore releaseは、復元時点以降に実行された可能性がある全publish系jobが安全な終端または送信禁止状態になったことをtransactionで検査し、安全なread/cleanup jobだけを解放する。未解決対象をPendingとして再送できるforce flagは設けない。元DBを消去せず、切戻しにも同じ検査を要求する。

## Retentionと削除

provider policyによる期限をraw、projection、export、backupに伝播させる。immutable snapshotとは値を上書きしない意味であり、規約に基づく削除を禁止しない。削除後は値・remote個人情報を残さない最小tombstoneだけを許容する。

期限到来・disconnect・削除要求時はjob停止、DB対象行とvault blob削除、該当managed export/spool削除、該当backupの削除または再生成を行う。backupはrow単位で消せないため、影響するbackup全体を削除する選択をdefaultとする。restore前に最新の外部削除要求・retention条件を適用できないbackupは利用しない。

SQLiteはdeleteだけで全physical bytesの即時消去を保証しない。secure_delete設定、reader停止後のcheckpoint/truncate、適切なVACUUM、古いbackup削除を運用に含める。SSD/wear levelingや外部コピーの完全消去は保証しない。暗号化rawの削除も旧backupとkeyの残存を考慮する。

YouTube等の保持条件は[Analytics](analytics.md)に定義する。PC停止中の期限超過をソフトだけで回避できない。長期停止前にはmanaged provider dataのpurgeを行う運用を明記し、起動直後に期限処理が終わるまでdata表示/再送信を禁止する。利用者が管理外へコピーしたexportは自動回収できず、export時に期限と削除責任を付記する。
