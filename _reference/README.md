# 参照ソース

Implem.Pleasanter は AGPL v3 の参照専用サブモジュール。commit `8d29c7bd110c2487ad4b39bd34dafaf0ba66373b` を固定する。CodeDefiner の引数とパス解決、SiteSettings と DB 定義の確認に使用する。独立 CLI からプロジェクト参照しない。配布 ZIP と Docker コンテキストに含めない。

```powershell
git submodule update --init --recursive
```
