import { test, expect, type Page, type WebSocket } from '@playwright/test';

test('has title', async ({ page }) => {
  await page.goto('/login');

  await expect(page).toHaveTitle('Sign in · Recommendarr');
  await expect(page.getByRole('heading', { name: 'What should you watch next?' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Sign in with Plex' }))
    .toHaveAttribute('href', '/auth/plex/start');
});

const protectedRoutes = ['/', '/search', '/admin', '/telegram', '/profiles'];

for (const route of protectedRoutes) {
  test(`${route} requires the appropriate Plex sign-in`, async ({ page }) => {
    await page.goto(route);

    await expect(page).toHaveURL(/\/login/);
    await expect(page.getByRole('heading', { name: 'What should you watch next?' })).toBeVisible();
  });
}

async function signInAs(page: Page, plexUserId: string) {
  await gotoInteractive(page, `/__e2e/sign-in/${plexUserId}`);
  await expect(page).toHaveURL('/');
}

/** Navigates with a full page load and waits for the Blazor circuit, so event handlers are live. */
async function gotoInteractive(page: Page, path: string) {
  let circuitFrames = 0;
  const onSocket = (socket: WebSocket) => {
    if (socket.url().includes('/_blazor'))
      socket.on('framereceived', () => circuitFrames++);
  };
  page.on('websocket', onSocket);

  await page.goto(path);
  await expect.poll(() => circuitFrames, { timeout: 15_000 }).toBeGreaterThan(1);
  page.off('websocket', onSocket);
}

test('signed-in profile sees seeded recommendations and can leave feedback', async ({ page }) => {
  await signInAs(page, '1002');

  await expect(page.getByRole('heading', { name: 'Recommended for you' })).toBeVisible();
  await expect(page.locator('.rec-card h3')).toContainText('Arrival');
  await page.getByRole('button', { name: 'Like Arrival' }).click();

  await expect(page.getByRole('status')).toContainText('Like saved');
  await expect(page.getByRole('button', { name: 'Like Arrival' })).toContainText('Liked');
});

test('admin can switch recommendation profiles', async ({ page }) => {
  await signInAs(page, '1001');

  await page.getByLabel('Viewing').selectOption('1002');

  await expect(page.getByRole('heading', { name: 'Recommended for Bob' })).toBeVisible();
  await expect(page.locator('.rec-card h3')).toContainText('Arrival');
});

test('admin can inspect users and sync history', async ({ page }) => {
  await signInAs(page, '1001');
  await page.getByRole('link', { name: 'Admin' }).click();

  await expect(page.getByRole('heading', { name: 'Admin' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Users' })).toBeVisible();
  await expect(page.getByRole('cell', { name: 'Aliceadmin', exact: true })).toContainText('admin');
  await expect(page.getByRole('cell', { name: 'Bob', exact: true })).toBeVisible();
});

test('admin can evaluate recommendation quality and pick an experiment by keyboard', async ({ page }) => {
  await signInAs(page, '1001');
  await gotoInteractive(page, '/admin/quality');

  await expect(page.getByRole('heading', { name: 'Recommendation quality' })).toBeVisible();
  await expect(page.getByRole('row').filter({ hasText: 'Alice' }).getByRole('link', { name: 'Why this score' })).toBeVisible();
  await page.getByRole('button', { name: 'Evaluate current settings' }).click();

  const experiment = page.getByRole('button', { name: 'current settings', exact: true });
  await expect(experiment).toBeVisible({ timeout: 15_000 });
  await experiment.focus();
  await page.keyboard.press('Enter');
  await expect(experiment).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('heading', { name: 'Hardest to predict' })).toBeVisible();
  await expect(page.getByRole('cell', { name: 'Moon', exact: true })).toBeVisible();
});

test('admin can expand a score breakdown by keyboard', async ({ page }) => {
  await signInAs(page, '1001');
  await gotoInteractive(page, '/admin/explain/1001');

  await expect(page.getByRole('heading', { name: 'Taste clusters' })).toBeVisible();
  const row = page.getByRole('button', { name: /The Matrix/ });
  await expect(row).toHaveAttribute('aria-expanded', 'false');
  // The circuit rescores once it connects and re-renders the table, which can swallow an early key press.
  await expect(async () => {
    await row.focus();
    await page.keyboard.press('Enter');
    await expect(row).toHaveAttribute('aria-expanded', 'true', { timeout: 1_000 });
  }).toPass({ timeout: 15_000 });
  await expect(page.getByText('No TMDB link from anything this profile watched.')).toBeVisible();
});

test('user can search and rate a catalog title', async ({ page }) => {
  await signInAs(page, '1002');
  await page.getByRole('link', { name: 'Search' }).click();
  await page.getByRole('searchbox', { name: 'Search movies and TV shows' }).fill('Arrival');
  await page.getByRole('button', { name: 'Search' }).click();

  await expect(page.getByRole('heading', { name: 'Arrival' })).toBeVisible();
  await page.getByRole('button', { name: 'Like Arrival' }).click();

  await expect(page.getByRole('status')).toContainText('Liked Arrival');
});

test('user can request an eligible recommendation through Seerr', async ({ page }) => {
  await signInAs(page, '1001');
  await page.getByRole('button', { name: 'Request' }).first().click();

  await expect(page.getByRole('status')).toContainText('Requested The Matrix.');
  await expect(page.locator('.rec-card').filter({ hasText: 'The Matrix' }).locator('.badge')).toHaveText('Requested');
});

test('profile can generate a Telegram pairing command', async ({ page }) => {
  await signInAs(page, '1001');
  await page.getByRole('link', { name: 'Telegram' }).click();
  await page.getByRole('button', { name: 'Generate link code' }).click();

  await expect(page.locator('.link-code code')).toContainText('/start ');
  await expect(page.getByRole('button', { name: 'Check connection' })).toBeVisible();
});

test('linked profile can toggle daily digests and unlink Telegram', async ({ page }) => {
  await signInAs(page, '1002');
  await page.getByRole('link', { name: 'Telegram' }).click();
  const digest = page.getByRole('checkbox', { name: 'Receive daily recommendations' });

  await expect(digest).not.toBeChecked();
  await digest.check();
  await expect(page.getByRole('status')).toContainText('Daily recommendations enabled.');
  await expect(digest).toBeChecked();
  await page.getByRole('button', { name: 'Unlink Telegram' }).click();

  await expect(page.getByRole('status')).toContainText('Telegram disconnected.');
  await expect(page.getByRole('button', { name: 'Generate link code' })).toBeVisible();
});

const loginErrors = [
  ['expired', 'The sign-in took too long. Please try again.'],
  ['not-approved', "Plex didn't confirm the sign-in. Please try again."],
  ['no-access', "None of this Plex account's profiles use this server yet. If you were just added, try again after the next sync."],
  ['unknown', 'Something went wrong while signing in.'],
];

for (const [code, message] of loginErrors) {
  test(`login displays the ${code} error`, async ({ page }) => {
    await page.goto(`/login?error=${code}`);

    await expect(page.getByText(message, { exact: true })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Sign in with Plex' })).toBeVisible();
  });
}

test('unknown paths render the not-found page', async ({ page }) => {
  await page.goto('/not-a-recommendarr-page');

  await expect(page.getByRole('heading', { name: 'Not Found' })).toBeVisible();
});
