# _reference（参照専用・非追跡）

Pleasanter 本体（GNU AGPL v3）は AI の事実確認用に必要時だけ取得する。
ソースは Git で追跡せず、CI でも取得しない。日次更新とポインタ更新 PR は廃止する。

## 取得してから読む

リポジトリ直下で実行する。既定の取得先は `_reference/Implem.Pleasanter`。

```powershell
bash scripts/sync-reference.sh
```

上流の [Implem/Implem.Pleasanter](https://github.com/Implem/Implem.Pleasanter) の
`main` を shallow clone し、再実行時は fetch したコミットを detached HEAD で参照する。
既存の未コミット変更・異なる取得元・空でない取得先などがあれば停止し、勝手に破棄しない。
旧サブモジュールのフォルダや未コミット変更も削除せず、エラーを確認して別の取得先を使う。

複数 clone で共有するときは、呼び出すプロセスだけに環境変数を設定する。
指定先を直接読み、ローカルへのリンクは作らない。

```powershell
$env:PLEASANTER_REFERENCE_DIR = 'D:\references\Implem.Pleasanter'
bash scripts/sync-reference.sh
# 調査後は環境変数を解除する。共有先は削除しない。
Remove-Item Env:PLEASANTER_REFERENCE_DIR
```

成功時は取得先の案内と SHA・コミット日・取得日時・版数の要約が表示される。
成功時の状態は、同じコミットを再取得した場合も `Status=updated`。
再現性の根拠には `FullSHA` または次のコマンドで確認した完全な SHA を使う。

```powershell
git -C $env:PLEASANTER_REFERENCE_DIR rev-parse HEAD
```

既定先の場合は `git -C _reference/Implem.Pleasanter rev-parse HEAD` で確認する。
文書には完全な SHA、`https://github.com/Implem/Implem.Pleasanter/blob/<SHA>/<path>` の
固定リンク、参照日を併記する。`main` はリリース前の変更を含むため、
必要なら本体 csproj の `<Version>` とリリースタグとの対応も確認する。
過去の確認結果の SHA を新しい SHA に書き換えるときは、対象ソースで再確認する。
ソースの取得だけでは実機検証にならない。

## 厳守事項

- コードをコピー・流用・リンク・プロジェクト参照しない。
  IndexCreator は本体アセンブリを参照せず、本体設定と利用者の DB を扱う独立 CLI。
- ビルド・配布物・ソリューションに含めない。既定先・共有先の中身を編集しない。
- API 仕様・サイト設定のスキーマ・列定義・挙動の事実確認だけに使う。
- 実際の API 応答、DB の動作、実運用の設定や権限は実機で確認し、未検証なら明記する。

`bash scripts/reference-isolation-check.sh` は、プロジェクト参照・本体名前空間・取得先の混入を検査する。
共有先を使うときは同じ `PLEASANTER_REFERENCE_DIR` を設定して検査する。
利用者の本体フォルダ名を扱うアプリ固有の文字列は取り込みと区別して許容する。

## 移行の根拠

参照日: 2026-10-27。
[Template の配布手順](https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Template/blob/3312f003fbe9827090e20bc643800824bdf30e22/_documents/参照ソースのオンデマンド取得への配布手順.md)
（採用コミット: `3312f003fbe9827090e20bc643800824bdf30e22`）に従う。
背景は vehiclevisionjp/VehicleVision.PleasanterTools.Template#61・
vehiclevisionjp/VehicleVision.PleasanterTools.Template#63。
本体の実装仕様や過去の実機検証結果を更新する変更ではない。
