-- Scar Alpha — MySQL bootstrap (run as root or admin user)
-- Example:
--   mysql -u root -p < backend/scripts/init-mysql.sql

CREATE DATABASE IF NOT EXISTS scaralpha
  CHARACTER SET utf8mb4
  COLLATE utf8mb4_unicode_ci;

-- Optional dedicated user (adjust password):
CREATE USER IF NOT EXISTS 'scaralpha'@'localhost' IDENTIFIED BY 'CHANGE_ME';
CREATE USER IF NOT EXISTS 'scaralpha'@'127.0.0.1' IDENTIFIED BY 'CHANGE_ME';

GRANT ALL PRIVILEGES ON scaralpha.* TO 'scaralpha'@'localhost';
GRANT ALL PRIVILEGES ON scaralpha.* TO 'scaralpha'@'127.0.0.1';
FLUSH PRIVILEGES;

-- Connection string for scaralpha.env:
-- DATABASE_PROVIDER=MySql
-- DATABASE_CONNECTION_STRING='Server=127.0.0.1;Port=3306;Database=scaralpha;User=scaralpha;Password=CHANGE_ME'
