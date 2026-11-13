SHELL := /bin/bash
EXTRA_PATH := /opt/homebrew/bin:/usr/local/share/dotnet:$(HOME)/.dotnet:$(HOME)/.local/bin
export PATH := $(EXTRA_PATH):$(PATH)

BABBOX_HOST ?= babbagebox-rtx-lan
BABBOX_REF_STACK ?= /opt/stacks/rift-sandbox
BABBOX_SANDBOX_STACK ?= /opt/stacks/rift-sandbox
BABBOX_SANDBOX_CONTAINER ?= rift-sandbox-server
SANDBOX_PORT ?= 2458
SANDBOX_QUERY_PORT ?= 2459
SANDBOX_WORLD ?= RiftSandbox
SANDBOX_NAME ?= RiftSandbox
SANDBOX_PASS ?= acidboots

REMOTE_MANAGED := $(BABBOX_REF_STACK)/data/bepinex/valheim_server_Data/Managed
REMOTE_BEPINEX_CORE := $(BABBOX_REF_STACK)/data/bepinex/BepInEx/core

DOTNET ?= $(shell PATH="$(EXTRA_PATH):$$PATH" command -v dotnet)
UV ?= $(shell PATH="$(EXTRA_PATH):$$PATH" command -v uv)
CSPROJ := src/ValheimRiftEngine/ValheimRiftEngine.csproj
MOD_DLL := src/ValheimRiftEngine/bin/Release/netstandard2.1/ValheimRiftEngine.dll
PROMPT ?= Keystone 5 Dwarven Forge Run: heavy fire mages and armored brutes, punish doorway camping, reward aggressive melee

.PHONY: help sync-libs build-mod ensure-sandbox deploy-mod rollback-mod synth-rift logs clean

help:
	@echo "valheim-rift-synth targets:"
	@echo "  make sync-libs       Rsync Valheim & BepInEx assemblies from BabBox into .lib/"
	@echo "  make build-mod       Compile ValheimRiftEngine.dll (Release, 0 warnings)"
	@echo "  make ensure-sandbox  Provision RiftSandbox dedicated server on BabBox (ports 2458-2459)"
	@echo "  make deploy-mod      Push ValheimRiftEngine.dll to BabBox RiftSandbox & restart service"
	@echo "  make rollback-mod    Roll back RiftSandbox to ValheimRiftEngine.dll.prev via riftctl"
	@echo "  make synth-rift      Synthesize & deploy a Rift Manifest via tools/rift_synth (PROMPT=...)"
	@echo "  make logs            Tail BepInEx LogOutput.log on BabBox RiftSandbox"
	@echo ""
	@echo "Tailscale Collaborator Usage (game-dev):"
	@echo "  make sync-libs BABBOX_HOST=game-dev@100.81.125.6"
	@echo "  make deploy-mod BABBOX_HOST=game-dev@100.81.125.6"
	@echo "  make synth-rift BABBOX_HOST=game-dev@100.81.125.6 PROMPT=\"...\""

sync-libs:
	@mkdir -p .lib
	@echo "==> Syncing Managed assemblies from $(BABBOX_HOST):$(REMOTE_MANAGED) ..."
	rsync -avz \
		--include='assembly_valheim.dll' \
		--include='assembly_utils.dll' \
		--include='assembly_guiutils.dll' \
		--include='gui_framework.dll' \
		--include='SoftReferenceableAssets.dll' \
		--include='Splatform.dll' \
		--include='com.rlabrecque.steamworks.net.dll' \
		--include='PlayFab.dll' \
		--include='PlayFabParty.dll' \
		--include='Unity.TextMeshPro.dll' \
		--include='UnityEngine*.dll' \
		--exclude='*' \
		"$(BABBOX_HOST):$(REMOTE_MANAGED)/" .lib/
	@echo "==> Syncing BepInEx core assemblies from $(BABBOX_HOST):$(REMOTE_BEPINEX_CORE) ..."
	rsync -avz \
		--include='BepInEx.dll' \
		--include='0Harmony.dll' \
		--include='BepInEx.Harmony.dll' \
		--exclude='*' \
		"$(BABBOX_HOST):$(REMOTE_BEPINEX_CORE)/" .lib/
	@echo "==> Synced $$(ls -1 .lib/*.dll | wc -l | tr -d ' ') assemblies into .lib/"

