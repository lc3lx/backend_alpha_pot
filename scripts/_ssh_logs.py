import os
import sys
import paramiko

host = "31.97.229.234"
password = os.environ["SCAR_SSH_PASS"]

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(host, username="root", password=password, timeout=20, allow_agent=False, look_for_keys=False)

cmds = [
    "curl -sS http://127.0.0.1:5207/health; echo; curl -sS http://127.0.0.1:8100/health; echo",
    "tail -n 80 /home/web/backend/logs/pm2-out.log",
    "tail -n 40 /home/web/backend/logs/pm2-error.log",
    "tail -n 80 /home/web/backend/brokers/logs/pm2-out.log",
]
for cmd in cmds:
    print("===== " + cmd[:90])
    stdin, stdout, stderr = ssh.exec_command(cmd, timeout=40)
    sys.stdout.buffer.write((stdout.read() + stderr.read()).decode("utf-8", "replace").encode("utf-8", "replace"))
    print()
ssh.close()
