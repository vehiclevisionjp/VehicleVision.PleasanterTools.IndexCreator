# 本体SQL経路の最終照合

Issue #141の検索・並べ替え・関連取得の調査を、現行の候補生成と子Issueへ対応付ける。確認日2026-11-02、Pleasanterの固定参照は `664c833b144d9da9b6bb58e60f7e48eb01141e58`。本体は事実確認専用で、コピー・流用・依存・ビルド・配布の対象にしない。

## 経路と製品の対応

| 経路 / 子Issue | 対応範囲 | 対応しない部分・利用上の境界 | 証跡 |
| --- | --- | --- | --- |
| IssuesのDelay / #142 | 肯定条件の未完了Status範囲を等価キーの後へ候補化。否定設定、参照種別、件数閾値を区別 | 現在時刻・進捗率・開始/作成/完了日時の計算式は残る。Status索引でDelay全体や並び順をカバーしたとはしない | Plannerと[生成ルール](../_documents/生成ルール.md)、単体のDelay条件・Own併用・重複・否定・非Issues・閾値 |
| Calendar/Ganttの経路整理 / #143 | 保存View、対象サイトと列、日時・NULL・式、通常APIと専用取得の違いを定義 | フォーム名を保存Viewプロパティと混同せず、既定列や実行時入力を推測しない | [本体分析](CalendarとGanttの索引分析.md) |
| 単一日付Calendar / #150 | Results/Issuesの保存Calendar Viewに明示された物理日付列の範囲候補。等価条件と既知Own分岐を組み合わせる | Calendar専用候補。一般API・リンク選択肢へ日付条件を混入しない。既定列・結合先・不確定な動的変更を解析済みとしない | CalendarTests、単一日付のDB往復試験 |
| 期間型Calendar / #173 | `/include-period-calendars`を明示したplan専用レビュー。開始/終了の各列の候補を提示し、同一物理定義は統合 | 期間重複の3分岐OR全体、順序、性能を保証しない。通常適用とpruneは不可 | [全OR比較](期間型Calendarの比較試験.md)、PeriodCalendarTests / WorkloadTests、オプション制限 |
| Gantt / #174 | 開始CASE・終了補正・NULL・境界を3DBで評価し、Gantt固有候補の自動追加を不採用とした | 一般Viewのフィルター候補は独立して残る。Gantt式を単純な日付列条件に書き換えない。専用索引・式索引・計算列は作らない | [比較と不採用根拠](Ganttの比較試験.md) |
| Dashboard Calendar / #175 | `/include-dashboard-calendars`でライブDBの保存部品をplan専用レビュー。明示数値対象の各基準候補に共通する物理キーのみを提示 | 同じ参照種別・全対象の取得/閾値/確定条件が必要。基準候補の和集合を採用しない。代表IDは実行時基準でない。名前/グループ展開、許可集合、フォーム/セッション条件は未解決。apply/prune/オフラインsitesは不可 | [解析境界](DashboardCalendarの解析境界.md)、DashboardCalendarTests / IntegrationTests |
| 管理系 / #145・#161・#194 | 関連する標準キー・型を揃えた3DB比較と採否を整理。既存Sites更新差分の明示対応を維持 | 他の管理系候補の一律自動追加は見送る。全列幅、全画面、全物理条件や未採用用途の適用機構を検証済みとしない | [操作別の採否](管理系索引の採否と検証範囲.md)、[基準補正](権限比較の標準キー補正.md) |
| 拡張SQL / #146 | ファイル・Extensions表のどちらも自動候補生成/作成の対象外。作成者向けの手動ガイドを提供 | 将来対応待ちではなく製品の責務境界。スクリプト経由の呼出でも任意SQLを索引化しない | [手動設計・比較・保守](../_documents/拡張SQLのインデックス設計ガイド.md) |
| 最終照合の補正 / #198 | Calendarの保存スクリプト判定にServerScriptsAllDisabledを反映。Dashboard実行ログを複数基準の共通キーという実装へ合わせる | 保存スクリプトの全体無効化だけで、他の入力不確定性・列検査・件数条件や期間型の明示指定を解除しない | CalendarTestsとDashboardCalendarTestsの追加回帰 |

単一日付の対応は通常の候補生成/適用経路に含まれるが、期間型・Dashboardのフラグはレビュー専用である。通常Viewの候補が同時に出力されても、レビュー専用フラグを付けた実行をそのまま自動適用できるという意味ではない。

## 通常規則との関係

