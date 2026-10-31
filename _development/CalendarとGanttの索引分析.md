# CalendarとGanttの索引分析

## 結論と作業範囲

Issue #143 の設計調査。単一の物理日付列を明示した保存Calendarビューを、最初の候補生成対象として
[#150](https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.IndexCreator/issues/150)へ分離する。
期間型CalendarのOR条件とGanttの式を使う条件は、通常の日付列索引でカバー済みとしない。
この文書は本体コードの照合結果であり、候補生成機能の実装・３DBでの実行検証・画面の性能改善は未完了。
カレンダーのスタイル変更・移行は本作業に含めない。

参照日: 2026-11-01。Pleasanter参照コミット:
`664c833b144d9da9b6bb58e60f7e48eb01141e58`。
以下は生成処理を事実確認して整理したもので、本体コードのコピー・流用・依存追加は行わない。

## 本体の経路

| 経路 | 確認した挙動 | 索引候補への影響 |
| --- | --- | --- |
| Results / Issues のCalendarDataRows | 終了列がなければ開始列のBETWEENとView条件を合成 | 単一日付列の範囲キーを検討可能 |
| 終了列があるCalendarDataRows | 開始列が期間内、終了列が期間内、開始列が期間開始以前かつ終了列が期間終了以後、のOR | 片側の日付キーだけで全ORをカバーしたと扱わない |
| DashboardのCalendar | DashboardPartのView条件も合成し、CalendarSiteIdや列設定を引き継ぐ | ダッシュボード自身のIDをレコードのSiteIdにしない。対象と条件を解決する別設計が必要 |
| GanttDataRows | GanttUtilities.WhereとView条件を合成 | 単純な一覧のソートから期間条件を推測しない |
| GanttUtilities.Where | 開始・終了の式を使った期間包含と端点BETWEENのOR | 式の実行計画とORの評価が必要 |

固定参照:

- [Results Calendar入口](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Models/Results/ResultUtilities.cs#L8841)、[CalendarDataRows](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Models/Results/ResultUtilities.cs#L9160)
- [Issues CalendarDataRows](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Models/Issues/IssueUtilities.cs#L9541)、[GanttDataRows](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Models/Issues/IssueUtilities.cs#L10269)
- [GanttUtilities](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Libraries/ViewModes/GanttUtilities.cs#L33)

## 列・表示モード・サイトの解決

保存Viewの表示モードのプロパティは`DefaultMode`。フォーム名の`DefaultViewMode`と区別する。
`CalendarFromTo`は単一列名または開始・終了の列名を表し、未指定時はViewMode定義のOption2へ戻る。
参照版の定義はResultsがDateA、IssuesがStartTimeとCompletionTimeの組だが、配置側での定義変更を
確認せずにIndexCreatorへ固定値として埋め込まない。

通常のCalendarでは対象サイトはSiteSettingsのSiteIdを使う。DashboardPartsがある場合は
CalendarSiteIdを使う経路がある。部品のサイトと列・View条件が確定できないまま通常サイト用の
候補生成を流用しない。保存したDefaultModeは実際の利用頻度の証拠ではなく、初期対応を絞る条件とする。
ユーザーが一覧から一時的にCalendarへ切り替える操作を、保存設定だけで網羅するものではない。

参照: [Viewの対象サイト・列解決](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Libraries/Settings/View.cs#L173)、
[DefaultModeの設定](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Libraries/Settings/View.cs#L576)、
[Dashboard部品の設定](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Libraries/Settings/View.cs#L973)、
[ViewMode定義](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Definition_ViewMode/Definition.json)。

## 日時・NULL・式

Calendarの開始・終了パラメータは表示期間、ユーザーのタイムゾーン、FullCalendarの入力、
Dashboardの入力による分岐がある。月末・週末を独自の半開区間へ置き換えず、
実測時には本体が渡す値と型を保つ。BETWEENの両端、NULL、期間外、境界精度を検証する。
列がNULLならその比較は真にならず、期間型では別のOR分岐が成立する可能性も分けて扱う。
参照: [Calendars.BeginDate / EndDate](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Libraries/Requests/Calendars.cs#L13)。

Ganttは開始列をCASE式で参照し、終了列にはEditorFormatに由来する日数補正を適用する。
開始のCASEをCOALESCE相当と決めつけず、NULLの実結果を検証する。
終了の補正はSQL Serverのdateadd、PostgreSQLのinterval演算、MySQLのdate_addに分かれる。
補正が0でも、オプティマイザーが単純な列条件へ変換すると仮定しない。
Ganttの開始パラメータはUTCへの変換、終了は期間日数とミリ秒調整を経る。
標準列の変更や計算列の追加は、この索引機能の範囲外とする。

固定参照（各ディレクトリのStartTimeColumn.sql / CompletionTimeColumn.sql）:

- [SQL Server](https://github.com/Implem/Implem.Pleasanter/tree/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Sqls/SQLServer)
- [PostgreSQL](https://github.com/Implem/Implem.Pleasanter/tree/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Sqls/PostgreSQL)
- [MySQL](https://github.com/Implem/Implem.Pleasanter/tree/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/App_Data/Definitions/Sqls/MySQL)

## 最初の実装境界

単一日付Calendarは、Results / Issuesの保存ViewでDefaultModeがCalendar、
CalendarFromToが明示された単一の物理日付列、DashboardPartsなしを初期対象とする。
未指定の既定列、期間型、未知列・結合先列、動的変更は診断に留める。
物理スキーマに欠損する列や不正JSONは、候補を黙って無視して適用を続けるのではなく安全に停止する。
既存の件数閾値・スクリプトや拡張の不確定性への扱いを確認し、候補根拠を明記する。
拡張SQLそのものからの索引生成は対象外。

候補の基礎はSiteId、確定した等価条件、対象日付の範囲キー。
通常のViewフィルターと同じ列が重なる場合、否定・Own・別の範囲・リンク条件がある場合は
単純なキー連結をせず、既存の候補と役割を比較する。
このCalendarDataRowsにはORDER BYの指定がないため、一覧用のUpdatedTimeソートや
表示上のグループ分けを、そのまま期間検索のソート索引の根拠にしない。

Calendar専用の候補経路と、`Planner.AnalyzeQuery`等の一般View解析を分離する。
items.Getへ渡すViewにCalendar用メタデータがあっても、そのAPIがCalendarDataRowsを
実行する根拠にはならない。日付範囲を一般APIの検索条件へ混入させない。

## 検証計画と残る範囲

| ケース | SQL Server / PostgreSQL / MySQLで確認する内容 |
| --- | --- |
| 単一日付 | 明示列・等価条件の有無・境界・NULL・期間外・複数サイトで候補と実行計画を比較 |
| 既存索引 | 重複・先頭キー・方向を照合し、作成後の再計画がKeepになることを確認 |
| 非混入 | 通常一覧、items.Get、異なる表示モード、Dashboard、未指定・不正な列で誤候補が出ない |
| 期間型 | 3分岐それぞれで成立する行、長期間の包含、NULL、交差しない期間を用意し、全ORを測る |
| Gantt | 開始CASE、終了の日付補正、EditorFormat、NULL、境界の計画をDBごとに確認 |
| 効果 | 時間だけでなく読み取り・実行行数・書込負荷・容量を比較し、採用しない条件も記録 |

SQL Serverだけの結果を他DBの性能保証にしない。合成データによる機能検証と、
利用環境での速度検証を分ける。期間型・Gantt・Dashboardは単一日付対応の後も残る。
特にOR分岐の一つだけ速くなった結果で、画面全体の期間検索が改善したと判断しない。
