import assert from 'node:assert/strict';
import test from 'node:test';
import { waitForQuotexLoginPage } from './loginPage.mjs';

function page({ present = false, title = '', body = '' } = {}) {
  return {
    title: async () => title,
    locator: () => ({
      innerText: async () => body,
      first: () => ({ waitFor: async ({ timeout, state }) => {
        assert.equal(timeout, 8000);
        assert.equal(state, 'attached');
        if (!present) throw new Error('timeout');
      } }),
    }),
  };
}

test('Quotex challenge is reported before credentials are submitted', async () => {
  await assert.rejects(waitForQuotexLoginPage(page({title: 'Just a moment...'})), /Cloudflare verification/);
  await assert.rejects(waitForQuotexLoginPage(page({body: 'Performing security verification'})), /credentials were not submitted/);
});

test('a missing form without a challenge reports the login URL issue', async () => {
  await assert.rejects(waitForQuotexLoginPage(page({title: 'Not found'})), /configured login URL/);
});

test('an attached login form proceeds even if an old challenge title remains', async () => {
  await waitForQuotexLoginPage(page({present: true, title: 'Just a moment...'}));
});
