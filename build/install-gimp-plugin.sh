#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/.." && pwd)

configuration=${CONFIGURATION:-Release}
dotnet_framework=${DOTNET_FRAMEWORK:-net10.0}
gimp_version=${GIMP_VERSION:-3.0}
gimp_root=${GIMP_ROOT:-}
restore_arg=--no-restore
if [ "${RESTORE:-0}" = "1" ]; then
	restore_arg=
fi

echo "Installing Gif320Sharp GIMP plug-in from:"
echo "  $repo_root"

if [ "${ALLOW_RUNNING_GIMP:-0}" != "1" ] && command -v pgrep >/dev/null 2>&1; then
	if pgrep -f '(^|/)(gimp|gimp-[0-9.]*|gegl)( |$)' >/dev/null 2>&1; then
		echo "GIMP/GEGL appears to be running. Close it before replacing installed modules." >&2
		echo "Set ALLOW_RUNNING_GIMP=1 to bypass this check." >&2
		exit 1
	fi
fi

if [ "${SKIP_DOTNET_BUILD:-0}" != "1" ]; then
	echo "Building managed CLI..."
	if [ -n "$restore_arg" ]; then
		dotnet build "$repo_root/Gif320Sharp.slnx" --configuration "$configuration" --framework "$dotnet_framework" "$restore_arg"
	else
		dotnet build "$repo_root/Gif320Sharp.slnx" --configuration "$configuration" --framework "$dotnet_framework"
	fi
fi

case "$(uname -s)" in
	MINGW*|MSYS*|CYGWIN*) exe_name=gif320sharp.exe ;;
	*) exe_name=gif320sharp ;;
esac

cli_build_dir=${GIF320SHARP_BUILD_DIR:-"$repo_root/Gif320Sharp/bin/$configuration/$dotnet_framework"}
if [ ! -f "$cli_build_dir/$exe_name" ]; then
	echo "Missing built CLI: $cli_build_dir/$exe_name" >&2
	exit 1
fi

gegl_build_dir="$repo_root/Gif320Sharp_Gegl/build"
if [ "${SKIP_GEGL_BUILD:-0}" != "1" ]; then
	meson_cmd=${MESON:-meson}
	if [ -n "$gimp_root" ] && [ -d "$gimp_root/lib/pkgconfig" ] && [ -z "${PKG_CONFIG_PATH:-}" ]; then
		export PKG_CONFIG_PATH="$gimp_root/lib/pkgconfig"
	fi
	if [ -n "$gimp_root" ] && [ -d "$gimp_root/bin" ]; then
		PATH="$PATH:$gimp_root/bin"
		export PATH
	fi
	if [ ! -f "$gegl_build_dir/build.ninja" ]; then
		echo "Configuring GEGL modules..."
		"$meson_cmd" setup "$gegl_build_dir" "$repo_root/Gif320Sharp_Gegl"
	fi
	echo "Building GEGL modules..."
	"$meson_cmd" compile -C "$gegl_build_dir"
fi

if [ -n "${GIMP_PLUGIN_DIR:-}" ]; then
	plugin_dir=$GIMP_PLUGIN_DIR
else
	case "$(uname -s)" in
		Darwin*) plugin_dir="$HOME/Library/Application Support/GIMP/$gimp_version/plug-ins/gif320sharp_export" ;;
		*) plugin_dir="$HOME/.config/GIMP/$gimp_version/plug-ins/gif320sharp_export" ;;
	esac
fi

plugin_bin_dir="$plugin_dir/bin"
mkdir -p "$plugin_bin_dir"

echo "Installing Python plug-in..."
cp "$repo_root/Gif320Sharp_Gimp/gif320sharp_export.py" "$plugin_dir/gif320sharp_export.py"
chmod +x "$plugin_dir/gif320sharp_export.py"

echo "Installing bundled CLI..."
for file in "$exe_name" gif320sharp.dll Gif320Sharp_Core.dll gif320sharp.deps.json gif320sharp.runtimeconfig.json; do
	if [ ! -f "$cli_build_dir/$file" ]; then
		echo "Missing runtime file: $cli_build_dir/$file" >&2
		exit 1
	fi
	cp "$cli_build_dir/$file" "$plugin_bin_dir/$file"
done

preview_module=$(find "$gegl_build_dir" -maxdepth 1 -type f \( -name '*vt320-preview*.dll' -o -name '*vt320-preview*.so' -o -name '*vt320-preview*.dylib' \) | sed -n '1p')
second_pass_module=$(find "$gegl_build_dir" -maxdepth 1 -type f \( -name '*vt320-second-pass*.dll' -o -name '*vt320-second-pass*.so' -o -name '*vt320-second-pass*.dylib' \) | sed -n '1p')
if [ -z "$preview_module" ] || [ -z "$second_pass_module" ]; then
	echo "Missing built GEGL modules in $gegl_build_dir" >&2
	exit 1
fi

if [ -n "${GEGL_PLUGIN_DIR:-}" ]; then
	gegl_plugin_dir=$GEGL_PLUGIN_DIR
else
	case "$(uname -s)" in
		Darwin*) gegl_plugin_dir="$HOME/Library/Application Support/gegl-0.4/plug-ins" ;;
		*) gegl_plugin_dir="$HOME/.local/share/gegl-0.4/plug-ins" ;;
	esac
fi

mkdir -p "$gegl_plugin_dir"
echo "Installing GEGL modules to:"
echo "  $gegl_plugin_dir"
rm -f "$gegl_plugin_dir"/libgif320sharp-vt320-preview.* "$gegl_plugin_dir"/libgif320sharp-vt320-second-pass.*
cp "$preview_module" "$gegl_plugin_dir/$(basename "$preview_module")"
cp "$second_pass_module" "$gegl_plugin_dir/$(basename "$second_pass_module")"

echo
echo "Installed Gif320Sharp GIMP plug-in."
echo "Restart GIMP to load the updated plug-in and GEGL modules."
