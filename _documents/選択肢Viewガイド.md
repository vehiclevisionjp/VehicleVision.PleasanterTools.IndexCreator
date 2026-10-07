# 選択肢マスタの SQL View

各サイトの項目に設定された固定選択肢を、SQL から値と表示テキストの一覧として取得できます。

```powershell
dotnet IndexCreator.dll choice-lists
dotnet IndexCreator.dll _choice-lists /y
```

`choice-lists` は計画、`_choice-lists` / `choice-lists-apply` は作成・更新です。`/p`、`/c`、`/y`、`--output` は一覧 View と共通です。

名前は `View_vvplic_ChoiceList_{ReferenceType}_{SiteId}_{ColumnName}_{SiteName}`。例えば `View_vvplic_ChoiceList_Results_100_ClassA_案件一覧` です。UTF-8 の最大63バイトに収めるためサイト名部分を短縮します。

| 列 | 内容 |
| --- | --- |
| Value | DB に保存する選択肢の値 |
| Text | 選択肢の表示名 |
| TextMini | 選択肢の短縮名 |

ChoicesText の `値,表示名,短縮名` を読みます。表示テキストが空なら値を使います。エスケープしたカンマに対応し、同じ項目で同じ値が繰り返される場合は最初の定義を使います。短縮名が空なら表示名を使います。項目ごとに View を作るため、同じサイトの ClassA と ClassB は別名で取得できます。固定選択肢がない項目、および ControlType が空または ChoicesText 以外の項目の View は作りません。

View はサイト設定を参照のたびに DB の組込関数で読み取ります。選択肢の追加・変更・削除は再実行しなくても反映されます。選択肢を持つ項目やサイトを追加した場合、サイト名を変更した場合は再実行してください。SQL Server では SQL Server 2022 以降または Azure SQL（互換性レベル 130 以上）が必要です。詳しい規則と各 DB の要件は [生成ルール](生成ルール.md#選択肢-view) を参照してください。

この機能は設定に列挙された固定選択肢を対象にします。`[[サイトID]]`、ユーザー・組織・グループなどの動的な選択肢を含む場合は生成を停止します。View の作成後に追加された動的な選択肢の行は、View の結果に含めません。区切り・置換は本体の既定の規則に従います。General.json の ChoiceSplitRegexPattern / ChoiceReplaceRegexPattern / ChoiceReplaceRegexReplacement を変更している場合は生成を停止します。

特定のフォルダ配下やサイトを対象外にするには、一覧 View と同じ `--exclude-tree` / `--exclude-site` を使います。詳しくは [サイト View ガイド](サイトViewガイド.md#対象から除外する) を参照してください。

不要なマスタ View は `choice-lists --prune` で計画を確認し、`_choice-lists --prune /y` で整理します。一覧 View と選択肢 View は別々に整理されます。

SQL View は Pleasanter のユーザー・レコード権限を判定しません。DB の参照権限を設定してください。SQL View 全体の導入と変更時の扱いは [サイト View ガイド](サイトViewガイド.md) を参照してください。
