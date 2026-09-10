/**
 * PM2 process file — Binolla guided-login server.
 *
 * Holds a Chromium instance open per in-flight login so the user can answer Binolla's
 * CAPTCHA themselves. Separate from the API because a browser cannot live inside the
 * one-shot capture the API already spawns.
 *
 *   chmod +x start-interactive-pm2.sh
 *   ./start-interactive-pm2.sh
 */
const path = require('path');

const host = process.env.BINOLLA_INTERACTIVE_HOST || '127.0.0.1';
const port = process.env.BINOLLA_INTERACTIVE_PORT || '8110';

module.exports = {
  apps: [
    {
      name: 'scaralpha-login',
      cwd: __dirname,
      script: path.join(__dirname, 'interactive-server.mjs'),
      interpreter: 'node',
      instances: 1,
      // Sessions and their browsers live in this process's memory, so a second copy would
      // answer with a browser that never saw the user's login. Never cluster this.
      exec_mode: 'fork',
      autorestart: true,
      watch: false,
      // Chromium is heavy and several logins can overlap; below this PM2 kills healthy
      // sessions mid-challenge.
      max_memory_restart: '1500M',
      kill_timeout: 10000,
      exp_backoff_restart_delay: 2000,
      error_file: path.join(__dirname, 'logs', 'pm2-error.log'),
      out_file: path.join(__dirname, 'logs', 'pm2-out.log'),
      merge_logs: true,
      time: true,
      max_size: '20M',
      retain: 3,
      env: {
        BINOLLA_INTERACTIVE_HOST: host,
        BINOLLA_INTERACTIVE_PORT: port,
        BINOLLA_INTERACTIVE_TOKEN: process.env.BINOLLA_INTERACTIVE_TOKEN || '',
        // The same proxy the automated capture uses: the challenge has to be answered
        // from the same exit the login is made from, or Binolla sees two origins.
        BINOLLA_AUTH_PROXY: process.env.BINOLLA_AUTH_PROXY || '',
        PATH: process.env.PATH || '',
      },
    },
  ],
};
