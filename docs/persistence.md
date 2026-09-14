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
| posts | id、client_request_id unique、intent_hash、current_revision、created_at、duplicate_of |
| post_revisions | post_id、revision、content_id、intent_json、created_at、PK(post_id,revision) |
| contents / media_assets | 本文/title/形式、hash、mime、size、duration、private storage_ref、reference count |
| targets | id、post_id、revision、account_id、options_schema/version/json、visibility、unique(post_id,revision,account_id) |
| schedules | id、mode、due_at_utc、original_local、zone_id、offset、tzdata_version、max_lateness |
| publications | id、target_id unique、schedule_id、state、execution_mode、timestamps、safe_error_json、generation |
| remote_objects | id、publication_id、kind、provider_object_id、parent_id、observed_state、timestamps |
| jobs | id、publication_id/grant_id、kind、step_key、state、due_at、resume_state、generation、attempt_count |
| attempts | id、job_id、step_key、dispatch_state、request_digest、effect_certainty、receipt_ref、safe_error、timestamps |
| provider_checkpoints | publication_id、adapter_key、schema_version、vault_blob_id、updated_at |
| rate_limit_buckets | provider/app/account/endpoint key、remaining、reset_at、next_allowed_at、observed_at |
| capability_snapshots | account_id、adapter_version、checked_at、expires_at、safe_payload |
| raw_metric_responses | id、provider/api_version、request_descriptor、subject、retrieved_at、vault_blob_id、payload_hash、retention_class、expires_at |
| stats_sync_runs | id、scope_json、state、requested_at、finished_at、safe_error |
| metrics_snapshots | id、account_id、subject_ref、period_start/end、provider_timezone、dimensions_json、completeness、mapping_version、created_at |
| snapshot_raw_links | snapshot_id、raw_id、page/query role |
| metric_observations | snapshot_id、provider_key、canonical_key、definition_version、value_decimal/text、unit、value_status、dimensions_json |
| policy_checks | grant/account、policy_version、authorization_checked_at、subject_existence_checked_at、next_check_at |
| data_deletion_jobs | scope、reason、deadline、state、backup_cutoff |
| audit_events | event_id、local IDs、safe actor/action、timestamp、safe details |
| schema_migrations | version、name、checksum、applied_at、app_version |

remote_objectsの識別はprovider＋account＋kind＋opaque ID。upload IDとmedia IDが同じ文字列でも別扱いする。1:nの返却を表現し、joinで曖昧な帰属を作らない。receipt/checkpointの機密部分はvault blob、非機密のremote IDは通常columnとする。

必須indexはjobs(state,due_at)、publications(state)、targets(post_id)、remote object identity、raw(expires_at)、snapshot(subject,period,retrieved_at)、observation(provider_key)。大きな時系列を全件読んでstats showしない。doubleでcountを丸めず、巨大countはdecimal文字列対応のCLI schemaとする。

unique client_request_idとintent_hash照合を同一transactionで実行する。hash衝突だけをidentityにせず、canonical intentも比較可能にする。publish jobの重複active登録を防ぐ部分unique indexとgeneration CASを使う。成功済みPublicationにpublish jobを作れない不変条件もApplicationで検証する。

## Filesystemとのcommit境界

素材はprivate tmpへstream copyしhash・validation・flush後、同一volumeのspoolへatomic renameする。その後DB transactionで参照を登録する。DB commit失敗時は未参照fileが残り得るが、queueが存在しない素材を参照する順序を避ける。startupでtmp/orphanを猶予期間付き清掃する。copy/renameの途中で失敗したものを完成素材と見なさない。

spoolをユーザーが消した場合、該当未公開対象をNeedsAttentionとして止める。remote IDがある既公開対象まで再作成しない。DBとfileを同一transactionにできないことをrecoveryで補う。

## Raw storageと容量

