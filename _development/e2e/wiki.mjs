// Wiki を選択肢のリンク先にしたときの動作を確認する。seed.mjs の後に実行する。
// Wiki の本文を画面で編集し、案件の ClassB の選択肢を自項目の行と [[Wiki の SiteId]] の混在にして、
// 登録画面のドロップダウンの内容を JSON で出力する（選択肢 View の結果と比べる）。
import fs from 'node:fs';
import { open, base, setChoices } from './lib.mjs';

const ids = JSON.parse(fs.readFileSync('e2e-ids.json', 'utf8'));
const backslash = String.fromCharCode(92);
const { browser, page } = await open();

// 親フォルダのサイトメニューにある Wiki のレコードへのリンクから、Wiki の編集画面を開く。
await page.goto(`${base}/items/${ids.folder}/index`);
const href = await page.locator('a[href$="/edit"]', { hasText: '手順書' }).first().getAttribute('href');
await page.goto(base + href);
await page.waitForTimeout(1200);
await page.fill('#Wikis_Body', ['a,Alpha', 'b' + backslash + ',c,カンマ付き,短', ' a,重複', '　z　', 'own1,wiki側'].join('\n'));
await page.click('#UpdateCommand');
await page.waitForTimeout(2000);

await setChoices(page, ids.table, 'ClassB', '種別', ['own1,自項目', `[[${ids.wiki}]]`, 'own2,後続']);
await page.goto(`${base}/items/${ids.table}/new`);
await page.waitForTimeout(1500);
console.log(JSON.stringify({ wikiRecord: href, dropdown: await page.evaluate(() => [...document.querySelector('#Results_ClassB').options].filter(o => o.value !== '').map(o => o.value + '|' + o.text)) }));
await browser.close();
