# grader-app — deployment to the judge host.
#
# Everything here is driven from your machine over ssh. Override any variable on
# the command line, e.g. `make deploy JUDGE=ops@157.245.83.194`.
#
# Builds happen locally: the judge host has a C/C++ toolchain by design, but no
# .NET SDK. See llm_docs/grader_runner.md.

# NB: make keeps the whitespace before an inline `#`, so comments stay on their
# own lines here — a trailing-space variable silently corrupts every path below.

# ssh alias (~/.ssh/config), where User is already set
JUDGE    ?= judge
# isolate box id; 0 and 1 are the ones with pinned cpus
BOX      ?= 0

# local publish output
OUT      := out/judge
# remote staging dir, owned by the ssh user
STAGE    := /tmp/judge-drop
# remote install dir, owned by judge
APP      := /opt/judge
PROBLEMS := /var/lib/grader/problems
BUILD    := /var/lib/grader/build

# Staging exists because /opt/judge and /var/lib/grader are owned by `judge`, which
# the ssh user cannot write to directly. scp into a dir it does own, then sudo-install.
#
# sudo steps use `ssh -t` so a password prompt has a tty. That is also why the copy
# in and the install are separate commands: a tty mangles binary data on a pipe.

SHELL := /bin/bash
.DEFAULT_GOAL := help
.PHONY: help publish deploy problems push grade ssh clean

help: ## Show this help
	@grep -hE '^[a-z][a-zA-Z_-]*:.*?## ' $(MAKEFILE_LIST) \
	  | awk 'BEGIN{FS=":.*?## "}{printf "  \033[36m%-10s\033[0m %s\n", $$1, $$2}'
	@echo
	@echo "  vars: JUDGE=$(JUDGE) BOX=$(BOX)"
	@echo "  e.g.  make grade P=two-sum"

publish: ## Build a Release publish into out/judge
	dotnet publish grader-app -c Release -o $(OUT)

deploy: publish ## Build and install grader-app on the judge host
	ssh $(JUDGE) 'rm -rf $(STAGE) && mkdir -p $(STAGE)'
	scp -q -r $(OUT)/. $(JUDGE):$(STAGE)/
	ssh -t $(JUDGE) 'set -e; \
	  sudo rm -rf $(APP); \
	  sudo mkdir -p $(APP); \
	  sudo cp -a $(STAGE)/. $(APP)/; \
	  sudo chown -R judge:judge $(APP); \
	  rm -rf $(STAGE)'
	@echo "deployed to $(JUDGE):$(APP)"

problems: ## Sync samples/problems to the judge host
	ssh $(JUDGE) 'rm -rf $(STAGE)-problems && mkdir -p $(STAGE)-problems'
	scp -q -r samples/problems/. $(JUDGE):$(STAGE)-problems/
	ssh -t $(JUDGE) 'set -e; \
	  sudo mkdir -p $(PROBLEMS); \
	  sudo cp -a $(STAGE)-problems/. $(PROBLEMS)/; \
	  sudo chown -R judge:judge $(PROBLEMS); \
	  rm -rf $(STAGE)-problems'
	@echo "synced samples/problems to $(JUDGE):$(PROBLEMS)"

push: deploy problems ## deploy + problems

# Compiles the reference solution and grades it — the end-to-end smoke test.
# Point SRC elsewhere to grade a different source file.
P   ?=
SRC ?= $(PROBLEMS)/$(P)/solution.cpp

grade: ## Compile and grade a problem: make grade P=two-sum [BOX=0] [SRC=...]
	@test -n "$(P)" || { echo "usage: make grade P=<problem-id> [BOX=0] [SRC=<remote .cpp>]"; exit 2; }
	ssh -t $(JUDGE) 'set -e; \
	  sudo -u judge mkdir -p $(BUILD); \
	  sudo -u judge g++ -O2 -std=c++20 -o $(BUILD)/$(P) $(SRC); \
	  sudo -u judge dotnet $(APP)/grader-app.dll $(PROBLEMS)/$(P)/problem.toml $(BUILD)/$(P) $(BOX)'

ssh: ## Open a shell on the judge host
	ssh $(JUDGE)

clean: ## Remove the local publish output
	rm -rf $(OUT)
