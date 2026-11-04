import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: true,
  retries: 0,
  workers: 1,
  reporter: 'html',
  use: {
    baseURL: 'http://127.0.0.1:5197',
    trace: 'on-first-retry',
  },
  projects: [
    {
      name: 'firefox',
      use: { ...devices['Desktop Firefox'] },
    },
  ],
  webServer: {
    command: 'dotnet run --no-launch-profile --project src/Recommendarr.Web/Recommendarr.Web.csproj',
    url: 'http://127.0.0.1:5197/api/v1/health',
    reuseExistingServer: false,
    timeout: 120_000,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Testing',
      ASPNETCORE_URLS: 'http://127.0.0.1:5197',
      Recommendarr__Storage__DataDirectory: '.playwright-data',
      Recommendarr__Sync__RunOnStartup: 'false',
    },
  },
});
