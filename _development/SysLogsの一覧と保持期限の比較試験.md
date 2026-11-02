# SysLogsの一覧と保持期限の比較試験

## 目的と固定参照

Issue #161 / #145 のログ比較を `SysLogsWorkloadTests` へ分離する。既存のAdminLogFixtureは代理表のCreatedTime順であり、本体の主キー差とUpdatedTime順の前後移動を同じものとして扱わない。製品の自動候補・適用対象は追加しない。実行先は既存の専用DB名制限を通すテストDBのみ。

参照日: 2026-11-02。Pleasanterのコミット `664c833b144d9da9b6bb58e60f7e48eb01141e58` を事実確認にだけ用いた。本体をコピー・依存・ビルド・配布しない。

- [CreatedTimeのキー定義](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Definition_Column/SysLogs_CreatedTime.json): 主キー第1列、降順。
- [SysLogId](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Definition_Column/SysLogs_SysLogId.json): bigint、主キー第2列、通常索引の先頭、Identity、MySQLでは単独主キー。
- [PostgreSQLの主キーDDL](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Sqls/PostgreSQL/CreatePk.sql): 主キーの方向指定を除く。
- [MySQLのキー生成](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.CodeDefiner/Functions/Rds/Parts/MySql/MySqlIndexes.cs): MySQL用主キーへの変更時に元の主キー列順の通常索引も生成し、通常索引の番号をずらす。
- [SysLogType](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Definition_Column/SysLogs_SysLogType.json)はint。[UpdatedTime](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Definition_Column/SysLogs_UpdatedTime.json)と異なる日時列のCreatedTimeを、前後移動の既定順とはしない。
- [GetSwitchTargets](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Models/SysLogs/SysLogUtilities.cs#L1790): Viewの条件・並べ替えにUpdatedTime降順を追加。正の上限の場合のみCOUNTし、件数が上限以内ならIDを取得。現在IDが含まれなければ末尾へ加える。
- [PhysicalDelete](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Models/SysLogs/SysLogUtilities.cs#L2670): CreatedTimeが保持期限未満のログを削除。正のチャンクサイズでは期限対象のID最小・最大を取得し、各ID区間にも日時条件を維持する。

## 試験の条件

関係する標準キーを次のように揃える。全ての本体DDLを再現した表ではない。

| DB | 主キー | 通常索引 |
| --- | --- | --- |
| SQL Server | CreatedTime DESC,SysLogId | SysLogId |
| PostgreSQL | CreatedTime,SysLogId | SysLogId |
| MySQL | SysLogId | CreatedTime DESC,SysLogId と SysLogId |

400 / 4,000件と均等 / 偏りを独立に組み合わせた4シナリオを使う。均等は4種類、偏りは種別1が95%、種別2が5%。ログIDと作成日時の順序は一致させず、保持対象と期限対象がID区間内に混在する。UpdatedTimeもCreatedTimeと異なる順序にする。日時は秒単位で各行に一意とし、同時刻ログの並びは評価しない。固定長256文字のPayloadを持たせる。

| 段階 | 標準に追加する索引 |
| --- | --- |
| standard | なし |
| kind-created | SysLogType,CreatedTime DESC,SysLogId |
| updated | UpdatedTime DESC,SysLogId |
| combined | 両方 |

段階ごとに行を入れ直して統計更新する。各追加索引の容量は読み取り前に取得する。SQL Serverは使用ページ、PostgreSQLは実サイズ、MySQLは統計の推定値。読み取りは書き込み試験より先に行う。

### 一覧・COUNT・前後移動

全期間・半数期間・狭い期間・種別1・種別2・空種別・空期間の7条件を使う。CreatedTimeは下限以上・上限未満。種別条件は等価。UpdatedTime降順で、IDのみとPayload等を含む投影を比較する。C#で元データから独立に求めた順序付きIDと照合し、ウォームアップ後5回の時間を保存する。

日時はDbType.DateTime、PostgreSQLではtimezoneなしTimestampを明示する。種別はInt32。COUNTの実行計画は行取得と別に保存し、行取得が省略された場合にCOUNTが高速になったと判断しない。

前後移動は上限-1・0・500・該当件数の直前・同数・直後を重複除外して評価する。上限0以下ではCOUNTも行取得も行わない。同数を含む閾値内だけ行取得し、条件外の現在IDの補完と、全件に含まれる現在IDを重複させないことを照合する。各境界の時間は単発の診断値であり、5回試行の一覧ベンチマークと混同しない。

### ログ追記・保持期限処理

100件追記、期限対象の一括削除、ID区間100件ごとの削除を測る。チャンク削除ではDBからMIN/MAX IDを取得し、各区間にもCreatedTimeの条件を付ける。削除件数だけでなく、保持対象を含む全ての残存IDを、元データから求めた期待集合に照合する。ここでは半数のログが期限対象になる固定の基準時刻を使う。

各操作はcommit / rollbackを分け、ウォームアップ後5回を保存する。commitの計測には確定時間を含み、rollbackの時間には取消自体を含めない。計測後の照合・復元は計測外。commitした削除は元の行で復元し、追加分は削除する。段階の最後に全列・全行を照合する。保持対象なしの場合のチャンク分岐、実時間からの期限算出、長期のログ成長はこの試験の対象外。

本体の各チャンクの呼出と確定境界を完全再現する試験ではない。比較試験では操作全体を一つのトランザクションへ入れ、全件を確実に復元できるようにしている。チャンク幅はID区間幅であり、該当行のないIDも区間に含むため、毎回その件数を削除する指定ではない。SQL Serverのロック・ログ量・実運用のチャンク調整を保証しない。

## 実行と成果物

`scripts/test.ps1 -Dbms SQLServer`（PostgreSQL / MySQLも同様）で既存の全統合試験と実行する。`syslogs-workload-<DBMS>-<件数>-<分布>.json` を4ファイルずつ出力し、CIでも合成比較のアーティファクトとして保存する。

- Reports: 各投影の結果件数・5回の時間・中央値・実行計画・独立したCOUNT計画。保持期限のMIN/MAX取得計画も含む。
- Switches: COUNTと行取得の有無、現在IDを補完した件数、各時間。
- Writes: 操作・確定/取消・変更行数・発行コマンド数・各時間。
- IndexSizes: 追加索引それぞれの容量とMySQLの推定値フラグ。

SQL Serverは実行後XML、PostgreSQLはANALYZE / BUFFERS付きJSON、MySQLはEXPLAIN ANALYZE。保持期限の計画はMIN/MAXを読むSELECTであり、削除DMLの実行計画ではない。削除自体は変更件数・残存集合・時間を測定する。読み取りの日時境界は型付きパラメータ、比較用の削除文は固定の合成日時リテラルであり、その計画をアプリの型付きクエリと同一視しない。

## 合成データでの観測（2026-11-02）

4,000件・偏りありのシナリオでは種別2が200件、狭い期間が400件、保持期限未満が2,000件だった。次は実環境のデータではない。

| DB | 種別2、standardからkind-createdへの変化 | UpdatedTime索引の単独追加 |
| --- | --- | --- |
| SQL Server | COUNTの論理読み取り436→3。行取得はIDのみ・幅ありとも436→436 | 全件ID取得436→17、幅あり436→436。狭い期間のID取得47→17、幅あり47→47 |
| PostgreSQL | COUNTのroot shared hit 160→160。ID取得のroot shared hit 160→163 | 全件・狭い期間のID取得はSortを維持、shared hit 160 / 56のまま |
| MySQL | COUNTは表走査4,000行からcovering range scan 200行へ。ID取得も種別索引のrange scanとSortへ変化 | 全件ID取得は表走査とSortを維持 |

PostgreSQLの上記shared readは0。SQL Serverでは両方を加えた種別2のID取得は20読み取りになったが、幅ありは436のままだった。MySQLでは種別指定のない全期間COUNTが追加後の種別索引のskip scanを選ぶ場合もあり、追加したこと自体を改善としない。

全4,000件のID取得が改善しても、上限500の前後移動ではその取得自体が省略される。合成比較の行取得はページングなしのプローブで、実際に実行されない取得経路の時間差を画面の改善率に使わない。逆に種別2の200件は上限内であり、COUNTと取得の両方を評価する必要がある。

kind-created / updatedの追加容量はSQL Server 120 / 136 KiB、PostgreSQL 176 / 144 KiB、MySQLの推定値128 / 112 KiBだった。100件追記と2,000件の期限削除のcommitを含む中央値（ms、ウォームアップ除外5回）は次のとおり。

| DB / 操作 | standard | kind-created | updated | combined |
| --- | --- | --- | --- | --- |
| SQL Server / 追記 | 12.36 | 12.61 | 14.79 | 13.07 |
| SQL Server / 一括削除 | 31.00 | 39.44 | 37.91 | 44.80 |
| SQL Server / チャンク削除 | 176.12 | 198.29 | 173.11 | 204.46 |
| PostgreSQL / 追記 | 8.41 | 9.39 | 6.78 | 6.15 |
| PostgreSQL / 一括削除 | 3.47 | 3.43 | 3.88 | 3.45 |
| PostgreSQL / チャンク削除 | 31.72 | 30.83 | 31.65 | 33.69 |
| MySQL / 追記 | 9.29 | 9.04 | 10.28 | 9.32 |
| MySQL / 一括削除 | 15.54 | 15.98 | 16.26 | 17.76 |
| MySQL / チャンク削除 | 49.64 | 64.34 | 51.63 | 88.36 |

この条件でチャンク削除はMIN/MAX取得1回とDELETE40回。単発のDELETEより長くても、運用でのロック保持や確定境界を揃えた比較ではなく、チャンク方式の廃止根拠にはしない。短い測定はキャッシュ・実行順序等の影響を受け、索引追加後の方が速い書き込みの値も改善保証ではない。

今回、新しいログ索引の自動採用は見送る。種別検索のCOUNT等に効果があっても、DB差、幅あり取得、実際の検索頻度、追記・期限削除の費用を合わせた採用条件が未確定のため。

## 評価の限界と採用条件

テスト表は標準の関連キーを持つが、本体全列・既定値・Identity採番・履歴/削除表・実際のログ本文幅・権限・Viewの任意JOIN/式・ページング・出力処理を再現しない。TenantIdを存在しないSysLogsへ補わない。SQL Serverの論理読み取り、PostgreSQLのbuffer、MySQLの行数や推定容量は同じ指標ではない。

統計更新のみで、VACUUMやキャッシュ・実行順序を固定しない。短時間の差を速度保証や合否に使わない。ログには高頻度の追記があり、検索の改善だけで索引を自動採用しない。実際の検索頻度・選択率・保持期限処理・追記負荷と、[管理系共通索引の管理範囲](../_documents/管理系共通索引の採用と管理方針.md)を揃えて判断する。
