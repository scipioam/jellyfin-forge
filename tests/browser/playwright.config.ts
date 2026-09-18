import { defineConfig, devices } from '@playwright/test';

/**
 * Browser integration test configuration for Jellyfin Forge.
 *
 * Base URL defaults to the repository test contract (127.0.0.1:18096) and can
 * be overridden with E2E_BASE_URL. All run artifacts (traces, screenshots,
 * videos, HTML report) are written under artifacts/ which is git-ignored.
 */
const baseURL = process.env.E2E_BASE_URL ?? 'http://127.0.0.1:18096';

export default defineConfig({
  testDir: './tests',
  outputDir: '../../artifacts/browser/test-results',
  timeout: 90_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL,
    headless: true,
    trace: 'off',
    screenshot: 'only-on-failure',
    video: 'off',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
    {
      name: 'firefox',
      use: { ...devices['Desktop Firefox'] },
    },
  ],
});
