#!/usr/bin/env node
/**
 * Minimal Telegram helper: reply to /start with a Web App button.
 * Does NOT replace Scar Alpha API auth — only opens the Mini App URL.
 *
 * Env:
 *   TELEGRAM_BOT_TOKEN   (required)
 *   MINIAPP_URL          (default https://www.scaralphaai.com/)
 *   BUTTON_TEXT          (default Open Scar Alpha)
 *
 * Run:
 *   node start-reply.mjs
 *   pm2 start start-reply.mjs --name scaralpha-tg-start
 */
const token = process.env.TELEGRAM_BOT_TOKEN || '';
const miniAppUrl = (process.env.MINIAPP_URL || 'https://www.scaralphaai.com/').trim();
const buttonText = (process.env.BUTTON_TEXT || 'Open Scar Alpha').trim();

if (!token || token.includes('REPLACE')) {
  console.error('TELEGRAM_BOT_TOKEN is required');
  process.exit(1);
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

async function ensureMenuButton() {
  await api('setChatMenuButton', {
    menu_button: {
      type: 'web_app',
      text: buttonText,
      web_app: { url: miniAppUrl },
    },
  });
  console.log('Menu button OK →', miniAppUrl);
}

async function replyStart(chatId) {
  await api('sendMessage', {
    chat_id: chatId,
    text: 'Welcome to Scar Alpha.\nTap the button below to open the app.',
    reply_markup: {
      inline_keyboard: [
        [
          {
            text: buttonText,
            web_app: { url: miniAppUrl },
          },
        ],
      ],
    },
  });
}

async function main() {
  await ensureMenuButton();
  let offset = 0;
  console.log('Listening for /start ...');

  for (;;) {
    try {
      const updates = await api('getUpdates', {
        offset,
        timeout: 30,
        allowed_updates: ['message'],
      });

      for (const u of updates) {
        offset = u.update_id + 1;
        const text = u.message?.text?.trim() || '';
        const chatId = u.message?.chat?.id;
        if (!chatId) continue;
        if (text === '/start' || text.startsWith('/start ')) {
          console.log(' /start from', chatId);
          await replyStart(chatId);
        }
      }
    } catch (e) {
      console.error('poll error:', e?.message || e);
      await new Promise((r) => setTimeout(r, 2000));
    }
  }
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
