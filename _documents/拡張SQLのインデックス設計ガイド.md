# 拡張SQLのインデックス設計ガイド

拡張SQLに必要なインデックスは、クエリ作成者が設計・追加・保守します。
ファイルとExtensionsテーブルのどちらに保存した拡張SQLも、IndexCreatorによる
索引候補生成・自動作成の対象外です。これは将来対応待ちではなく、管理範囲の区分です。
拡張サーバースクリプトから呼び出す拡張SQLも同じ扱いです。

IndexCreatorが表示する拡張SQLの介入フラグは、通常の候補解析に影響する可能性を
知らせる診断です。そのSQLに適切な索引があることを保証しません。

## 最初に検索の実態を記録する

対象SQL、呼び出し箇所、パラメータの型、返却件数、実行頻度を記録します。
値によって件数が大きく異なる場合は、少数・多数・0件になる条件を用意します。
SQLの実行時間と画面全体の応答時間は分けて測ります。
実行計画の推奨だけで追加せず、統計情報の状態や待機・ロックも確認してください。

1. 同じSQLとパラメータで、変更前の実行計画・時間・読み取り量を複数回保存する。
2. 既存索引を調べ、一つの候補を検証環境で追加する。
3. 同じ条件で再測定し、返却結果と並び順の意味が変わらないことを確認する。
4. 主な別パラメータと、通常の登録・更新・削除の負荷も比較する。
5. 改善が確認できた候補だけを本番へ適用し、通常運用の頻度・容量・待機を観察する。

初回とキャッシュが温まった回を分け、中央値とばらつきを比較します。
本番のキャッシュ全消去を比較の前提にしないでください。
権限やテナントの条件を取り除いたSQLでは、元の処理の性能を評価できません。

## 列の選び方

| SQLの要素 | 検討すること |
| --- | --- |
| 等価条件 | 常に指定する範囲のキーと絞り込み列を先頭候補にする。列順は他の利用SQLも含めて決める |
| JOIN | 結合先の検索キーと、同時に適用する条件を組み合わせる |
| 範囲条件 | 等価条件に続く範囲キーを検討する。範囲より後ろの列だけで全体のソートが消えるとは限らない |
| ORDER BY | 等価条件の後に並べ替え列を置く候補を比較する。ASC/DESC、NULL、同値時の順序も確認する |
| 取得列 | 必要な列だけ取得する。カバーのために長い本文を大量に索引へ追加しない |
| OR・式・部分一致 | 通常の複合索引一つで全条件を処理できると考えない。分岐ごとの計画や専用機能を別途評価する |

列名だけでなく、実際の型・長さ・照合順序とパラメータ型を確認します。
暗黙の型変換や列側の関数がある場合は、索引を追加しても検索に使われないことがあります。
SiteIdやTenantIdは実際の検索・テーブル構造に基づいて選び、すべての表へ一律に追加しません。
小さい表のスキャンは正常な選択の場合もあります。
基本的な考え方は[SQL Serverの索引設計資料](https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-index-design-guide?view=sql-server-ver17)も参照してください。

## 既存索引との重複を確認する

名前ではなく、キーの順序・方向、含まれる列、フィルター条件、式、照合順序、
有効性、主キー・一意性を照合します。同名だから同じ定義とは限りません。
広い索引で検索可能でも、狭い索引と読み取り量や更新費用が同じとは限りません。
標準索引や主キー・一意制約を、独自クエリのために削除・変更しないでください。

| DBMS | 定義を確認する入口 |
| --- | --- |
| SQL Server | `sys.indexes`、`sys.index_columns`、`sys.columns`。キーとINCLUDE、フィルター、有効性を確認 |
| PostgreSQL | `pg_indexes`、`pg_index`、`pg_get_indexdef`。式・部分索引と`indisvalid`も確認 |
| MySQL | `SHOW INDEX FROM ...`、`SHOW CREATE TABLE ...`。プレフィックス長や可視性も確認 |

## 架空の候補例

以下は説明用の`IndexGuideSample`テーブルを想定した例です。
`ScopeId`、`CategoryId`、`RecordId`は整数、`OccurredAt`は日時とします。
Pleasanterの実テーブルへそのまま適用するDDLではありません。
実環境での効果や作成可否は未検証です。

「ScopeIdとCategoryIdが等しい行を、OccurredAt、RecordIdの昇順で取得する」検索なら、
`(ScopeId, CategoryId, OccurredAt, RecordId)`を候補として比較します。
ScopeIdが条件にない検索にも同じ性能を期待することはできません。

SQL Server（`ONLINE`対応環境でのみ実行）：

```sql
CREATE INDEX IX_custom_IndexGuide_scope_category_time
ON dbo.IndexGuideSample (ScopeId, CategoryId, OccurredAt, RecordId)
WITH (ONLINE = ON);
```

