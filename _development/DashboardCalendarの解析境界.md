# Dashboard Calendarの解析境界

Issue #175 の採用範囲。`plan /include-dashboard-calendars`で、数値サイトIDを明示した保存Calendar部品を候補レビューする。単一対象に加え、複数の基準候補で物理キーが共通になる範囲を扱う。ライブDB専用で、apply・prune・オフラインsites入力と併用しない。

## 対象と条件

本体のDashboardPartはCalendarSitesから基準設定を解決し、実行時のSiteIdを設定する。保存されたSiteIdだけを信用せず、CalendarSitesをカンマで分け、空要素と周囲空白を除く。全要素が明示された正の数値IDで、全対象を取得できることを要求する。重複IDは解析時にまとめる。サイト名・グループ名・不明なIDは診断に留め、既知のIDだけを部分採用しない。

CalendarSitesDataが保存されている場合は、重複を含む数値列と順序がCalendarSitesと一致することを要求する。順序の違いでも基準の選択に影響し得るため、集合が同じという理由で矛盾を見逃さない。

複数対象は全てResultsまたは全てIssuesの場合に限る。どれかが件数閾値未満、スクリプト/指定済み拡張が不確定な場合は部品全体を抑止する。件数の合計で閾値を満たしたことにはしない。

部品のViewを独立に読み、部品のCalendarFromToを対象列として使う。利用者の文脈で基準が変わり得るため、各対象を基準候補としてCalendar専用解析へ渡し、表・列順・方向・MySQL prefix・PostgreSQL opclassが全候補で一致する索引だけを残す。候補の和集合や設定の上書きで相違を消さない。全対象で共通キーがなければ候補は0件となる。

候補名の代表IDは最小IDに固定するが、実行時の基準サイトと解決できたという意味ではない。索引はSiteIdを先頭に持つ通常の表全体の索引であり、代表サイトだけのWHERE付き索引ではない。対象サイトの無関係な保存Viewsを部品のViewへ混ぜず、一覧用の既定ソート候補も追加しない。複数部品は独立に処理する。物理表はResults / Issuesで、Dashboards表を作成・索引化しない。

保存Viewにある等価条件と日付範囲を候補の根拠にする。実行時のセッション・フォーム・ユーザーのViewとの合成を再現したとは扱わず、診断にも明記する。有効なソースまたは対象のサーバースクリプト、指定した拡張入力で不確定な適用先は抑止する。未指定の拡張入力・実行文脈を検証済みとはしない。

本体は基準設定に統合先を設定し、許可サイトを解決する。許可集合だけで済む場合のSiteId INに加え、拒否サイトがあるとレコード権限を含む別条件へ分岐する。名前/グループ展開、数値文字列と名前の衝突、許可/拒否集合はこの解析では確定しない。明示IDを利用者の許可集合と扱わず、権限の再実装や、権限条件全体の索引設計も行わない。

期間型Calendarの部品候補は`/include-period-calendars`も指定した場合だけ表示する。全ORの効果を保証しない境界は通常の期間型レビューと共通。未知列・結合先列・未指定の既定日付は推測しない。Ganttや他種別の部品はこの解析の対象外。

## 検証と採用範囲

３DB共通の合成設定で、型、単一対象、複数部品の分離、複数基準の共通キー、順序・重複・キャッシュ、異種表、欠損・件数閾値、基準ごとの検索方法の差、スクリプト・拡張、壊れたView、apply/prune禁止を検証する。代表名が指定順で変わらず、無関係なViewの条件値が診断へ漏れないことも確認する。

ライブ結合試験では、専用Dashboard読取、実際に読んだ複数対象の設定からの共通キー、列検査・作成・再計画Keepを確認する。独自の架空レコードで、対象２サイト・片側だけ・空集合・対象外サイトの除外を前後照合する。部品条件と追加条件のAND、および矛盾する条件で0件になることも検証する。許可集合は試験が明示した集合で、認可機能を検証したものではない。

採用するのは明示数値対象の保存部品から共通キーを提示するplan専用レビューまでとし、名前/グループ解決や不確定な実行時合成は理由付き診断に留める。SQLの結果・DDLの機能検証であり、本体UI、全ユーザーの権限分岐、実際の性能・更新費用を保証するものではない。適用を検討する場合は実際の全条件の計画と負荷を測る。拡張SQLは自動候補化対象外の方針を維持する。

## 参照

確認日: 2026-11-01、コミット`664c833b144d9da9b6bb58e60f7e48eb01141e58`。本体は事実確認専用で、コピー・依存追加は行わない。

- [DashboardPartの対象解決](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Libraries/Settings/DashboardPart.cs#L373)
- [部品型](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Libraries/Settings/DashboardPartType.cs)
- [Calendar呼出](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Models/Dashboards/DashboardUtilities.cs#L2186)
- [部品Viewとフォーム](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Models/Sites/SiteModel.cs#L9384)
- [統合先と許可集合](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Libraries/Settings/SiteSettings.cs#L5455)
- [許可・拒否サイトの条件分岐](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Libraries/Security/Permissions.cs#L153)
- [部品条件と実行時Viewの合成](https://github.com/Implem/Implem.Pleasanter/blob/664c833b144d9da9b6bb58e60f7e48eb01141e58/Implem.Pleasanter/Models/Results/ResultUtilities.cs#L9160)
