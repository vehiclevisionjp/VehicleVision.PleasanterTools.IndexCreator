# Dashboard Calendarの解析境界

Issue #175 の第一段階。`plan /include-dashboard-calendars`で、単一の数値サイトIDを明示した保存Calendar部品だけを候補レビューする。ライブDB専用で、apply・prune・オフラインsites入力と併用しない。

## 対象と条件

本体のDashboardPartはCalendarSitesから対象を解決し、実行時のSiteIdを設定する。保存されたSiteIdだけを索引対象として信用しない。CalendarSitesに単一の正の数値IDがある場合に限り、通常のレコードサイト一覧からResults / Issuesを参照する。サイト名・グループ名・複数ID・欠損対象は診断に留める。CalendarSitesDataが保存されている場合は矛盾がないことを要求する。

部品のViewを独立に読み、部品のCalendarFromToを対象列として使う。対象サイトの列設定・件数閾値でCalendar専用候補を解析し、一覧用の既定ソート候補は追加しない。複数部品はそれぞれ別に処理し、別サイトのフィルターを結合しない。管理対象の物理表は常にResults / Issuesで、Dashboards表を作成・索引化しない。

保存Viewにある等価条件と日付範囲を候補の根拠にする。実行時のセッション・フォーム・ユーザーのViewとの合成を再現したとは扱わず、診断にも明記する。有効なソースまたは対象のサーバースクリプト、指定した拡張入力で不確定な適用先は抑止する。未指定の拡張入力・実行文脈を検証済みとはしない。

期間型Calendarの部品候補は`/include-period-calendars`も指定した場合だけ表示する。全ORの効果を保証しない境界は通常の期間型レビューと共通。未知列・結合先列・未指定の既定日付は推測しない。Ganttや他種別の部品はこの解析の対象外。

## 検証と残件

３DB共通の合成設定で、数値/文字列のCalendar型、単一対象、複数部品の分離、未知セレクター、件数閾値、スクリプト・拡張、壊れたView、apply/prune禁止を確認する。ライブ結合試験でDashboardの専用読取と、対象Resultsの日付索引作成・再計画Keepを確認する。これは保存部品の候補化検証であり、本体UIでの全実行条件・ユーザー別権限・部品切替を網羅した試験ではない。

複数サイト・サイト名による統合、実行時Viewの合成、本体UIと性能・更新費用の検証が残るため、この変更だけでは #175 を閉じない。拡張SQLは自動候補化対象外の方針を維持する。

## 参照

確認日: 2026-11-01、コミット`664c833b144d9da9b6bb58e60f7e48eb01141e58`。本体は事実確認専用で、コピー・依存追加は行わない。

- [DashboardPartの対象解決](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Libraries/Settings/DashboardPart.cs#L373)
- [部品型](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Libraries/Settings/DashboardPartType.cs)
- [Calendar呼出](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Models/Dashboards/DashboardUtilities.cs#L2186)
- [部品Viewとフォーム](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Models/Sites/SiteModel.cs#L9384)
