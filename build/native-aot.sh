#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/.." && pwd)
configuration=${CONFIGURATION:-Release}

if [ "$#" -gt 0 ]; then
	rids="$*"
else
	case "$(uname -s)" in
		Linux*) os=linux ;;
		Darwin*) os=osx ;;
		MINGW*|MSYS*|CYGWIN*) os=win ;;
		*) echo "Unsupported OS for Native AOT publish." >&2; exit 1 ;;
	esac

	case "$(uname -m)" in
		x86_64|amd64) arch=x64 ;;
		aarch64|arm64) arch=arm64 ;;
		*) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
	esac

	rids="$os-$arch"
fi

for rid in $rids; do
	output="$repo_root/artifacts/native-aot/$rid"
	echo "Publishing gif320sharp Native AOT for $rid..."
	dotnet publish "$repo_root/Gif320Sharp/Gif320Sharp.csproj" \
		-c "$configuration" \
		-r "$rid" \
		-p:PublishAot=true \
		-p:SelfContained=true \
		-p:StripSymbols=true \
		-o "$output" || {
			echo "Native AOT publish failed for $rid." >&2
			echo "Install the RID's native compiler toolchain and runtime packs, or build that RID on its native OS runner." >&2
			exit 1
		}
done
