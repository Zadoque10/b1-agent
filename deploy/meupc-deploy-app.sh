#!/usr/bin/env bash
# Installs/updates B1 Agent on meupc as user "user" (no sudo). Does NOT touch DNS or the Cloudflare tunnel.
#  app: ~/apps/b1agent/current (self-contained .NET), pm2 process "b1agent" on 127.0.0.1:4300
set -euo pipefail
[ -s "$HOME/.nvm/nvm.sh" ] && . "$HOME/.nvm/nvm.sh" >/dev/null 2>&1
export PATH="$PATH:$HOME/.npm-global/bin:/usr/local/bin"

APP="$HOME/apps/b1agent"
PORT=4300

echo "== release"
mkdir -p "$APP/releases" "$APP/config"
REL="$APP/releases/$(date +%Y%m%d%H%M%S)"
mkdir -p "$REL"
tar xzf /tmp/b1agent.tar.gz -C "$REL"
chmod +x "$REL/B1Agent.Api"

# Settings that survive redeploys (put Llm:ApiKey here). Only readable by this user.
if [ ! -f "$APP/config/appsettings.Production.json" ]; then
  printf '{\n  "Llm": { "ApiKey": "", "Endpoint": "", "Model": "gpt-4o-mini" }\n}\n' > "$APP/config/appsettings.Production.json"
fi
chmod 600 "$APP/config/appsettings.Production.json"
ln -sf "$APP/config/appsettings.Production.json" "$REL/appsettings.Production.json"
ln -sfn "$REL" "$APP/current"
ls -1dt "$APP"/releases/* | tail -n +4 | xargs -r rm -rf   # keep the last 3 releases

echo "== pm2"
cat > "$APP/ecosystem.config.js" <<JS
module.exports = { apps: [{
  name: "b1agent",
  script: "$APP/current/B1Agent.Api",
  cwd: "$APP/current",
  interpreter: "none",
  env: { ASPNETCORE_URLS: "http://127.0.0.1:$PORT", ASPNETCORE_ENVIRONMENT: "Production", DOTNET_gcServer: "0" },
  max_memory_restart: "400M"
}]};
JS
pm2 delete b1agent >/dev/null 2>&1 || true
pm2 start "$APP/ecosystem.config.js"
pm2 save >/dev/null

for i in $(seq 1 30); do
  curl -fsS "http://127.0.0.1:$PORT/api/health" > /tmp/b1agent-health.json 2>/dev/null && break
  sleep 1
done
echo "local health: $(cat /tmp/b1agent-health.json 2>/dev/null || echo NOT RESPONDING)"
pm2 jlist | python3 -c "import json,sys;[print(p['name'],p['pm2_env']['status'],'restarts',p['pm2_env']['restart_time']) for p in json.load(sys.stdin) if p['name']=='b1agent']"
echo "== done"
