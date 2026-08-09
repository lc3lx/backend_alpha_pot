/**
 * PM2 process file — Scar Alpha Backend API
 *
 * Usage (from this folder):
 *   chmod +x start-backend-pm2.sh
 *   ./start-backend-pm2.sh          # publish + pm2 start/restart
 *   ./start-backend-pm2.sh stop
 *   ./start-backend-pm2.sh logs
 *   ./start-backend-pm2.sh status
 *
 * Env is loaded by start-backend-pm2.sh from scaralpha.env before PM2 reads this file.
 */
const path = require('path');

const port = process.env.BACKEND_PORT || '5207';

module.exports = {
  apps: [
    {
      name: 'scaralpha-api',
      cwd: path.join(__dirname, 'publish'),
      script: 'dotnet',
      args: 'ScarAlpha.Api.dll',
      interpreter: 'none',
      instances: 1,
      exec_mode: 'fork',
      autorestart: true,
      watch: false,
      max_memory_restart: '512M',
      kill_timeout: 10000,
      exp_backoff_restart_delay: 2000,
      error_file: path.join(__dirname, 'logs', 'pm2-error.log'),
      out_file: path.join(__dirname, 'logs', 'pm2-out.log'),
      merge_logs: true,
      time: true,
      env: {
        ASPNETCORE_ENVIRONMENT: process.env.ASPNETCORE_ENVIRONMENT || 'Development',
        ASPNETCORE_URLS: process.env.ASPNETCORE_URLS || `http://0.0.0.0:${port}`,
        DATABASE_PROVIDER: process.env.DATABASE_PROVIDER || 'InMemory',
        DATABASE_INMEMORY_NAME: process.env.DATABASE_INMEMORY_NAME || 'ScarAlphaVps',
        DATABASE_CONNECTION_STRING: process.env.DATABASE_CONNECTION_STRING || '',
        JWT_SECRET: process.env.JWT_SECRET || '',
        JWT_ISSUER: process.env.JWT_ISSUER || 'ScarAlpha',
        JWT_AUDIENCE: process.env.JWT_AUDIENCE || 'ScarAlpha.App',
        BINOLLA_TOKEN_ENCRYPTION_KEY: process.env.BINOLLA_TOKEN_ENCRYPTION_KEY || '',
        TELEGRAM_BOT_TOKEN: process.env.TELEGRAM_BOT_TOKEN || '',
        CORS_ORIGINS: process.env.CORS_ORIGINS || '',
        ADMIN_TELEGRAM_USER_IDS: process.env.ADMIN_TELEGRAM_USER_IDS || '',
        BINOLLA_AUTH_PROXY: process.env.BINOLLA_AUTH_PROXY || '',
        DOTNET_ROOT: process.env.DOTNET_ROOT || '',
        PATH: process.env.PATH || '',
      },
    },
  ],
};
