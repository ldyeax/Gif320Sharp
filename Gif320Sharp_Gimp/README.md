# Gif320Sharp GIMP export plug-in

This folder contains a GIMP 3 Python plug-in that delegates byte generation to
the `gif320sharp` executable. Use a Native AOT build beside the plug-in to avoid
requiring a .NET runtime for GIMP users.

The plug-in adds:

- `Filters/Gif320Sharp/Add VT320 Preview Filter...`
- `Filters/Gif320Sharp/Edit VT320 Preview Filter...`
- `Filters/Gif320Sharp/Add VT320 Second Pass Filter...`

The filter commands append non-destructive GIMP 3 layer filters backed by the
native GEGL operations. The default size mode uses 80 columns for landscape
input, 24 rows for portrait input, and derives square input from 24 displayed
rows using the VT320 4:11 displayed character aspect. In
interactive mode, the preview filter resets stale GIMP procedure values to those
safe auto defaults before it attaches the temporary live filter, and the VT320
second-pass checkbox starts unchecked. The plug-in warns and refuses settings
that are too large for a live layer-filter preview. In interactive mode, the
filter dialogs temporarily attach the non-destructive filter while the dialog is
open, so the image display shows the filtered layer composited with the rest of
the image; Cancel removes the temporary filter and OK keeps it. The VT320
preview filter dialog also has Save and Copy buttons that export using the
dialog's current filter parameters. Save writes the exact VT escape sequence
bytes, and Copy writes ASCII hex pairs such as `01 23 45 ab cd ef` to the
system clipboard. The edit command opens the selected layer's existing
Gif320Sharp preview filter config with draggable slider controls and the same
Save/Copy actions, so export uses the parameters currently stored on that layer
filter. The export section can write normal output, VT320 double-width/
double-height output, or full-screen 40x12 double-size output. Aspect-derived
row/column counts use the VT320 4:11 displayed character aspect so the GIMP
preview has roughly the same proportions as the real VT320 output.

Set `GIF320_GIMP_LOG=1` before launching GIMP to log the interactive defaults
chosen by the Python plug-in.

## Install

1. Publish or copy the platform-native `gif320sharp` executable.
2. Copy `gif320sharp_export.py` into a GIMP plug-ins folder, normally:
   - Windows: `%APPDATA%\GIMP\3.0\plug-ins\gif320sharp_export\`
   - Linux: `~/.config/GIMP/3.0/plug-ins/gif320sharp_export/`
   - macOS: `~/Library/Application Support/GIMP/3.0/plug-ins/gif320sharp_export/`
3. Put the executable in a `bin/` folder beside the plug-in script, put it on
   `PATH`, or set `GIF320SHARP` to its full path.
4. On Linux/macOS, mark the script executable:

```sh
chmod +x gif320sharp_export.py
```

The GEGL layer filters are separate native modules in `Gif320Sharp_Gegl`; this
plug-in owns the filter-add dialogs and the preview dialog's Save/Copy actions
because GEGL operations cannot open save dialogs or access the clipboard.
