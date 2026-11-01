# Sitesの階層と更新差分の比較試験

## 対象と基準

Issue #161 / #145 の比較を `SitesWorkloadTests` で行う。実行先は既存のテスト用DB制限を通した専用DBのみで、実環境の設定・レコード・計画は含めない。新しい自動候補や適用オプションを追加する変更ではない。

参照日: 2026-11-01。Pleasanterの固定参照は `664c833b144d9da9b6bb58e60f7e48eb01141e58`。本体を依存・コピー・ビルド対象にせず、定義と呼出経路の事実確認にだけ使用した。

- [SiteIdの定義](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Definition_Column/Sites_SiteId.json): bigint、主キー第2列、Ix1第2列、Ix2第1列、Uniqueあり。PkMySqlの指定はない。
- [更新日時の基底定義](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Definition_Column/_BaseItems_UpdatedTime.json): 主キー第3列とIx1第3列、いずれも降順。
- [ReferenceType](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Definition_Column/Sites_ReferenceType.json) / [Form](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Definition_Column/Sites_Form.json): それぞれIx1先頭、Ix4。型は長さ32の文字列、FormはNULLを許す。
- [ParentId](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Definition_Column/Sites_ParentId.json): bigint、標準索引の指定なし。
- [Menu](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Models/Sites/SiteUtilities.cs#L5464): TenantIdとParentIdで取得し、非特権では権限条件を追加。メニュー順と一部表示可否は取得後に処理する。
- [SetSites](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Libraries/Server/SiteInfo.cs#L492): TenantIdとUpdatedTimeの範囲条件、本文や設定を含む投影。

試験表は関係する列だけを独立定義する。主キーの列順は3DBとも `(TenantId,SiteId,UpdatedTime)` で、更新日時の方向はSQL Server / MySQLが降順、PostgreSQLが昇順。定義JSONだけから全DBの主キーを降順とは扱わない。[PostgreSQLの主キー用テンプレート](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Sqls/PostgreSQL/CreatePk.sql)と[生成時の方向指定除外](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.CodeDefiner/Functions/Rds/Parts/Indexes.cs#L305)を照合した。

通常索引 `(ReferenceType,SiteId,UpdatedTime DESC)`、一意な `(SiteId)`、通常索引 `(Form)` も基準に含める。MySQLでディレクトリ系と同じID単独主キーへ置き換えない。

## 比較条件

400件・4,000件と均等・偏りのあるテナント分布を独立に組み合わせた4シナリオを使う。親はルート0と17種類、更新日時は一意な秒単位、ReferenceTypeとNULLを含むFormを生成する。DB間で同じレコードを用いる。

| 段階 | 標準に追加する索引 |
| --- | --- |
| standard | なし |
| parent | TenantId,ParentId,SiteId |
| delta | TenantId,UpdatedTime |
| combined | parentとdeltaの両方 |

各段階でデータを入れ直し、統計更新後、書き込み試験より先に読み取りを測る。ルート・子・別テナント・空テナント・空の親・更新差分の半数境界と末尾境界・単一ID・ReferenceType・Formの10条件を、IDだけの投影と幅を持つ投影の両方で測る。パラメータ型はTenantIdがInt32、ID/親がInt64、更新境界がDateTime、その他がString。PostgreSQLの更新境界はNpgsqlのTimestampを明示し、timezoneなしの試験列と揃える。

SQLのORDER BYは付けない。期待ID集合はC#側の独立した条件で求め、計測後にソートして全件・重複を照合する。アプリのメニュー順を索引で代替したとは扱わない。1回ウォームアップ後の5回を保存し、クライアント経過時間の中央値と各試行を記録する。

挿入50件、親変更と更新日時変更100件、更新日時のみ100件、削除50件について、rollbackとcommitを分けて測る。commitの計測には確定時間を含め、その後に対象値を復元する。全工程後に全列・NULL・件数・ID重複を照合する。索引容量は読み取り前にSQL Serverの使用ページ、PostgreSQLの実サイズ、MySQLの統計上の推定サイズを記録する。

## 出力と限界

`scripts/test.ps1 -Dbms SQLServer`（PostgreSQL / MySQLも同様）で実行する。`sites-workload-<DBMS>-<件数>-<分布>.json` を4ファイルずつ出力し、CIの合成比較アーティファクトにも保存する。

- `Reports`: 段階・条件・投影・件数・時間と実行計画。SQL Serverは実行後XML、PostgreSQLはANALYZE/BUFFERS付きJSON、MySQLはEXPLAIN ANALYZE。
- `Writes`: 段階・操作・commit/rollback・件数と時間。
- `IndexSizes`: 追加索引ごとの容量。MySQLは推定値であることを明示する。

## 合成データでの観測（2026-11-01）

4,000件・偏りありの例では、Tenant 1に3,240件、親7の条件に152件、更新差分の半数境界に1,620件が該当した。次はstandardから対応する単独索引を追加した場合の比較で、実環境の計測値ではない。

| DB | 親7、IDのみ | 親7、幅あり | 更新差分半数、IDのみ | 更新差分半数、幅あり |
| --- | --- | --- | --- | --- |
| SQL Server | 論理読み取り237→4 | 237→237 | 15→8 | 237→237 |
| PostgreSQL | rootのshared hit 167→122 | 167→122 | 167→167 | 167→167 |
| MySQL | 主キーのTenant lookup 3,240行→親索引lookup 152行 | 同じ条件で親索引lookupを選択 | 主キーのTenant lookupを維持 | 主キーのTenant lookupを維持 |

PostgreSQLの上記shared readは0。親条件はSeq ScanからBitmap Heap Scanへ変わり、更新差分の半数条件はSeq Scanを維持した。ANALYZEのみでVACUUMやvisibility mapの固定は行っていない。SQL Serverの幅あり条件では親・差分索引を追加しても走査の読み取りが残る。この結果から、ID投影の改善を実際の幅広いメニューや設定取得へ一般化しない。

親索引 / 差分索引の容量はSQL Serverが152 / 120 KiB、PostgreSQLが176 / 144 KiB、MySQLの推定値が144 / 128 KiBだった。両方を追加した段階でもそれぞれの容量を別に保存する。

100件を変更してcommitするクライアント時間の中央値（ms、ウォームアップ除外5回）は次のとおり。

| DB / 操作 | standard | parent | delta | combined |
| --- | --- | --- | --- | --- |
| SQL Server / 親と更新日時 | 7.72 | 7.91 | 8.57 | 10.17 |
| SQL Server / 更新日時のみ | 7.03 | 8.03 | 7.81 | 8.66 |
| PostgreSQL / 親と更新日時 | 3.52 | 3.29 | 3.26 | 3.73 |
| PostgreSQL / 更新日時のみ | 3.35 | 4.34 | 3.01 | 3.58 |
| MySQL / 親と更新日時 | 9.26 | 8.89 | 8.93 | 8.14 |
| MySQL / 更新日時のみ | 7.41 | 8.79 | 7.09 | 8.29 |

同一ホスト上の短時間の合成試験であり、追加索引のある段階の方が速い数値もノイズやキャッシュ等を含む。DB間の絶対速度の比較や、更新が必ず改善・悪化するとの判断には使わない。今回は親候補の継続評価と既存の差分用途との分離を判断し、新しい共通索引の自動採用は見送る。

## 評価の範囲

標準の関連キーを揃えた比較であり、本体の全列、既定値、履歴・削除表、照合順序、巨大な本文や設定の幅、CodeDefinerのDDL全体を再現するものではない。幅のある投影は256文字の固定Payloadを含む比較用で、実際のSiteSettings等と同じ転送量・評価費用ではない。非特権のHasPermissionと取得後の権限・表示条件・メニュー並べ替え、UI通信は対象外。ID照合から認可ロジックの正しさや画面全体の速度を保証しない。

SQL Serverの論理読み取りとPostgreSQLのbuffer hit/readは同じ単位でない。キャッシュを強制排除せず、速度の大小でテストを失敗させない。自動採用には実際の投影・権限・選択率・更新費用を合わせた追加判断と、[用途単位の管理範囲](../_documents/管理系共通索引の採用と管理方針.md)が必要。
