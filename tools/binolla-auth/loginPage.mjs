/** Wait once for the Quotex form, then distinguish a challenge from a missing field. */
export async function waitForQuotexLoginPage(page, timeout = 8000) {
  try {
    await page.locator([
      'input[name="email"]', 'input[inputmode="email"]', 'input[type="email"]',
      'input[autocomplete="username"]', 'input[placeholder*="mail" i]',
    ].join(',')).first().waitFor({ state: 'attached', timeout });
  } catch {
    const title = await page.title();
    const body = await page.locator('body').innerText();
    if (/just a moment|performing security verification|checking your browser|verify you are (?:a )?human/i.test(`${title} ${body}`)) {
      throw new Error('Quotex requires Cloudflare verification before login (HTTP 403). The account credentials were not submitted.');
    }
    throw new Error('Quotex did not display its login form. Check the configured login URL and connection.');
  }
}
