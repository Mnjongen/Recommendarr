import { test, expect } from '@playwright/test';

test('has title', async ({ page }) => {
  await page.goto('/login');

  await expect(page).toHaveTitle('Sign in · Recommendarr');
  await expect(page.getByRole('heading', { name: 'What should you watch next?' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Sign in with Plex' }))
    .toHaveAttribute('href', '/auth/plex/start');
});

test('Telegram settings require a signed-in profile', async ({ page }) => {
  await page.goto('/telegram');

  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByRole('link', { name: 'Sign in with Plex' })).toBeVisible();
});
