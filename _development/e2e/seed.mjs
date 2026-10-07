// 検証用の本体にサイト構成・選択肢・保存ビュー・レコードを画面から作る。
// 空の DB で一度だけ実行し、作成した SiteId を e2e-ids.json に書く。
import fs from 'node:fs';
import { open, createSite, enableColumns, setChoices, createView, useExactMatchFilter, createRecord } from './lib.mjs';

const backslash = String.fromCharCode(92);
const { browser, page } = await open();
const ids = {};
ids.folder = await createSite(page, 0, 'Folder', 'E2E部門');
ids.table = await createSite(page, ids.folder, 'Recorded table', '案件');
ids.subFolder = await createSite(page, ids.folder, 'Folder', 'サブ');
ids.child = await createSite(page, ids.subFolder, 'Recorded table', '子テーブル');
ids.wiki = await createSite(page, ids.folder, 'Wiki', '手順書');
ids.top = await createSite(page, 0, 'Recorded table', 'トップ');

await setChoices(page, ids.table, 'ClassA', '状態', ['10,受付,受', '20,対応中', '30,完了', '40' + backslash + ',特殊,カンマ付き', ' 50,前後空白 ', '10,重複']);
await enableColumns(page, ids.table, ['NumA']);
await createView(page, ids.table, '受付の金額順', { column: '[案件] 状態', name: 'ClassA', value: '受付' }, { column: 'NumA', order: 'Descendant' });
await useExactMatchFilter(page, ids.table, 'ClassA');
await setChoices(page, ids.child, 'ClassA', '区分', ['1,子A', '2,子B']);
await setChoices(page, ids.top, 'ClassA', '区分', ['X,トップX']);
await createRecord(page, ids.table, { Results_Title: '案件A', Results_ClassA: '10', Results_NumA: '300' });
await createRecord(page, ids.table, { Results_Title: '案件B', Results_ClassA: '20', Results_NumA: '100' });
await createRecord(page, ids.table, { Results_Title: '案件C', Results_ClassA: '10', Results_NumA: '200' });

fs.writeFileSync('e2e-ids.json', JSON.stringify(ids, null, 2));
console.log(JSON.stringify(ids));
await browser.close();
