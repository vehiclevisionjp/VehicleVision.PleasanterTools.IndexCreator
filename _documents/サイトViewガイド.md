# サイト一覧の SQL View

Results / Issues / Wikis の各サイトに、一覧の表示列と項目名を反映した SQL View を作ります。インデックスの操作とは別のコマンドです。件数の下限に関係なく対象サイトの View を生成します。
Wikis には Status と Class / Num / Date / Check / Description / Attachments 列がないため、一覧列に指定すると停止します。

```powershell
dotnet IndexCreator.dll views /p "C:\web\pleasanter\Implem.Pleasanter"
dotnet IndexCreator.dll _views /p "C:\web\pleasanter\Implem.Pleasanter" /y
```

`views` は計画のみ、`_views` / `views-apply` は適用です。`/c` は確認のみ、`/y` は入力の省略です。接続設定はインデックス操作と共通です。適用には対象スキーマでの View 作成・変更権限が必要です。MySQL の本体 owner 接続に標準で付く権限には CREATE VIEW / SHOW VIEW が含まれないため、DB 管理者が追加するか、必要な権限を持つ接続を INDEXCREATOR_CONNECTION_STRING に指定してください。

## 名前と表示列

名前は `View_vvplic_{ReferenceType}_{SiteId}_{SiteName}`。ReferenceType は Results / Issues / Wikis の参照種別です。例えば `View_vvplic_Results_100_案件一覧` です。サイト名は Sites.Title から取得します。記号や空白は `_` にまとめ、空の名前は `Untitled` とします。3 DB 共通で UTF-8 の 63 バイト以内になるようサイト名部分を短縮します。コンソールには日本語を Unicode エスケープで表示します。

GridColumns の順序で列を作り、GridLabelText、LabelText、本体の列定義、物理列名の順で列名を決めます。同じ列名には物理列名を付記します。GridColumns が省略されている場合は `/p` の本体にある列定義から既定の一覧列を読みます。Title は Items のタイトルを取得します。TitleBody も SQL View ではタイトルを返します。

## 列名を選ぶ

一覧 View の列名は `/names` で選べます。

| 指定 | 列名 |
| --- | --- |
| `/names label` | Pleasanter の表示名。上の規則で決めます。既定です |
| `/names column` | サイト設定の Columns の `ColumnName`（`ResultId`、`ClassA`、`NumA` など） |

`column` では、表示名の重複を避ける処理は要りません。`TitleBody` は Items のタイトルを返し、列名は `TitleBody` のままです。GridColumns に同じ列が2回あると停止します。選択肢 View の列は Value / Text / TextMini で固定なので、`/names` は一覧 View だけで使えます。

```powershell
dotnet IndexCreator.dll _views /names column /y
```

途中で指定を変えると、View の列名が変わります。この View を使うクエリや BI がある場合は、最初に決めて固定してください。PostgreSQL は列名の変更を `CREATE OR REPLACE` ではできないため、`/f` を付けないと停止します。
値は DB の格納値です。分類の表示ラベル、ユーザーの表示名、リンク先の表示値、添付表示、書式、保存ビューの絞り込み・並べ替えは再現しません。リンク先項目や計算項目の列がある場合は、列を省略せず生成を停止します。取得順序が必要な場合は SELECT に ORDER BY を指定してください。

SQL View は Pleasanter のユーザー・レコード権限判定を実行しません。DB の参照権限を別途設定してください。

## 対象から除外する

View を作らないサイトは2つの方法で指定できます。どちらもカンマ区切りで複数の SiteId を指定でき、併用もできます。選択肢 View（`choice-lists`）でも同じ指定を使えます。

| 指定 | 除外する範囲 |
| --- | --- |
| `/exclude-tree <SiteId,...>` | 指定したサイト（通常はフォルダ）と、その配下の階層すべて |
| `/exclude-site <SiteId,...>` | 指定したサイトだけ。フォルダを指定しても配下は除外しない |

```powershell
dotnet IndexCreator.dll views /exclude-tree 10 /exclude-site 205,318
```

階層は Sites.ParentId で判定します。`/sites` で計画する場合は、フォルダを含む全サイトの行に ParentId を入れてください。存在しない SiteId を指定すると停止します。除外したサイトの既存の管理 View は、`/prune` を付けた場合に不要として削除されます。計画の一覧で確認してから適用してください。

## サイト構成を変更したら

再実行すると表示列と項目名を更新します。サイト名の変更は新しい名前の View を作ります。不要になった管理 View は、計画を確認してから `/prune` で整理します。

```powershell
dotnet IndexCreator.dll views /prune
dotnet IndexCreator.dll _views /prune /y
```

削除するのは本ツールの命名規則に一致する View だけです。古い名前を参照している SQL や依存 View がある場合は、先に参照を変更してください。新しい名前の View に既存の参照権限は自動継承しません。

PostgreSQL では列名・列順の変更を通常の置換で処理できないため停止します。依存と権限を確認後、`_views /f /y` で再作成できます。テーブル単位の外部 GRANT を復元し、依存 View や列単位の権限がある場合は自動再作成せず停止します。

`/output views.sql` で計画を保存できます。SQL Server の GO は SQL クライアントのバッチ区切りです。PostgreSQL の列構成変更時は、出力された CREATE OR REPLACE だけでは適用できません。インデックスと同様、本体更新・CodeDefiner・サイト編集と同時に適用しないでください。