完全一致・範囲・前方一致、Ownの既知OR、近い期限/期限超過、リンク選択肢・項目連携・ID参照は既存の通常規則として維持する。否定・任意OR・複数選択・部分一致・式によるソート・リンク先条件の未対応部分を黙って通常候補へ変換しない。

現行の規則は[生成ルール](../_documents/生成ルール.md)と[アーキテクチャ](../_documents/アーキテクチャ方針.md)を参照する。[2026-10-09のカバー分析](索引カバー分析.md)は別の固定参照・旧版の調査履歴であり、今回の参照版で全てを再測定した記録へ書き換えない。

サーバースクリプトのAST解析は別の親#105の範囲であり、本体SQL経路を調べただけで任意JavaScript・動的View・複数Getの全実行経路をカバーしたとはしない。指定したファイル/Extensions入力の検査と、未指定の拡張を区別する。スクリプト本文も拡張SQLも実行しない。

## 最終照合で補正した内容

CalendarはServerScriptsAllDisabledがtrueなら保存スクリプトを有効と扱わない。未指定またはfalseで個別に有効な項目がある場合は専用日付候補を抑止し、全項目Disabledの場合も解析を認める。結果に影響する他の条件は変更しない。

本体も[ServerScriptUtilities](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Libraries/ServerScripts/ServerScriptUtilities.cs#L1179)で全体無効化を先に確認する。ここで確認したのはそのフラグの意味で、契約・実行文脈・共有スクリプト・拡張の全てを再現したという意味ではない。

Dashboardは複数基準の共通キーに対応済みだったが、実行時の概要ログだけに単一対象という旧説明が残っていた。説明を補正し、対象解決・許可集合・候補の生成規則や適用制限は広げない。

Calendarの除外理由も、単一日付だけを前提とする旧説明から、明示指定した期間型を含む現行の条件へ揃える。この診断文言の補正で候補化・除外規則は追加変更しない。

## 検証と未検証範囲

- 全DBのResults/Issuesで、単一日付・期間型、全体無効化のtrue/false、個別Disabled、一般APIへの非混入を単体確認する。Dashboardの基準サイトでも全体無効化を確認する。
- 単一日付・期間型は既存の3DB統合で、候補DDL、カタログ照合、再計画Keep、日付の境界・NULL・包含・サイト分離を確認する。これはレビュー候補の実験的な検証であり、期間型の製品CLIにapplyを許可するものではない。
- 期間型・Gantt・管理系は合成比較の結果と容量・更新費用を保存する。Dashboardは共通キーと条件合成・不確定入力を検証し、全UI・全認可・全実行文脈の性能試験とはしない。

各DBの指標を同じ単位と見なさず、索引選択や短い時間差を合格条件・改善保証にしない。全業務クエリのカバー率を百分率で示さない。参照版以降の本体差分、利用頻度、実運用のデータ・全列幅、同時更新、利用者固有の権限と動的入力は別の検証が必要。

## 親Issueの完了範囲

子Issueごとに候補化・レビュー専用・不採用・対象外を定義し、上の最終補正と回帰確認まで行った範囲で#141を完了する。Pleasanterの全SQL・全表示モード・任意の拡張・画面全体が高速化したという完了ではない。今後の新しい経路や参照版更新は、根拠・入力境界・3DB検証条件を持つ別Issueにする。

実装済みの索引は既存カタログと先頭キーを比較し、標準/独自索引を保護する。計画の確認と実DBへの適用を区別し、オンライン制御・作成後検証・明示pruneの既存の運用を維持する。今回の最終照合では実DBへの索引追加・削除・スクリプトの実行・カレンダースタイルの変更を行わない。

## 固定参照と実装の入口

- [ViewのDelay条件](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Libraries/Settings/View.cs#L1998)
- [Calendar/Ganttの本体入口・日付・式](CalendarとGanttの索引分析.md)、[Dashboardの基準・View合成・許可集合](DashboardCalendarの解析境界.md)
- [Planner](../src/IndexCreator/Planner.cs)、[DashboardCalendarPlanner](../src/IndexCreator/DashboardCalendarPlanner.cs)、[Optionsの適用制限](../src/IndexCreator/Options.cs)、[Program](../src/IndexCreator/Program.cs)
- [Calendarの単体試験](../tests/IndexCreator.Tests/CalendarTests.cs)、[Dashboardの単体試験](../tests/IndexCreator.Tests/DashboardCalendarTests.cs)、[DB統合の入口](../tests/IndexCreator.Tests/Program.cs)