MVPは小規模運用として、圧縮後暗号化したmetrics responseをSQLite BLOBへ保存する。HTTP応答bytes上限と展開後上限を設ける。認証responseのraw保存はしない。headersはrequest ID、API version、rate metadata等allowlistだけ。生のAuthorization、Cookie、署名URLは除く。

大きなmediaはDB外。rawの長期増大はretention、圧縮、query限定、収集間隔で抑える。100 Publication/月×20 snapshot×20KBならraw約40MB/月という容量試算を置くが、実測値として扱わない。default DB警告2GB、管理spool上限は明示configで設定し、disk不足ならenqueue/HTTP継続を止める。大容量対応が必要になってからraw blob portのfile backendを検討し、初期から別object DBを持たない。

## Migration

連番の埋込SQL migration、immutable checksum、schema_migrations tableを採用する。手書きSQLの対象DB versionとbackup方法をreviewする。既存migrationを書き換えず次versionで修正する。provider checkpoint/options/metric mapping versionはDB schema versionとは別。

db migrateはworkerと書込CLIを停止するmaintenance lockを取得し、online backup、空き容量、整合性を確認する。transaction内で適用可能な変更は全体commit。table再構成はcopy・制約/件数確認・swapし、失敗時rollbackする。transaction外が必要な操作は明示段階とrecovery markerを持つ。

新binary起動時に古いschemaなら送信を開始せずmigrationを案内する。新しすぎるschemaなら読書を拒否する。未読checkpointは新規publishに変換せずNeedsAttention。down migrationはMVPなし。復旧はbackup restoreだが必ずquarantineとなる。

## Backup / corruption / restore

稼働中のDB file単体copyは禁止。SQLite online backup APIを使い、spool manifest/hashと参照素材を含めた整合backupを作る。WALを無視したcopyをbackup成功と呼ばない。quick_checkを起動時、integrity_checkをdb check/restore時に行う。異常時はremote mutationを止め、元DBを上書き修復しない。

default backupは同一PCのACL保護領域、7日rotation。DBには本文・ID等の平文個人情報があり、token暗号化だけでbackup全体が秘密化されたとは説明しない。外部持出しは利用者管理の暗号化volume等を必要条件とする。

master keyはDB backupへ平文同梱しない。同一profileで復号可能かrestore時に確認する。別PCでkeyがない場合、token/checkpointは復号できず再認証とremote照合が必要。keyを失ったupload sessionは安全に新uploadへ置き換えられない。MVPではportable key exportを提供しない。

restoreは全jobをquarantineし、復元時点より後のremote副作用が存在し得ることを提示する。ローカル記録が古い場合の自動再投稿をしない。[復旧詳細](scheduling.md)

## Retentionと削除

provider policyによる期限をraw、projection、export、backupに伝播させる。immutable snapshotとは値を上書きしない意味であり、規約に基づく削除を禁止しない。削除後は値・remote個人情報を残さない最小tombstoneだけを許容する。

期限到来・disconnect・削除要求時はjob停止、DB対象行とvault blob削除、該当managed export/spool削除、該当backupの削除または再生成を行う。backupはrow単位で消せないため、影響するbackup全体を削除する選択をdefaultとする。restore前に最新の外部削除要求・retention条件を適用できないbackupは利用しない。

SQLiteはdeleteだけで全physical bytesの即時消去を保証しない。secure_delete設定、reader停止後のcheckpoint/truncate、適切なVACUUM、古いbackup削除を運用に含める。SSD/wear levelingや外部コピーの完全消去は保証しない。暗号化rawの削除も旧backupとkeyの残存を考慮する。

YouTube等の保持条件は[Analytics](analytics.md)に定義する。PC停止中の期限超過をソフトだけで回避できない。長期停止前にはmanaged provider dataのpurgeを行う運用を明記し、起動直後に期限処理が終わるまでdata表示/再送信を禁止する。利用者が管理外へコピーしたexportは自動回収できず、export時に期限と削除責任を付記する。
