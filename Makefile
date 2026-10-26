SOLUTION       = Jellyfin.Plugin.CcSync.slnx
PROJECT        = Jellyfin.Plugin.CcSync/Jellyfin.Plugin.CcSync.csproj
ASSEMBLY       = Jellyfin.Plugin.CcSync
FRAMEWORK      = net10.0
VERSION       ?= $(shell sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
CHANGELOG     ?=
JELLYFIN_CONFIG ?= $(HOME)/.config/jellyfin

.PHONY: build test publish package release deploy clean

build:
	dotnet build $(SOLUTION) -c Release

test:
	dotnet test --solution $(SOLUTION) -c Release

publish:
	dotnet publish $(PROJECT) -c Release -f $(FRAMEWORK) -o artifacts/publish

# Builds artifacts/jellyfin-cc-sync_<version>.zip and prints its md5.
package:
	python3 scripts/package.py --changelog "$(CHANGELOG)"

# Bumps the version, drops the zip in releases/ and records it in manifest.json,
# which points at the raw file in this repo. Commit and push the result; the
# checksum only matches the zip that is committed alongside it.
release:
	python3 scripts/package.py --version "$(VERSION)" --changelog "$(CHANGELOG)" --manifest --in-repo

# Drops the assembly into a local server. A folder without meta.json is still
# loaded, so no packaging step is needed while iterating.
deploy: publish
	mkdir -p "$(JELLYFIN_CONFIG)/plugins/$(ASSEMBLY)_$(VERSION)"
	cp artifacts/publish/$(ASSEMBLY).dll "$(JELLYFIN_CONFIG)/plugins/$(ASSEMBLY)_$(VERSION)/"
	@echo "Installed to $(JELLYFIN_CONFIG)/plugins/$(ASSEMBLY)_$(VERSION) — restart Jellyfin"

clean:
	rm -rf artifacts
	dotnet clean $(SOLUTION) -c Release
