# Gif320Sharp
Managed C# port of the GIF320 VT320 GIF renderer, with a reusable core
library and CLI.

`Gif320Sharp_Core` contains the GIF decoder, original-style image conversion
options, VT320 DRCS renderer, and optimization helpers. `Gif320Sharp` is the
CLI project and keeps the original entry points. Pipe mode uses a dedicated
legacy renderer so its byte stream can be compared against the C `gif320 -p`
output:

```text
gif320 -p < image.gif
gif320 image.gif
```

The managed CLI also accepts automation flags such as `--output`,
`--cells`, `--full-screen`, `--threshold`, `--balance`, and `--no-auto`.
In interactive mode, the `mode` command toggles between the original GIF320
16x6 boxed sketch preview and an 80x24 full-screen preview. `mode old` and
`mode 80x24` select those previews explicitly.

Interactive mode runs one advanced automatic tone-tuning pass at startup by
default on modern terminal emulators. The `advanced`, `auto`, or `tune`
commands rerun that scored tuning pass for the current zoom/crop; pass `old`,
`80x24`, or `current` as an optional target. Manual `threshold` or `balance`
commands clear the tuned tone profile and return to explicit GIF320-style
settings.

The interactive `optimize` command mirrors GIF320's original meaning: it
chooses or applies output cell dimensions for the current zoom, ratio,
thresholds, balance, and glyph budget, then asks whether to save that rendered
VT sequence. It is not the automatic image-parameter tuning pass described
below. Automatic tuning is used by the managed rendering API and
non-interactive CLI output unless disabled with `--no-auto`.

On terminals detected as modern emulators (`xterm`, `screen`, `tmux`,
Windows Terminal, VTE/Konsole-style terminals, and similar), state-changing
interactive commands redraw immediately. If the terminal does not look modern,
ordinary state changes are deferred until Enter to avoid excessive work on
real hardware. `--interactive-compat` disables the startup advanced tuning pass
and is intended for deterministic tests or old-style interactive behavior.

In the modern interactive UI, the command prompt is not focused by default.
Single-key hotkeys such as `z`, `x`, `h`, `j`, `k`, `l`, `f`, `a`, `o`, `m`,
`?`, and `q` run as soon as the key is pressed. Press `Esc` to focus the
original text command prompt for commands that need values or file names, such
as `threshold`, `balance`, `ratio`, `save`, or `double`. Threshold and balance
are also exposed as horizontal sliders: click the left/right arrows for one-step
changes, or drag the bar marker on terminals that report xterm SGR mouse input.
Manual slider changes clear the tuned tone profile and return to explicit
threshold/balance settings.

## Library API

Use `Gif320Converter` for GIF files or streams:

```csharp
var converter = new Gif320Converter();
Gif320RenderResult result = converter.RenderGifFile(
	"image.gif",
	new Gif320ConversionOptions { FullScreenDouble = true }
);

File.WriteAllText("image.vt320", result.VtSequence, Encoding.ASCII);
```

Use `Gif320Renderer` directly for raw RGB/RGBA/BGRA pixel buffers:

```csharp
var renderer = new Gif320Renderer();
Gif320RenderResult result = renderer.RenderRgb(
	rgbPixels,
	width,
	height,
	Gif320RenderOptions.FullScreenDouble()
);
```

`Gif320RenderOptions.FullScreenDouble()` renders a 40x12 logical-cell image
using VT320 double-width/double-height line attributes, filling the 80x24
screen.

By default the renderer auto-tunes luminance balance, gamma, contrast,
thresholds, local contrast, and dithering. Candidate settings are scored with
a structural-similarity-style tone score, source/output edge correlation, tone
error after low-pass filtering, and a penalty for glyph-budget pressure. When
an image has more distinct 15x12 cells than the DRCS budget allows, cells are
reduced with binary vector quantization using farthest-first initialization and
Lloyd-style majority-bit centroid refinement.

## Image Processing Pipeline

GIF input is decoded in managed code. The decoder reads GIF87a/GIF89a headers,
global or local color tables, graphic-control transparency, interlaced row
ordering, and GIF LZW raster data. The converter currently renders the first
image descriptor, matching the original GIF320 viewer's still-image workflow.

Rendering targets the VT320 soft-font model: each logical screen cell is a
15x12 bitmap encoded as two rows of sixels separated by `/`. The screen is
then printed as DRCS character references. Normal output can use up to 80x24
logical cells; full-screen double-size output renders 40x12 logical cells and
prints every row with VT320 double-width/double-height line controls so the
terminal fills the 80x24 display.

For original-style rendering, RGB pixels are collapsed to monochrome using the
configured color balance. The default balance and thresholds mirror GIF320's
interactive controls: red/green/blue balance, a full-intensity threshold, and
a half-intensity threshold. Checkerboard half-tone dithering emulates the
original false-gray behavior by turning on alternating pixels for values above
the half threshold and below the full threshold. The managed renderer also
offers Floyd-Steinberg error diffusion for smoother tonal preservation when
exact GIF320 compatibility is not the goal.

The packer groups identical 15x12 cells so repeated glyphs consume one DRCS
slot. Black, white, and duplicate cells naturally collapse this way, which is
the core trick that lets simple images grow larger than the nominal soft-font
budget. `Gif320Converter` can probe candidate dimensions and choose a larger
render size that still fits the configured glyph budget.

Automatic tuning is separate from GIF320 compatibility mode. It searches a
small grid of luminance balances, gamma, contrast, brightness, thresholding
choices, local contrast, and dithering modes. Each candidate is rendered,
reconstructed from the actual glyph map, and scored by:

- structural-similarity-style agreement between source luminance and the
  low-pass-filtered monochrome output;
- Sobel edge correlation, so important source edges are preserved without
  rewarding random high-frequency noise by itself;
- tone error after low-pass filtering, which approximates how black/white
  dither integrates visually;
- glyph-budget pressure and reduction error, so settings that only look good
  before DRCS packing are penalized.

When a candidate still needs more distinct glyphs than the budget allows, the
renderer treats each 15x12 cell as a 180-dimensional binary vector. It seeds a
codebook with farthest-first traversal, then applies Lloyd-style refinement:
cells are assigned to the nearest codebook glyph by Hamming distance, and each
codebook glyph is rebuilt as the weighted majority bit pattern of its assigned
cells. This is a vector-quantization approach to the "merge similar glyphs"
problem; pairwise nearest-glyph merging is only a special, greedier form of the
same idea and tends to make poorer global choices.

The legacy compatibility renderer is intentionally narrower. It mirrors
GIF320's pipe-mode optimizer: 96 DRCS slots starting at space, bottom-right to
top-left packing, black/white cell reuse, integer weighted grayscale, inclusive
box filtering, and the original VT soft-font escape sequence layout. The newer
renderer is used for managed-only options such as `--full-screen`, `--cells`,
`--double`, and glyph vector quantization.

## Compatibility Test

`Gif320Sharp_Test` includes a CLI compatibility test for
`ExampleImages/jimm.gif`. The test compares original C `gif320 -p` output to
managed `Gif320Sharp -p` output with the original default rendering
parameters (`--no-auto --threshold 50 25 --balance 30 40 10 --ratio 0.8`).

The test is intentionally inconclusive unless the original C executable exists
at `gif320/gif320`, `gif320/gif320.exe`, or the Visual Studio output path
`gif320/bin/<platform>/<configuration>/gif320.exe`. On Windows, the test will
also try to build `gif320/gif320.vcxproj` with MSBuild before comparing output.
On Unix-like systems, including WSL, it will try to build `gif320/gif320`
directly with gcc.
