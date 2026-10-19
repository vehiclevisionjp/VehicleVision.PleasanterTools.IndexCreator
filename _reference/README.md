# 参照ソース

Implem.Pleasanter は AGPL v3 の参照専用サブモジュール。上流の `main` の先頭を追う（2026-10-19 に、リリースタグへの固定から変えた）。コミットは固定して参照し、更新は自動 PR で進める。固定位置の正は、リポジトリが記録しているコミット（`git ls-tree HEAD _reference/Implem.Pleasanter`）で、2026-10-19 時点は `1b7b6e6c`（`Pleasanter_1.5.9.0` と同じコミット）。CodeDefiner の引数とパス解決、SiteSettings と DB 定義の確認に使用する。独立 CLI からプロジェクト参照しない。配布 ZIP と Docker コンテキストに含めない。

```powershell
git submodule update --init --recursive
git config submodule.recurse true
```

`submodule.recurse` を設定しておくこと（クローンごとに 1 回）。追従 PR がマージされるたびに親のポインタが進むが、手元のサブモジュールの中身は、これが無いと古いまま残り、`pull` のあとに `modified: _reference/Implem.Pleasanter (new commits)` と差分が出る。設定しておけば、`pull`・`switch` のたびに中身が、親が記録したコミットへ自動で合う。`.gitmodules` の `ignore = all` で隠さないこと（差分は消えるが、中身が古いまま残り、事実確認の根拠を取り違える）。

`main` はリリース前の変更を含む。「どの版の事実か」は、確認したコミットと `<Version>`（`Implem.Pleasanter/Implem.Pleasanter.csproj`）で言う。リリース版との対応は `Pleasanter_*` タグで確かめる。
