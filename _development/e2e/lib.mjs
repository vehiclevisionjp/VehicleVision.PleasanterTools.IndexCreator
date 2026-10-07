// Pleasanter 本体の画面を Playwright で操作する共通処理。検証用の本体だけに向けて使う。
import { chromium } from 'playwright-core';
import fs from 'node:fs';

export const base = process.env.E2E_BASE ?? 'http://127.0.0.1:58800';
const password = process.env.E2E_PASSWORD ?? 'E2e_Test_only_123!';
const stateFile = process.env.E2E_STATE ?? 'e2e-state.json';
const changedFile = stateFile + '.changed';

export async function open() {
  const browser = await chromium.launch({ channel: 'chrome', headless: true });
  const context = await browser.newContext({ locale: 'ja-JP', viewport: { width: 1400, height: 900 }, storageState: fs.existsSync(stateFile) ? stateFile : undefined });
  const page = await context.newPage();
  page.on('dialog', d => d.accept());
  await page.goto(base + '/items/0/index');
  if (page.url().includes('/users/login')) {
    await page.fill('#Users_LoginId', 'Administrator');
    await page.fill('#Users_Password', fs.existsSync(changedFile) ? password : 'pleasanter');
    await page.click('#Login');
    await page.waitForTimeout(2000);
    // 初回ログインは既定パスワードの変更を求められる。
    if (await page.locator('#Users_ChangedPassword').isVisible().catch(() => false)) {
      await page.fill('#Users_ChangedPassword', password);
      await page.fill('#Users_ChangedPasswordValidator', password);
      await page.click('#ChangePassword');
      await page.waitForTimeout(2000);
      fs.writeFileSync(changedFile, '');
    }
    await context.storageState({ path: stateFile });
  }
  return { browser, page };
}

const update = page => page.getByRole('button', { name: 'Update' }).last().click();

// テンプレート（Folder / Recorded table / Wiki）からサイトを作り、新しい SiteId を返す。
export async function createSite(page, parentId, template, title) {
  await page.goto(`${base}/items/${parentId}/index`);
  await page.click('#NewMenuContainer');
  await page.waitForTimeout(1000);
  await page.getByText(template, { exact: true }).first().click();
  await page.waitForTimeout(800);
  await page.click('#OpenSiteTitleDialog');
  await page.fill('#SiteTitle', title);
  await page.click('#CreateByTemplate');
  await page.waitForTimeout(2500);
  return page.evaluate(t => [...document.querySelectorAll('.nav-site')].filter(e => e.innerText.trim() === t).map(e => Number(e.dataset.value)).pop(), title);
}

// 「エディタ」で項目を有効にする。既に有効なら何もしない。
export async function enableColumns(page, siteId, columns) {
  await page.goto(`${base}/items/${siteId}/edit`);
  await page.click('a[href="#EditorSettingsEditor"]');
  await page.waitForTimeout(800);
  for (const c of columns) {
    if (!(await page.locator(`#EditorSourceColumns li[data-value="${c}"]`).count())) continue;
    await page.click(`#EditorSourceColumns li[data-value="${c}"]`);
    await page.click('#ToEnableEditorColumns');
    await page.waitForTimeout(800);
  }
  await update(page);
  await page.waitForTimeout(2500);
}

// 項目の詳細設定で表示名と選択肢（行の配列）を設定する。
export async function setChoices(page, siteId, column, label, lines) {
  await page.goto(`${base}/items/${siteId}/edit`);
  await page.click('a[href="#EditorSettingsEditor"]');
  await page.waitForTimeout(800);
  if (await page.locator(`#EditorSourceColumns li[data-value="${column}"]`).count()) {
    await page.click(`#EditorSourceColumns li[data-value="${column}"]`);
    await page.click('#ToEnableEditorColumns');
    await page.waitForTimeout(800);
  }
  // 項目をクリックすると出るツールバーから詳細設定を開く。表示されるまで数回クリックする。
  for (let i = 0; i < 4 && !(await page.isVisible('#OpenEditorColumnDialog')); i++) {
    await page.click(`#EditorColumns li[data-value="${column}"]`);
    await page.waitForTimeout(600);
  }
  await page.click('#OpenEditorColumnDialog');
  await page.waitForTimeout(1500);
  await page.fill('#LabelText', label);
  const editor = page.locator('#EditorColumnDialog').locator('.cm-content, .monaco-editor textarea, .CodeMirror textarea, [contenteditable=true]').first();
  await editor.click();
  await page.keyboard.press('Control+A');
  await page.keyboard.press('Delete');
  for (const [i, line] of lines.entries()) {
    await page.keyboard.insertText(line);
    if (i < lines.length - 1) await page.keyboard.press('Enter');
  }
  await page.click('#SetEditorColumn');
  await page.waitForTimeout(1000);
  await update(page);
  await page.waitForTimeout(2500);
}

// 保存ビューを1つ作る。filter は { column: 絞り込み項目の表示名, name: 物理列名, value: 選択肢の表示名 }、
// sorter は { column: 物理列名, order: 'Descendant' など }。
export async function createView(page, siteId, name, filter, sorter) {
  await page.goto(`${base}/items/${siteId}/edit`);
  await page.getByRole('link', { name: 'View', exact: true }).click();
  await page.waitForTimeout(800);
  await page.click('#NewView');
  await page.waitForTimeout(1500);
  await page.fill('#ViewName', name);
  await page.click('a[href="#ViewFiltersTab"]');
  await page.waitForTimeout(500);
  await page.selectOption('#ViewFilterSelector', { label: filter.column });
  await page.click('#AddViewFilter');
  await page.waitForTimeout(1200);
  await page.click(`#ViewFilters__${filter.name}_ms`);
  await page.waitForTimeout(500);
  await page.locator('.ui-multiselect-menu:visible label').filter({ hasText: new RegExp(`^${filter.value}$`) }).click();
  await page.click('#ViewName');
  await page.click('a[href="#ViewSortersTab"]');
  await page.waitForTimeout(500);
  await page.locator('#ViewSorterSelector:visible').selectOption(sorter.column);
  await page.locator('#ViewSorterOrderTypes:visible').selectOption({ label: sorter.order });
  await page.locator('#AddViewSorter:visible').click();
  await page.waitForTimeout(800);
  await page.click('#AddView');
  await page.waitForTimeout(1500);
  await update(page);
  await page.waitForTimeout(2500);
}

// フィルタ項目の検索方法を「完全一致」にする（画面で選択肢を設定すると部分一致で保存される場合がある）。
export async function useExactMatchFilter(page, siteId, column) {
  await page.goto(`${base}/items/${siteId}/edit`);
  await page.getByRole('link', { name: 'Filter', exact: true }).click();
  await page.waitForTimeout(800);
  await page.click(`#FilterColumns li[data-value="${column}"]`);
  await page.click('#OpenFilterColumnDialog');
  await page.waitForTimeout(1500);
  await page.selectOption('#SearchTypes', { label: 'Exact match' });
  await page.click('#SetFilterColumn');
  await page.waitForTimeout(800);
  await update(page);
  await page.waitForTimeout(2500);
}

export async function createRecord(page, siteId, fields) {
  await page.goto(`${base}/items/${siteId}/new`);
  await page.waitForTimeout(1200);
  for (const [id, value] of Object.entries(fields)) {
    const tag = await page.evaluate(i => document.getElementById(i)?.tagName, id);
    if (tag === 'SELECT') await page.selectOption('#' + id, value);
    else await page.fill('#' + id, value);
  }
  await page.click('#CreateCommand');
  await page.waitForTimeout(2000);
}
