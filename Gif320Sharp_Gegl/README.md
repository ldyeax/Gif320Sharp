# Gif320Sharp GEGL operations

This is a native GEGL operation module set for GIMP/GEGL hosts.

It builds two operation modules:

- `gif320:vt320-preview`: converts the input layer to a raster preview of the
  Gif320Sharp/VT320 character-cell output. It renders against black with a
  configurable phosphor tint, defaults to amber `(255,191,0)`, supports crop
  controls, fixed or auto character dimensions, tone/dither/glyph-budget
  controls, and optional VT320 second-pass styling. Auto orientation uses 80
  columns for landscape input, 24 rows for portrait input, and derives square
  input from 24 displayed rows using the VT320 4:11 displayed character aspect,
  fitting the VT320 raster back into the layer bounds.
- `gif320:vt320-second-pass`: applies the VT320 second-pass phosphor/scanline
  styling to an existing layer. When both character dimensions are `0`, it uses
  the same rendered-aspect auto-orientation default.

The preview operation is implemented as a plain GEGL filter so GIMP can attach
it non-destructively to a layer. It only uses the selected layer input; GIMP
rejects non-destructive layer filters that expose an auxiliary `aux` pad.
The operations include guardrails for live preview: oversized character grids,
terminal sample counts, or output rasters are rejected with a warning instead of
attempting an intractable render.

Both operations cache one full fitted layer render per GEGL evaluation and copy
subsequent tile requests from that buffer. Set `GIF320_GEGL_LOG=1` to log cache
miss/render timings, or `GIF320_GEGL_LOG=verbose` to include cache hits.

The preview operation collects cell patterns before spending the DRCS glyph
budget, then chooses prototypes globally by worst represented fit instead of
using the first distinct cells encountered in scan order. This makes reduced
previews favor more even image-wide fidelity when the glyph budget is tight.

## Build

Install GEGL development headers, then from this directory:

```sh
meson setup build
meson compile -C build
meson install -C build
```

For local testing without installing:

```sh
GEGL_PATH="$PWD/build" gegl -o out.png input.png -- gif320:vt320-preview
```

Windows builds need a GEGL/GIMP SDK whose `pkg-config` files expose `gegl-0.4`,
`babl`, and `gmodule-2.0`.

The module is pure native C and does not load the .NET renderer. Use Meson cross
files or native builders for each target platform. In practice, Windows builds
should use the GIMP/GEGL SDK toolchain, Linux builds should use distro GEGL
development packages, and macOS builds should run on macOS so the result can be
signed with the rest of the GIMP plug-in bundle.

## Exact byte export

GEGL operations cannot open a save dialog or use the system clipboard. The
companion GIMP Python plug-in in `../Gif320Sharp_Gimp` provides those commands
by invoking a bundled Native AOT `gif320sharp` executable with raw RGBA pixels.
That path produces the same bytes as saving from the Gif320Sharp CLI.

## Shader sharing

The Unity shader cannot be loaded directly by GEGL because it is a Unity ShaderLab
file with multiple HLSL passes and Unity-specific includes. The reusable
terminal-pixel math has been mirrored into `shaders/vt320_effect_core.glsl` for
future GPU/OpenCL work, while the current GEGL module uses the same math in a CPU
path for compatibility with standard GEGL operation loading.
