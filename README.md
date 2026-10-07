# VehicleVision.PleasanterTools.IndexCreator

Pleasanter のサイト構成から必要なインデックスを計画・作成・更新する .NET 10 コンソールアプリです。SQL Server・PostgreSQL・MySQL に対応し、CodeDefiner と同じように本体の設定を読んで実行します。コンソール出力は英語固定です。

## 配置する

.NET Runtime 10 を用意し、配布 ZIP の `IndexCreator` フォルダを `Implem.Pleasanter`・`Implem.CodeDefiner` と同じ階層へ置きます。

```text
pleasanter/
├── Implem.Pleasanter/
├── Implem.CodeDefiner/
└── IndexCreator/
    └── IndexCreator.dll
```

接続設定は本体の `App_Data/Parameters/Rds.json` を使います。追加索引を適用する前に **`DisableIndexChangeDetection` を `true`** にしてください。配置・接続・Runtime の詳細は [導入手順](_documents/導入手順.md) を参照してください。

## 使う

`IndexCreator` フォルダで計画を確認し、適用します。

```powershell
dotnet IndexCreator.dll plan
dotnet IndexCreator.dll _rds
```

`_rds` は `apply` の別名で、計画表示後に `yes` の入力を求めます。CodeDefiner と同じ `/p` で本体フォルダを指定し、`/y` で入力を省略できます。

```powershell
dotnet IndexCreator.dll _rds /p "C:\web\pleasanter\Implem.Pleasanter" /y
```

## サイト構成を変えたら

再実行で不足分を補います。不要な管理索引も整理する場合は、計画を確認してから `/prune` を付けます。

```powershell
dotnet IndexCreator.dll plan /prune
dotnet IndexCreator.dll _rds /prune /y
```

管理名は `IX_vvplic_{ReferenceType}_{SiteId}_{用途}_{定義ハッシュ16桁}`。同じ物理索引を複数サイトが使う場合は共有します。標準索引・他ツールの索引・列・テーブルは削除しません。新規作成と確認が成功してから古い管理索引を整理します。CodeDefiner がテーブルを作り直した後も `_rds /y` を再実行してください。

## サイト一覧の SQL View

表示列の順序と項目名を反映した View も作成できます。名前は `View_vvplic_{ReferenceType}_{SiteId}_{SiteName}` です。

```powershell
dotnet IndexCreator.dll views
dotnet IndexCreator.dll _views /y
```

対応範囲と変更時の扱いは [サイト View ガイド](_documents/サイトViewガイド.md) を参照してください。

固定選択肢のマスタ View も `choice-lists` / `_choice-lists` で生成できます。選択肢の編集は再実行しなくても View に反映されます。[選択肢 View ガイド](_documents/選択肢Viewガイド.md) を参照してください。

特定のフォルダ配下やサイトは `/exclude-tree` / `/exclude-site` で View の対象から外せます。

## 稼働中の環境で実行する

索引はオンラインで作成し、ロック待ちは `/lock-timeout`（既定5秒）で打ち切って再試行します。業務を止めずに実行できるよう設計しています。オンライン作成に対応しない SQL Server のエディションでは停止するので、保守時間帯に `/offline` を付けて実行してください。詳しくは [利用ガイド](_documents/利用ガイド.md#稼働中の環境で実行する) を参照してください。

## 現在の対応範囲

Results・Issues・Wikis の保存ビューの通常のフィルタ・関数を伴わない並べ替え、リンク項目、サマリを解析する初期版です。実運用データでの性能測定は未実施です。検証の範囲は [導入手順](_documents/導入手順.md) を参照してください。式索引などの未対応条件とキーサイズの制約は [利用ガイド](_documents/利用ガイド.md) を参照してください。

## 資料

- [導入手順](_documents/導入手順.md): Runtime・配置・接続設定
- [利用ガイド](_documents/利用ガイド.md): コマンド・オプション・対応範囲
- [生成ルール](_documents/生成ルール.md): 名前・ハッシュ・列の生成方法
- [運用手順](_documents/運用手順.md): サイト変更・CodeDefiner 後・失敗時の対応
- [App Service 配置](_documents/AppService導入手順.md): 既存 App Service での実行
- [開発者向けガイド](_documents/開発ガイド.md): ソースの変更・検証
- [資料の案内](_documents/README.md): 利用者と開発者それぞれの入口

## ライセンス

AGPL v3（AGPL-3.0-or-later）。[LICENSE](LICENSE)、[NOTICE](NOTICE)、[第三者ライセンス](ThirdPartyNotices.txt) を参照してください。
