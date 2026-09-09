/**
 * PM2 process file — Scar Alpha Broker Gateway (Python/FastAPI)
 *
 * Usage (from this folder):
 *   chmod +x start-gateway-pm2.sh
 *   ./start-gateway-pm2.sh          # venv + install + pm2 start/restart
 *   ./start-gateway-pm2.sh logs
 *   ./start-gateway-pm2.sh status
 *
 * Env is loaded by start-gateway-pm2.sh from scaralpha.env before PM2 reads this file.
 * The .NET API talks to this process over localhost only.
 */
const path = require('path');

const host = process.env.BROKER_GATEWAY_HOST || '127.0.0.1';
const port = process.env.BROKER_GATEWAY_PORT || '8100';
const venvBin = path.join(__dirname, '.venv', 'bin');

module.exports = {
  apps: [
    {
      name: 'scaralpha-brokers',
      cwd: __dirname,
      script: path.join(venvBin, 'uvicorn'),
      args: `app.main:app --host ${host} --port ${port}`,
      interpreter: 'none',
      instances: 1,
      // Sessions live in this process's memory, so a second copy would hold a second set
      // of broker sockets for the same users. Never cluster this.
      exec_mode: 'fork',
      autorestart: true,
      watch: false,
      max_memory_restart: '512M',
      kill_timeout: 8000,
      exp_backoff_restart_delay: 2000,
      error_file: path.join(__dirname, 'logs', 'pm2-error.log'),
      out_file: path.join(__dirname, 'logs', 'pm2-out.log'),
      merge_logs: true,
      time: true,
      max_size: '20M',
      retain: 3,
      env: {
        // Shared secret with the .NET side. Defence in depth only — the localhost
        // binding above is what actually keeps this process private.
        BROKER_GATEWAY_TOKEN: process.env.BROKER_GATEWAY_TOKEN || '',
        // Brokers refuse this server's IP directly, so every outbound broker connection
        // goes through the proxy. BROKER_PROXY wins; BINOLLA_AUTH_PROXY is the fallback
        // so one proxy line in scaralpha.env serves both processes.
        BROKER_PROXY: process.env.BROKER_PROXY || process.env.BINOLLA_AUTH_PROXY || '',
        BINOLLA_AUTH_PROXY: process.env.BINOLLA_AUTH_PROXY || '',
        PYTHONUNBUFFERED: '1',
        PATH: `${venvBin}:${process.env.PATH || ''}`,
      },
    },
  ],
};
