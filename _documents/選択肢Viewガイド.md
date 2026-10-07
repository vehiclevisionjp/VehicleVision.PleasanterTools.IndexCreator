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

ChoicesText の `値,表示名,短縮名` を読みます。表示テキストが空なら値を使います。エスケープしたカンマに対応し、同じ項目で同じ値が繰り返される場合は最初の定義を使います。短縮名が空なら表示名を使います。項目ごとに View を作るため、同じサイトの ClassA と ClassB は別名で取得できます。固定選択肢がない項目の View は作りません。

この機能は設定に列挙された固定選択肢を対象にします。`[[サイトID]]`、ユーザー・組織・グループなどの動的な選択肢を含む場合は生成を停止します。区切り・置換は本体の General.json の ChoiceSplitRegexPattern / ChoiceReplaceRegexPattern / ChoiceReplaceRegexReplacement を読みます。

選択肢設定の変更後は再実行してください。不要なマスタ View は `choice-lists --prune` で計画を確認し、`_choice-lists --prune /y` で整理します。一覧 View と選択肢 View は別々に整理されます。

SQL View は Pleasanter のユーザー・レコード権限を判定しません。DB の参照権限を設定してください。SQL View 全体の導入と変更時の扱いは [サイト View ガイド](サイトViewガイド.md) を参照してください。
