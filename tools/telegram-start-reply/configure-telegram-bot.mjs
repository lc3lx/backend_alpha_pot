#!/usr/bin/env node
/**
 * One-shot: point Telegram Mini App to MINIAPP_URL (/app/ on www).
 * Uses setChatMenuButton + deleteWebhook (so getUpdates polling in start-reply.mjs works).
 *
 * Env (from scaralpha.env):
 *   TELEGRAM_BOT_TOKEN   required
 *   MINIAPP_URL          default https://www.scaralphaai.com/app/
 *   BUTTON_TEXT          default Open Scar Alpha
 *
 * Run:
 *   cd backend && node tools/telegram-start-reply/configure-telegram-bot.mjs
 */
const token = process.env.TELEGRAM_BOT_TOKEN || '';
const miniAppUrl = (process.env.MINIAPP_URL || 'https://www.scaralphaai.com/app/').trim();
const buttonText = (process.env.BUTTON_TEXT || 'Open Scar Alpha').trim();

if (!token || token.includes('REPLACE')) {
  console.error('TELEGRAM_BOT_TOKEN is required (set in scaralpha.env)');
  process.exit(1);
}

if (!miniAppUrl.includes('/app')) {
  console.warn('WARN: MINIAPP_URL should usually end with /app/ — got:', miniAppUrl);
}

const api = (method, body) =>
  fetch(`https://api.telegram.org/bot${token}/${method}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body ?? {}),
  }).then(async (r) => {
    const j = await r.json();
    if (!j.ok) throw new Error(`${method}: ${JSON.stringify(j)}`);
    return j.result;
  });

async function main() {
  console.log('Mini App URL →', miniAppUrl);

  await api('deleteWebhook', { drop_pending_updates: false });
  console.log('deleteWebhook OK (polling / start-reply can receive /start)');

  await api('setChatMenuButton', {
    menu_button: {
      type: 'web_app',
      text: buttonText,
      web_app: { url: miniAppUrl },
    },
  });
  console.log('setChatMenuButton OK');

  const me = await api('getMe');
  console.log(`Bot @${me.username} — open chat, menu button should open: ${miniAppUrl}`);
  console.log('Restart PM2 if needed: ./tools/telegram-start-reply/start-pm2.sh');
}

main().catch((e) => {
  console.error(e?.message || e);
  process.exit(1);
});
