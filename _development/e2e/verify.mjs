// IndexCreator の適用後に、画面の動作と選択肢の反映を確認し、結果を JSON で出力する。
// 適用前後の保存ビューの一覧、画面からの登録、選択肢を画面で変更した後のドロップダウンを返す。
import fs from 'node:fs';
import { open, base, createRecord, setChoices } from './lib.mjs';

const ids = JSON.parse(fs.readFileSync('e2e-ids.json', 'utf8'));
const backslash = String.fromCharCode(92);
const { browser, page } = await open();

async function listed() {
  await page.goto(`${base}/items/${ids.table}/index`);
  await page.waitForTimeout(1200);
  await page.selectOption('#ViewSelector', { label: '受付の金額順' });
  await page.waitForTimeout(2000);
  return page.evaluate(() => [...document.querySelectorAll('#Grid tbody tr')].map(r => r.innerText.split(/\s+/)[1]));
}
const result = { before: await listed() };
// 索引と View が追加された状態でも、画面から登録できて一覧に反映される。
await createRecord(page, ids.table, { Results_Title: '案件D', Results_ClassA: '10', Results_NumA: '250' });
result.after = await listed();

// 選択肢を画面で変えると、View を作り直さなくても反映される（60 の追加と動的参照の行）。
await setChoices(page, ids.table, 'ClassA', '状態', ['10,受付,受', '20,対応中', '30,完了', '40' + backslash + ',特殊,カンマ付き', ' 50,前後空白 ', '10,重複', '60,保留', '[[Users]]']);
await page.goto(`${base}/items/${ids.table}/new`);
await page.waitForTimeout(1500);
result.dropdown = await page.evaluate(() => [...document.querySelector('#Results_ClassA').options].filter(o => o.value !== '').map(o => o.value + '|' + o.text));
console.log(JSON.stringify(result));
await browser.close();