build-mod:
	"$(DOTNET)" build $(CSPROJ) -c Release --nologo

ensure-sandbox:
	@echo "==> Checking sandbox Valheim stack at $(BABBOX_HOST):$(BABBOX_SANDBOX_STACK) ..."
	@ssh $(BABBOX_HOST) '\
		set -e; \
		if [ -x /usr/local/sbin/riftctl ] && [ "$$(id -un)" = "game-dev" ]; then \
			sudo /usr/local/sbin/riftctl status >/dev/null; \
			exit 0; \
		fi; \
		if [ ! -d "$(BABBOX_SANDBOX_STACK)" ]; then \
			sudo mkdir -p "$(BABBOX_SANDBOX_STACK)"; \
			sudo chown -R $$(id -u):$$(id -g) "$(BABBOX_SANDBOX_STACK)"; \
		fi; \
		mkdir -p "$(BABBOX_SANDBOX_STACK)/config/bepinex/plugins/ValheimRiftEngine" \
		         "$(BABBOX_SANDBOX_STACK)/config/bepinex/patchers" \
		         "$(BABBOX_SANDBOX_STACK)/config/bepinex/ValheimRiftEngine" \
		         "$(BABBOX_SANDBOX_STACK)/config/worlds_local" \
		         "$(BABBOX_SANDBOX_STACK)/data"; \
		if [ ! -f "$(BABBOX_SANDBOX_STACK)/docker-compose.yml" ]; then \
			printf "%s\n" \
				"services:" \
				"  valheim:" \
				"    image: ghcr.io/community-valheim-tools/valheim-server:latest" \
				"    container_name: $(BABBOX_SANDBOX_CONTAINER)" \
				"    cap_add:" \
				"      - sys_nice" \
				"    stop_grace_period: 120s" \
				"    restart: unless-stopped" \
				"    ports:" \
				"      - \"$(SANDBOX_PORT):$(SANDBOX_PORT)/udp\"" \
				"      - \"$(SANDBOX_QUERY_PORT):$(SANDBOX_QUERY_PORT)/udp\"" \
				"    environment:" \
				"      - SERVER_NAME=$(SANDBOX_NAME)" \
				"      - WORLD_NAME=$(SANDBOX_WORLD)" \
				"      - SERVER_PORT=$(SANDBOX_PORT)" \
				"      - SERVER_PASS=$(SANDBOX_PASS)" \
				"      - SERVER_PUBLIC=false" \
				"      - CROSSPLAY=false" \
				"      - BEPINEX=true" \
				"      - UPDATE_CRON=0 4 * * *" \
				"      - BEPINEX_UPDATE_CRON=15 4 * * *" \
				"      - PUID=1000" \
				"      - PGID=1000" \
				"      - PERMISSIONS_UMASK=002" \
				"      - TZ=America/Chicago" \
				"      - BACKUPS=false" \
				"      - SERVER_ARGS=-setkey nobuildcost" \
				"    volumes:" \
				"      - ./config:/config" \
				"      - ./data:/opt/valheim" \
				> "$(BABBOX_SANDBOX_STACK)/docker-compose.yml"; \
		fi'

deploy-mod: build-mod ensure-sandbox
	@echo "==> Deploying $(MOD_DLL) to $(BABBOX_HOST) ..."
	@REMOTE_STAGE="/tmp/ValheimRiftEngine-$$$$.dll"; \
	scp "$(MOD_DLL)" "$(BABBOX_HOST):$$REMOTE_STAGE"; \
	ssh $(BABBOX_HOST) "set -e; trap 'rm -f $$REMOTE_STAGE' EXIT; sudo /usr/local/sbin/riftctl deploy-mod $$REMOTE_STAGE"
	@echo "==> Deployed to $(BABBOX_SANDBOX_CONTAINER) (UDP $(SANDBOX_PORT)-$(SANDBOX_QUERY_PORT), world $(SANDBOX_WORLD))."

rollback-mod:
	ssh $(BABBOX_HOST) 'sudo /usr/local/sbin/riftctl rollback'

synth-rift:
	"$(UV)" run --project tools/rift_synth rift-synth --host "$(BABBOX_HOST)" "$(PROMPT)"

logs:
	ssh $(BABBOX_HOST) 'sudo /usr/local/sbin/riftctl logs 80'

clean:
	"$(DOTNET)" clean $(CSPROJ) -c Release || true
