import { expect, test } from '@playwright/test';

/**
 * Minimal smoke test: proves the pinned browser engine starts and renders
 * locally without requiring a running Jellyfin instance.
 */
test('browser engine starts and renders a page', async ({ page }) => {
  await page.goto('about:blank');
  await page.setContent('<title>danmuku-smoke</title><h1 id="ok">ok</h1>');
  await expect(page).toHaveTitle('danmuku-smoke');
  await expect(page.locator('#ok')).toHaveText('ok');
  expect(page.url()).toBe('about:blank');
});