PostgreSQL（専用セッションで、トランザクションブロックの外で実行）：

```sql
CREATE INDEX CONCURRENTLY ix_custom_indexguide_scope_category_time
ON public.index_guide_sample (scope_id, category_id, occurred_at, record_id);
```

MySQL（InnoDBの対応する通常のセカンダリ索引）：

```sql
ALTER TABLE IndexGuideSample
  ADD INDEX IX_custom_IndexGuide_scope_category_time
    (ScopeId, CategoryId, OccurredAt, RecordId),
  ALGORITHM=INPLACE, LOCK=NONE;
```

作成オプションが使えない場合、オンライン指定を外して本番で再試行しないでください。
対象バージョン・型・エンジンの制限を確認し、必要なら停止時間を確保します。

## DBMS別の測定と作成時の注意

### SQL Server

実際の実行計画と`SET STATISTICS IO ON`、`SET STATISTICS TIME ON`で比較し、
測定後はOFFに戻します。論理読み取り、CPU、経過時間、実行行数を保存します。
計画のSeekという表示だけでなく、残余条件や読み取った行数、Lookup、Sortも確認します。
参照：[STATISTICS IO](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-statistics-io-transact-sql?view=sql-server-ver17)。

`ONLINE`の対応は製品・エディション等に依存します。対応環境でもロック待ちや
作業領域・ログの消費を監視し、実行枠とロック待ち上限を決めます。
参照：[オンライン索引操作](https://learn.microsoft.com/en-us/sql/relational-databases/indexes/perform-index-operations-online?view=sql-server-ver17)。

### PostgreSQL

`EXPLAIN (ANALYZE, BUFFERS)`で実行時間、実行行数とループ回数、バッファを比較します。
ANALYZEは対象SQLを実行するため、まず副作用のないSELECTを選びます。
更新SQLや副作用のある関数を本番で性能確認のために実行しないでください。
参照：[EXPLAINの利用](https://www.postgresql.org/docs/18/using-explain.html)。

`CREATE INDEX CONCURRENTLY`はトランザクションブロック内では実行できません。
長いトランザクション等を待つことがあり、失敗後には無効な索引が残る場合があります。
カタログで有効性を確認し、自分が作成した索引だけを復旧対象にします。
INCLUDE列を足しても常にIndex Only Scanになるとは限りません。
参照：[CREATE INDEX](https://www.postgresql.org/docs/18/sql-createindex.html)。

### MySQL

MySQL 8.4では`EXPLAIN ANALYZE SELECT ...`で実際の時間・行数・ループ回数を比較します。
これもSQLを実行します。通常の`EXPLAIN`による推定と実測を区別してください。
SQL Serverのlogical readsと同じ数値がこの出力にあるとは扱いません。
参照：[EXPLAIN](https://dev.mysql.com/doc/refman/8.4/en/explain.html)。

InnoDBでは`ALGORITHM=INPLACE, LOCK=NONE`を明示し、非対応なら停止させます。
オンラインDDLでもメタデータロックや負荷を確認してください。
SQL Server/PostgreSQLのINCLUDEをそのまま移植せず、TEXTのプレフィックス索引も
完全な列の索引と同じカバー・並べ替え性能とは扱いません。
参照：[オンラインDDL](https://dev.mysql.com/doc/refman/8.4/en/innodb-online-ddl-operations.html)。

## 所有者と変更履歴を残す

独自索引には組織で定めた名前を使います。上の`IX_custom_`は一例です。
IndexCreator管理用の`IX_vvplic_`接頭辞は使用しないでください。
IndexCreatorが既存の独自索引をカバー判定に利用しても、その索引の保守責任は移りません。

索引ごとに次の情報をクエリ作成者の管理場所に保存します。

| 記録 | 内容 |
| --- | --- |
| 所有者と用途 | 担当者、拡張SQLの識別名、対象DB・スキーマ・テーブル |
| 定義 | 作成DDL、期待する定義、撤去DDL、対象バージョン |
| 測定 | 比較条件、変更前後の結果・計画・時間・読み取り、書込負荷と索引容量 |
| 適用 | 実施日時、結果、作成後の有効性、失敗・取消時の復旧記録 |
| 再確認 | 拡張SQL変更・廃止、CodeDefiner実行、本体更新後の確認担当 |

拡張SQLのファイルやExtensionsレコードを削除・無効化しても、独自索引は自動削除されません。
他のSQLが利用していないかを調べ、撤去も所有者が行います。
CodeDefinerや更新後は索引が残っているか、定義が適合するかを確認し、必要なら再適用します。
撤去前には、その索引で充足していたIndexCreatorの候補も再計画してください。
接続情報・実パラメータ・個人情報を含む計画は公開Issueへ添付せず、共有用には合成データを使います。

公式資料の確認日：2026-11-01。各例は設計説明用で、３DBでの実行テストや性能検証は未実施です。
