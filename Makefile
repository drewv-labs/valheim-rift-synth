SHELL := /bin/bash
EXTRA_PATH := /opt/homebrew/bin:/usr/local/share/dotnet:$(HOME)/.dotnet:$(HOME)/.local/bin
export PATH := $(EXTRA_PATH):$(PATH)

BABBOX_HOST ?= babbagebox-rtx-lan
BABBOX_REF_STACK ?= /opt/stacks/valheim-server
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

.PHONY: help sync-libs build-mod ensure-sandbox deploy-mod synth-rift logs clean

help:
	@echo "valheim-rift-synth targets:"
	@echo "  make sync-libs       Rsync Valheim & BepInEx assemblies from BabBox into .lib/"
	@echo "  make build-mod       Compile ValheimRiftEngine.dll (Release, 0 warnings)"
	@echo "  make ensure-sandbox  Provision RiftSandbox dedicated server on BabBox (ports 2458-2459)"
	@echo "  make deploy-mod      Push ValheimRiftEngine.dll to BabBox RiftSandbox & restart service"
	@echo "  make synth-rift      Synthesize & deploy a Rift Manifest via tools/rift_synth (PROMPT=...)"
	@echo "  make logs            Tail BepInEx LogOutput.log on BabBox RiftSandbox"

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
	@echo "==> Ensuring sandbox Valheim stack at $(BABBOX_HOST):$(BABBOX_SANDBOX_STACK) (ports $(SANDBOX_PORT)-$(SANDBOX_QUERY_PORT), world $(SANDBOX_WORLD)) ..."
	@ssh $(BABBOX_HOST) '\
		set -e; \
		if [ ! -d "$(BABBOX_SANDBOX_STACK)" ]; then \
			sudo mkdir -p "$(BABBOX_SANDBOX_STACK)"; \
			sudo chown -R $$(id -u):$$(id -g) "$(BABBOX_SANDBOX_STACK)"; \
		fi; \
		mkdir -p "$(BABBOX_SANDBOX_STACK)/config/bepinex/plugins/ValheimRiftEngine" \
		         "$(BABBOX_SANDBOX_STACK)/config/bepinex/patchers" \
		         "$(BABBOX_SANDBOX_STACK)/config/bepinex/ValheimRiftEngine" \
		         "$(BABBOX_SANDBOX_STACK)/config/worlds_local" \
		         "$(BABBOX_SANDBOX_STACK)/data"; \
		if [ -f "$(BABBOX_REF_STACK)/config/adminlist.txt" ] && [ ! -f "$(BABBOX_SANDBOX_STACK)/config/adminlist.txt" ]; then \
			cp "$(BABBOX_REF_STACK)/config/adminlist.txt" "$(BABBOX_SANDBOX_STACK)/config/adminlist.txt"; \
		fi; \
		if [ ! -d "$(BABBOX_SANDBOX_STACK)/data/bepinex" ]; then \
			echo "Seeding Valheim + BepInEx binaries from $(BABBOX_REF_STACK)/data ..."; \
			cp -a "$(BABBOX_REF_STACK)/data/server" "$(BABBOX_SANDBOX_STACK)/data/server"; \
			cp -a "$(BABBOX_REF_STACK)/data/dl" "$(BABBOX_SANDBOX_STACK)/data/dl"; \
			cp -a "$(BABBOX_REF_STACK)/data/bepinex" "$(BABBOX_SANDBOX_STACK)/data/bepinex"; \
			rm -f "$(BABBOX_SANDBOX_STACK)/data/bepinex/BepInEx/LogOutput.log"; \
			rm -rf "$(BABBOX_SANDBOX_STACK)/data/bepinex/BepInEx/plugins/"*; \
			ln -sfn /config/bepinex "$(BABBOX_SANDBOX_STACK)/data/bepinex/BepInEx/config"; \
		fi; \
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
			"      - TZ=America/Chicago" \
			"      - BACKUPS=false" \
			"      - SERVER_ARGS=-setkey nobuildcost" \
			"    volumes:" \
			"      - ./config:/config" \
			"      - ./data:/opt/valheim" \
			> "$(BABBOX_SANDBOX_STACK)/docker-compose.yml"'

deploy-mod: build-mod ensure-sandbox
	@echo "==> Deploying $(MOD_DLL) to $(BABBOX_HOST) ..."
	scp "$(MOD_DLL)" "$(BABBOX_HOST):$(BABBOX_SANDBOX_STACK)/config/bepinex/plugins/ValheimRiftEngine/ValheimRiftEngine.dll"
	@ssh $(BABBOX_HOST) '\
		set -e; \
		mkdir -p "$(BABBOX_SANDBOX_STACK)/data/bepinex/BepInEx/plugins/ValheimRiftEngine" \
		         "$(BABBOX_SANDBOX_STACK)/config/bepinex/ValheimRiftEngine"; \
		cp "$(BABBOX_SANDBOX_STACK)/config/bepinex/plugins/ValheimRiftEngine/ValheimRiftEngine.dll" \
		   "$(BABBOX_SANDBOX_STACK)/data/bepinex/BepInEx/plugins/ValheimRiftEngine/ValheimRiftEngine.dll"; \
		ln -sfn /config/bepinex "$(BABBOX_SANDBOX_STACK)/data/bepinex/BepInEx/config"; \
		if docker ps --format "{{.Names}}" | grep -qx "$(BABBOX_SANDBOX_CONTAINER)"; then \
			echo "Restarting valheim-server inside $(BABBOX_SANDBOX_CONTAINER) ..."; \
			rm -f "$(BABBOX_SANDBOX_STACK)/data/bepinex/BepInEx/LogOutput.log"; \
			docker exec "$(BABBOX_SANDBOX_CONTAINER)" supervisorctl restart valheim-server; \
		else \
			echo "Starting $(BABBOX_SANDBOX_CONTAINER) compose stack ..."; \
			rm -f "$(BABBOX_SANDBOX_STACK)/data/bepinex/BepInEx/LogOutput.log"; \
			cd "$(BABBOX_SANDBOX_STACK)" && docker compose up -d; \
		fi'
	@echo "==> Deployed to $(BABBOX_SANDBOX_CONTAINER) (UDP $(SANDBOX_PORT)-$(SANDBOX_QUERY_PORT), world $(SANDBOX_WORLD))."

synth-rift:
	"$(UV)" run --project tools/rift_synth rift-synth "$(PROMPT)"

logs:
	ssh $(BABBOX_HOST) 'tail -n 80 "$(BABBOX_SANDBOX_STACK)/data/bepinex/BepInEx/LogOutput.log"'

clean:
	"$(DOTNET)" clean $(CSPROJ) -c Release || true
