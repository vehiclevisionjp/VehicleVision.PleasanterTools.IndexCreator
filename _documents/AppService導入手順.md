# App Service への配置

本書は既存の Pleasanter を Azure App Service で運用している管理者向けです。アプリ設定と本体ファイルの配置が環境ごとに異なるため、最初に `plan` で接続・パス・候補を確認してください。IndexCreator 自体は常駐する Web アプリではなく、必要なときに実行するコンソールツールです。

## 配置とパス

本体が `wwwroot/Implem.Pleasanter` にあり、CodeDefiner が兄弟の `wwwroot/Implem.CodeDefiner` にある場合は、配布フォルダを `wwwroot/IndexCreator` に置きます。通常の配置と同じで、`/p` は省略できます。

本体の DLL と App_Data が wwwroot 直下にある場合は、その本体フォルダを `/p` で指定します。CodeDefiner と同じ階層へ配置した IndexCreator の実際のパスから実行してください。例えば IndexCreator を wwwroot 配下へ置いた Windows 環境では、管理用コンソールで次を実行します。

```bat
dotnet "%HOME%\site\wwwroot\IndexCreator\IndexCreator.dll" plan /p "%HOME%\site\wwwroot"
dotnet "%HOME%\site\wwwroot\IndexCreator\IndexCreator.dll" _rds /p "%HOME%\site\wwwroot" /y
```

Linux で本体が `/home/site/wwwroot` 直下にある場合の例です。

```bash
dotnet /home/site/wwwroot/IndexCreator/IndexCreator.dll plan /p /home/site/wwwroot
dotnet /home/site/wwwroot/IndexCreator/IndexCreator.dll _rds /p /home/site/wwwroot /y
```

ZIP 配置や実行パッケージの設定で wwwroot が読み取り専用の場合は、サイトと CodeDefiner を配置する運用に合わせて IndexCreator を配置し、`/p` で本体の絶対パスを指定します。IndexCreator は本体フォルダに状態やログを書き込みません。`--output` の出力先は書き込み可能な場所を指定します。

## 環境変数の接続設定

接続文字列は既存の本体設定、または App Service のアプリ設定として供給します。専用の `INDEXCREATOR_CONNECTION_STRING` と `INDEXCREATOR_DBMS` はドットを含まない名前です。ファイルの設定より優先されます。環境変数の詳しい順序は [導入手順](導入手順.md) を参照してください。

App Service の「接続文字列」欄で指定した値には SQLCONNSTR_ などの接頭辞が付くため、初版はその名前を自動で読みません。IndexCreator が読むアプリ設定名へ供給してください。設定変更後に開いた管理用コンソールで値がプロセスへ渡ることを確認します。接続文字列そのものを画面へ出す確認は避けてください。

`Rds.json` の DisableIndexChangeDetection=true は App Service でも必須です。本体と同じ接続文字列でも、索引作成の権限が必要です。.NET Runtime 10 の存在、DB の TLS、ネットワークとファイアウォールを `plan` で確認します。

## サイト変更と本体更新

サイト編集と CodeDefiner を終了し、`plan --prune` で確認した後に `_rds --prune /y` を実行します。複数インスタンスやスロットから同じ DB に実行してもセッションロックで IndexCreator 同士の多重実行を防ぎます。CodeDefiner やサイト編集との同時実行は避けてください。

本体を再配置する際に IndexCreator フォルダも保持・再配置してください。テーブルが作り直された場合は、IndexCreator の再実行で不足した管理索引を補います。詳しくは [運用手順](運用手順.md) を参照してください。

## 確認した範囲

Windows と Linux で配布 ZIP を使い、App Service に似た配置・環境変数・別フォルダからの起動をローカル検証しました。実際の Azure App Service、Azure SQL、マネージド ID、スロット、プラットフォームの実行制限を含む検証は未実施です。
