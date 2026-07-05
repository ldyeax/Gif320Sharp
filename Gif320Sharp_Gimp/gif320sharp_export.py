#!/usr/bin/env python3
import os
import shutil
import subprocess
import sys
from pathlib import Path

import gi

gi.require_version("Gegl", "0.4")
gi.require_version("Gdk", "3.0")
gi.require_version("Gimp", "3.0")
gi.require_version("GimpUi", "3.0")
gi.require_version("Gtk", "3.0")

from gi.repository import Gegl, Gdk, Gimp, GimpUi, GLib, GObject, Gtk


PREVIEW_FILTER_PROC = "python-fu-gif320sharp-add-vt320-preview-filter"
SECOND_PASS_FILTER_PROC = "python-fu-gif320sharp-add-vt320-second-pass-filter"
EDIT_PREVIEW_FILTER_PROC = "python-fu-gif320sharp-edit-vt320-preview-filter"

CELL_PIXEL_WIDTH = 15
CELL_PIXEL_HEIGHT = 12
DISPLAY_CELL_ASPECT = 4.0 / 11.0
ATLAS_PREFIX = "gif320-atlas-v1:"
CELL_MAP_PREFIX = "gif320-map-v1:"
ATLAS_GLYPH_BYTES = (CELL_PIXEL_WIDTH * CELL_PIXEL_HEIGHT + 7) // 8
ATLAS_MAX_GLYPHS = 94
PREVIEW_MAX_CELLS = 12000
PREVIEW_MAX_TERMINAL_PIXELS = 2500000
PREVIEW_MAX_OUTPUT_PIXELS = 12000000
SECOND_PASS_MAX_CELLS = 500000
EXPORT_SIZE_NORMAL = 0
EXPORT_SIZE_DOUBLE = 1
EXPORT_SIZE_FULL_SCREEN_DOUBLE = 2

PREVIEW_FILTER_PROPERTIES = [
    "crop-x", "crop-y", "crop-width", "crop-height",
    "size-mode", "cells-x", "cells-y", "output-scale",
    "resize-mode", "dither-mode", "auto-tune",
    "allow-glyph-reduction", "max-glyphs",
    "red-balance", "green-balance", "blue-balance",
    "full-threshold", "half-threshold",
    "lock-red-balance", "lock-green-balance", "lock-blue-balance",
    "lock-full-threshold", "lock-half-threshold",
    "tune-frequency", "tune-smoothness", "tune-glyph-reuse",
    "reverse-video-tolerance", "manual-atlas", "manual-cell-map",
    "tint-red", "tint-green", "tint-blue",
    "second-pass", "scanline-gap", "pixel-roundness",
    "roundness-aspect", "hide-single-pixel", "glow",
]

SECOND_PASS_FILTER_PROPERTIES = [
    "terminal-width", "terminal-height",
    "tint-red", "tint-green", "tint-blue",
    "scanline-gap", "pixel-roundness", "roundness-aspect",
    "hide-single-pixel", "glow",
]


def _default_executable():
    override = os.environ.get("GIF320SHARP")
    if override:
        return override

    script_dir = Path(__file__).resolve().parent
    executable_name = "gif320sharp.exe" if os.name == "nt" else "gif320sharp"
    candidates = [
        script_dir / "bin" / executable_name,
        script_dir / executable_name,
    ]
    for candidate in candidates:
        if candidate.exists():
            return str(candidate)

    found = shutil.which(executable_name)
    return found if found else executable_name


def _config_value(config, name, fallback):
    try:
        value = config.get_property(name)
        return fallback if value is None else value
    except Exception:
        return fallback


def _clamp(value, minimum, maximum):
    return max(minimum, min(maximum, value))


def _percent_config_value(config, name, fallback):
    value = float(_config_value(config, name, fallback))
    if 0.0 <= value <= 1.0:
        value *= 100.0
    return int(round(_clamp(value, 0.0, 100.0)))


def _log(message):
    enabled = os.environ.get("GIF320_GIMP_LOG", "")
    if enabled and enabled != "0":
        print("Gif320Sharp GIMP: " + message, file=sys.stderr, flush=True)


def _derive_rows_from_columns(columns, width, height, max_rows):
    rows = round(columns * DISPLAY_CELL_ASPECT * max(height, 1) / max(width, 1))
    return _clamp(rows, 1, max_rows)


def _derive_columns_from_rows(rows, width, height, max_columns):
    columns = round(rows * max(width, 1) / (max(height, 1) * DISPLAY_CELL_ASPECT))
    return _clamp(columns, 1, max_columns)


def _resolve_cells(width, height, size_mode, cells_x, cells_y, max_x=240, max_y=120):
    cells_x = _clamp(cells_x if cells_x > 0 else 80, 1, max_x)
    cells_y = _clamp(cells_y if cells_y > 0 else 24, 1, max_y)
    if size_mode == 3:
        if height == width:
            cells_x = _derive_columns_from_rows(cells_y, width, height, max_x)
        elif height > width:
            cells_x = _derive_columns_from_rows(cells_y, width, height, max_x)
        else:
            cells_y = _derive_rows_from_columns(cells_x, width, height, max_y)
    elif size_mode == 1:
        cells_y = _derive_rows_from_columns(cells_x, width, height, max_y)
    elif size_mode == 2:
        cells_x = _derive_columns_from_rows(cells_y, width, height, max_x)
    return cells_x, cells_y


def _resolve_second_pass_cells(width, height, cells_x, cells_y):
    if cells_x > 0 and cells_y > 0:
        return _clamp(cells_x, 1, 4096), _clamp(cells_y, 1, 4096)
    if cells_x > 0:
        cells_x = _clamp(cells_x, 1, 4096)
        return cells_x, _derive_rows_from_columns(cells_x, width, height, 4096)
    if cells_y > 0:
        cells_y = _clamp(cells_y, 1, 4096)
        return _derive_columns_from_rows(cells_y, width, height, 4096), cells_y
    if height == width:
        return _derive_columns_from_rows(24, width, height, 4096), 24
    if height > width:
        return _derive_columns_from_rows(24, width, height, 4096), 24
    return 80, _derive_rows_from_columns(80, width, height, 4096)


def _default_auto_cells(width, height):
    if width <= 0 or height <= 0:
        return 80, 24
    if height == width:
        return _derive_columns_from_rows(24, width, height, 240), 24
    if height > width:
        return _derive_columns_from_rows(24, width, height, 240), 24
    return 80, _derive_rows_from_columns(80, width, height, 120)


def _set_config_property(config, name, value):
    try:
        config.set_property(name, value)
        return True
    except Exception:
        return False


def _apply_preview_interactive_defaults(drawables, config):
    width, height = _source_size(drawables[0], config)
    cells_x, cells_y = _default_auto_cells(width, height)
    _set_config_property(config, "size-mode", 3)
    _set_config_property(config, "cells-x", cells_x)
    _set_config_property(config, "cells-y", cells_y)
    _set_config_property(config, "second-pass", False)
    _set_config_property(config, "live-preview", True)
    _log(
        "preview defaults: "
        f"source={width}x{height} cells={cells_x}x{cells_y} second-pass=false"
    )


def _apply_second_pass_interactive_defaults(config):
    _set_config_property(config, "terminal-width", 0)
    _set_config_property(config, "terminal-height", 0)
    _set_config_property(config, "live-preview", True)


def _source_size(drawable, config):
    buffer = drawable.get_buffer()
    extent = buffer.get_extent()
    crop_x = int(_config_value(config, "crop-x", 0))
    crop_y = int(_config_value(config, "crop-y", 0))
    crop_width = int(_config_value(config, "crop-width", 0))
    crop_height = int(_config_value(config, "crop-height", 0))
    x = extent.x + max(0, crop_x)
    y = extent.y + max(0, crop_y)
    width = crop_width if crop_width > 0 else extent.width - max(0, crop_x)
    height = crop_height if crop_height > 0 else extent.height - max(0, crop_y)
    right = min(x + width, extent.x + extent.width)
    bottom = min(y + height, extent.y + extent.height)
    return max(0, right - x), max(0, bottom - y)


def _resolved_grid_text(drawable, config):
    width, height = _source_size(drawable, config)
    size_mode = int(_config_value(config, "size-mode", 3))
    requested_x = int(_config_value(config, "cells-x", 80))
    requested_y = int(_config_value(config, "cells-y", 24))
    cells_x, cells_y = _resolve_cells(width, height, size_mode, requested_x, requested_y)
    if size_mode == 0:
        relation = "Fixed mode uses both character values."
    elif size_mode == 1:
        relation = "Rows are derived from columns for the current crop."
    elif size_mode == 2:
        relation = "Columns are derived from rows for the current crop."
    elif height >= width:
        relation = "Auto orientation derives columns from rows for this crop."
    else:
        relation = "Auto orientation derives rows from columns for this crop."
    return f"Resolved grid: {cells_x} columns x {cells_y} rows. {relation}"


def _preview_cost_warning(drawables, config):
    for drawable in drawables:
        width, height = _source_size(drawable, config)
        if width <= 0 or height <= 0:
            return "The selected Gif320Sharp crop rectangle is empty."
        cells_x, cells_y = _resolve_cells(
            width,
            height,
            int(_config_value(config, "size-mode", 3)),
            int(_config_value(config, "cells-x", 80)),
            int(_config_value(config, "cells-y", 24)),
        )
        cell_count = cells_x * cells_y
        terminal_pixels = cell_count * CELL_PIXEL_WIDTH * CELL_PIXEL_HEIGHT
        output_pixels = width * height
        if (
            cell_count > PREVIEW_MAX_CELLS
            or terminal_pixels > PREVIEW_MAX_TERMINAL_PIXELS
            or output_pixels > PREVIEW_MAX_OUTPUT_PIXELS
        ):
            return (
                "Gif320Sharp preview was not added because the requested "
                f"{cells_x} x {cells_y} characters on a {width} x {height} layer "
                "is too intensive for a live GIMP layer filter. Reduce character "
                "rows/columns or the layer size."
            )
    return None


def _second_pass_cost_warning(drawables, config):
    for drawable in drawables:
        extent = drawable.get_buffer().get_extent()
        cells_x, cells_y = _resolve_second_pass_cells(
            extent.width,
            extent.height,
            int(_config_value(config, "terminal-width", 0)),
            int(_config_value(config, "terminal-height", 0)),
        )
        if cells_x * cells_y > SECOND_PASS_MAX_CELLS:
            return (
                "Gif320Sharp second pass was not added because the requested "
                f"{cells_x} x {cells_y} character grid is too intensive for "
                "a live GIMP layer filter. Reduce character rows/columns."
            )
    return None


def _extract_rgba(drawable, crop_x, crop_y, crop_width, crop_height):
    buffer = drawable.get_buffer()
    extent = buffer.get_extent()
    x = extent.x + max(0, crop_x)
    y = extent.y + max(0, crop_y)
    width = crop_width if crop_width > 0 else extent.width - max(0, crop_x)
    height = crop_height if crop_height > 0 else extent.height - max(0, crop_y)
    right = min(x + width, extent.x + extent.width)
    bottom = min(y + height, extent.y + extent.height)
    width = max(0, right - x)
    height = max(0, bottom - y)
    if width <= 0 or height <= 0:
        raise RuntimeError("The selected crop rectangle is empty.")

    rect = Gegl.Rectangle()
    rect.x = x
    rect.y = y
    rect.width = width
    rect.height = height
    pixels = bytes(buffer.get(rect, 1.0, "R'G'B'A u8", Gegl.AbyssPolicy.NONE))
    expected_length = width * height * 4
    if len(pixels) != expected_length:
        raise RuntimeError(
            "GIMP returned "
            f"{len(pixels)} bytes for a {width} x {height} RGBA export; "
            f"expected {expected_length}."
        )
    return pixels, width, height


def _item_position_path(image, item):
    path = []
    current = item
    while current is not None:
        path.insert(0, image.get_item_position(current))
        current = current.get_parent()
    return path


def _item_at_position_path(image, path):
    siblings = list(image.get_layers())
    item = None
    for index, position in enumerate(path):
        if position < 0 or position >= len(siblings):
            raise RuntimeError("Could not find the duplicated layer for export.")
        item = siblings[position]
        if index < len(path) - 1:
            siblings = list(item.get_children())
    return item


def _delete_preview_and_filters_above(drawable, preview_filter_index=None):
    try:
        filters = list(drawable.get_filters())
    except Exception:
        return

    preview_index = preview_filter_index if preview_filter_index is not None else -1
    if preview_index < 0 or preview_index >= len(filters):
        preview_index = -1
        for index, drawable_filter in enumerate(filters):
            try:
                if drawable_filter.get_operation_name() == "gif320:vt320-preview":
                    preview_index = index
                    break
            except Exception:
                pass

    if preview_index < 0:
        return

    for drawable_filter in filters[:preview_index + 1]:
        try:
            if drawable_filter.is_valid():
                drawable_filter.delete()
        except Exception:
            pass


def _extract_rendered_input_rgba(
    image,
    drawable,
    crop_x,
    crop_y,
    crop_width,
    crop_height,
    preview_filter_index=None,
):
    path = _item_position_path(image, drawable)
    duplicate = image.duplicate()
    try:
        duplicate_drawable = _item_at_position_path(duplicate, path)
        _delete_preview_and_filters_above(duplicate_drawable, preview_filter_index)
        try:
            remaining_filters = list(duplicate_drawable.get_filters())
        except Exception:
            remaining_filters = []
        if remaining_filters and not duplicate_drawable.merge_filters():
            raise RuntimeError("Could not render the filters below Gif320Sharp for export.")
        return _extract_rgba(duplicate_drawable, crop_x, crop_y, crop_width, crop_height)
    finally:
        duplicate.delete()


def _build_command(
    executable,
    width,
    height,
    config,
    hex_output,
    export_size,
    include_manual_atlas=True,
):
    size_mode = int(_config_value(config, "size-mode", 3))
    cells_x = int(_config_value(config, "cells-x", 80))
    cells_y = int(_config_value(config, "cells-y", 24))
    dither = int(_config_value(config, "dither-mode", 1))
    resize = int(_config_value(config, "resize-mode", 2))
    dither_names = ["threshold", "checkerboard", "floyd-steinberg"]
    resize_names = ["stretch", "contain", "cover"]

    command = [
        executable,
        "--raw-rgba",
        str(width),
        str(height),
    ]
    if export_size == EXPORT_SIZE_FULL_SCREEN_DOUBLE:
        command.append("--full-screen")
    elif export_size == EXPORT_SIZE_DOUBLE:
        command.append("--double")

    if export_size == EXPORT_SIZE_FULL_SCREEN_DOUBLE:
        pass
    elif size_mode == 1:
        command += ["--cells-width", str(cells_x)]
    elif size_mode == 2:
        command += ["--cells-height", str(cells_y)]
    elif size_mode == 3:
        if height >= width:
            command += ["--cells-height", str(cells_y)]
        else:
            command += ["--cells-width", str(cells_x)]
    else:
        command += ["--cells", str(cells_x), str(cells_y)]

    command += [
        "--max-glyphs",
        str(int(_config_value(config, "max-glyphs", 94))),
        "--dither",
        dither_names[max(0, min(dither, len(dither_names) - 1))],
        "--resize",
        resize_names[max(0, min(resize, len(resize_names) - 1))],
        "--threshold",
        str(_percent_config_value(config, "full-threshold", 50)),
        str(_percent_config_value(config, "half-threshold", 25)),
        "--balance",
        str(int(_config_value(config, "red-balance", 30))),
        str(int(_config_value(config, "green-balance", 40))),
        str(int(_config_value(config, "blue-balance", 10))),
        "--tune-frequency",
        str(int(_config_value(config, "tune-frequency", 0))),
        "--tune-smoothness",
        str(int(_config_value(config, "tune-smoothness", 0))),
        "--tune-glyph-reuse",
        str(int(_config_value(config, "tune-glyph-reuse", 0))),
        "--invert-tolerance",
        str(int(_config_value(config, "reverse-video-tolerance", 4))),
    ]

    if bool(_config_value(config, "auto-tune", True)):
        command.append("--auto-tune")
    else:
        command.append("--no-auto")
    if not bool(_config_value(config, "allow-glyph-reduction", True)):
        command.append("--no-reduce")
    manual_atlas = str(_config_value(config, "manual-atlas", "") or "").strip()
    manual_cell_map = str(_config_value(config, "manual-cell-map", "") or "").strip()
    if include_manual_atlas and manual_atlas and manual_cell_map:
        command += ["--atlas", manual_atlas]
        command += ["--cell-map", manual_cell_map]
    if bool(_config_value(config, "lock-red-balance", False)):
        command.append("--lock-red-balance")
    if bool(_config_value(config, "lock-green-balance", False)):
        command.append("--lock-green-balance")
    if bool(_config_value(config, "lock-blue-balance", False)):
        command.append("--lock-blue-balance")
    if bool(_config_value(config, "lock-full-threshold", False)):
        command.append("--lock-full-threshold")
    if bool(_config_value(config, "lock-half-threshold", False)):
        command.append("--lock-half-threshold")
    if hex_output:
        command.append("--hex")

    return command


def _run_gif320sharp(
    config,
    image,
    drawable,
    preview_filter_index,
    hex_output,
    export_size=EXPORT_SIZE_NORMAL,
):
    executable = str(_config_value(config, "executable", _default_executable()))
    crop_x = int(_config_value(config, "crop-x", 0))
    crop_y = int(_config_value(config, "crop-y", 0))
    crop_width = int(_config_value(config, "crop-width", 0))
    crop_height = int(_config_value(config, "crop-height", 0))
    pixels, width, height = _extract_rendered_input_rgba(
        image,
        drawable,
        crop_x,
        crop_y,
        crop_width,
        crop_height,
        preview_filter_index,
    )
    command = _build_command(executable, width, height, config, hex_output, export_size)
    completed = subprocess.run(
        command,
        input=pixels,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if completed.returncode != 0:
        stderr = completed.stderr.decode("utf-8", "replace").strip()
        raise RuntimeError(stderr or "gif320sharp failed.")

    return completed.stdout


def _materialize_gif320sharp_atlas_state(config, image, drawable, preview_filter_index):
    executable = str(_config_value(config, "executable", _default_executable()))
    crop_x = int(_config_value(config, "crop-x", 0))
    crop_y = int(_config_value(config, "crop-y", 0))
    crop_width = int(_config_value(config, "crop-width", 0))
    crop_height = int(_config_value(config, "crop-height", 0))
    pixels, width, height = _extract_rendered_input_rgba(
        image,
        drawable,
        crop_x,
        crop_y,
        crop_width,
        crop_height,
        preview_filter_index,
    )
    command = _build_command(
        executable,
        width,
        height,
        config,
        hex_output=False,
        export_size=EXPORT_SIZE_NORMAL,
        include_manual_atlas=False,
    )
    command.append("--atlas-state-only")
    completed = subprocess.run(
        command,
        input=pixels,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if completed.returncode != 0:
        stderr = completed.stderr.decode("utf-8", "replace").strip()
        raise RuntimeError(stderr or "gif320sharp failed.")

    lines = completed.stdout.decode("ascii", "replace").splitlines()
    atlas = lines[0].strip() if len(lines) > 0 else ""
    cell_map = lines[1].strip() if len(lines) > 1 else ""
    if not atlas.startswith(ATLAS_PREFIX) or not cell_map.startswith(CELL_MAP_PREFIX):
        raise RuntimeError("gif320sharp returned an invalid atlas state.")
    return atlas, cell_map


def _choose_save_path(parent=None):
    dialog = Gtk.FileChooserNative.new(
        "Save Gif320Sharp VT320 bytes",
        parent,
        Gtk.FileChooserAction.SAVE,
        "_Save",
        "_Cancel",
    )
    dialog.set_do_overwrite_confirmation(True)
    dialog.set_current_name("output.vt320")
    response = dialog.run()
    path = None
    if response == Gtk.ResponseType.ACCEPT:
        selected = dialog.get_file()
        path = selected.get_path() if selected is not None else None
    dialog.destroy()
    return path


def _copy_text(text):
    display = Gdk.Display.get_default()
    if display is not None and hasattr(display, "get_clipboard"):
        display.get_clipboard().set(text)
        return

    clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
    clipboard.set_text(text, -1)
    clipboard.store()


def _message(text):
    try:
        Gimp.message(text)
    except Exception:
        print(text, file=sys.stderr)


def _return_error(procedure, status, message):
    _message(message)
    return procedure.new_return_values(status, GLib.Error(message))


def _parse_atlas(text):
    glyphs = []
    if not text:
        return glyphs

    text = str(text).strip()
    if text.lower().startswith(ATLAS_PREFIX):
        text = text[len(ATLAS_PREFIX):]

    token = []
    for char in text:
        if char in "0123456789abcdefABCDEF":
            token.append(char)
        elif char in ",; \t\r\n":
            _flush_atlas_token(token, glyphs)
        else:
            raise ValueError("Manual atlas contains an unsupported character.")
    _flush_atlas_token(token, glyphs)
    return glyphs


def _flush_atlas_token(token, glyphs):
    if not token:
        return
    if len(token) != ATLAS_GLYPH_BYTES * 2:
        raise ValueError(
            f"Manual atlas glyphs must be {ATLAS_GLYPH_BYTES * 2} hex characters."
        )
    if len(glyphs) >= ATLAS_MAX_GLYPHS:
        raise ValueError(f"Manual atlas cannot contain more than {ATLAS_MAX_GLYPHS} glyphs.")
    glyphs.append(bytearray.fromhex("".join(token)))
    token.clear()


def _format_atlas(glyphs):
    if not glyphs:
        return ""
    return ATLAS_PREFIX + ",".join(bytes(glyph).hex() for glyph in glyphs)


def _parse_cell_map(text):
    if not text:
        return None
    text = str(text).strip()
    if not text.lower().startswith(CELL_MAP_PREFIX):
        return None

    dimensions_and_hex = text[len(CELL_MAP_PREFIX):]
    separator = dimensions_and_hex.find(":")
    if separator < 0:
        return None

    dimensions = dimensions_and_hex[:separator]
    hex_text = dimensions_and_hex[separator + 1:]
    x_separator = dimensions.lower().find("x")
    if x_separator < 0:
        return None

    try:
        cells_x = int(dimensions[:x_separator])
        cells_y = int(dimensions[x_separator + 1:])
        data = bytes.fromhex(hex_text)
    except ValueError:
        return None

    if cells_x <= 0 or cells_y <= 0 or len(data) != cells_x * cells_y:
        return None
    return cells_x, cells_y, data


def _manual_state_matches_grid(drawable, config):
    try:
        glyphs = _parse_atlas(_config_value(config, "manual-atlas", ""))
    except Exception:
        return False

    cell_map = _parse_cell_map(_config_value(config, "manual-cell-map", ""))
    if not glyphs or cell_map is None:
        return False

    width, height = _source_size(drawable, config)
    cells_x, cells_y = _resolve_cells(
        width,
        height,
        int(_config_value(config, "size-mode", 3)),
        int(_config_value(config, "cells-x", 80)),
        int(_config_value(config, "cells-y", 24)),
    )
    map_cells_x, map_cells_y, map_data = cell_map
    if map_cells_x != cells_x or map_cells_y != cells_y:
        return False

    max_glyph_code = max((value & 0x7f) for value in map_data) if map_data else 0
    return max_glyph_code <= len(glyphs)


def _atlas_bit(glyph, x, y):
    bit = y * CELL_PIXEL_WIDTH + x
    return (glyph[bit // 8] & (1 << (bit % 8))) != 0


def _set_atlas_bit(glyph, x, y, enabled):
    bit = y * CELL_PIXEL_WIDTH + x
    mask = 1 << (bit % 8)
    if enabled:
        glyph[bit // 8] |= mask
    else:
        glyph[bit // 8] &= 0xff ^ mask


def _drawable_filter_index(drawable, target_filter):
    if target_filter is None:
        return None
    try:
        target_id = target_filter.get_id()
        filters = list(drawable.get_filters())
    except Exception:
        return None

    for index, drawable_filter in enumerate(filters):
        try:
            if drawable_filter.get_id() == target_id:
                return index
        except Exception:
            pass
    return None


class Gif320SharpExportPlugin(Gimp.PlugIn):
    def do_set_i18n(self, procname):
        return False, None, None

    def do_query_procedures(self):
        return [
            PREVIEW_FILTER_PROC,
            EDIT_PREVIEW_FILTER_PROC,
            SECOND_PASS_FILTER_PROC,
        ]

    def do_create_procedure(self, name):
        Gegl.init(None)
        procedure = Gimp.ImageProcedure.new(
            self,
            name,
            Gimp.PDBProcType.PLUGIN,
            self.run,
            None,
        )
        procedure.set_image_types("*")
        procedure.set_sensitivity_mask(Gimp.ProcedureSensitivityMask.DRAWABLE)
        procedure.set_attribution("Gif320Sharp", "Gif320Sharp", "2026")

        if name == PREVIEW_FILTER_PROC:
            procedure.set_documentation(
                "Add a Gif320Sharp VT320 preview layer filter.",
                "Appends the gif320:vt320-preview GEGL operation to the selected drawable.",
                name,
            )
            procedure.set_menu_label("Add VT320 Preview Filter...")
            self._add_preview_filter_options(procedure)
        elif name == SECOND_PASS_FILTER_PROC:
            procedure.set_documentation(
                "Add a VT320 second-pass layer filter.",
                "Appends the gif320:vt320-second-pass GEGL operation to the selected drawable.",
                name,
            )
            procedure.set_menu_label("Add VT320 Second Pass Filter...")
            self._add_second_pass_filter_options(procedure)
        elif name == EDIT_PREVIEW_FILTER_PROC:
            procedure.set_documentation(
                "Edit an existing Gif320Sharp VT320 preview layer filter.",
                "Opens a slider-based editor for the selected layer's existing gif320:vt320-preview filter, with VT320 byte export actions.",
                name,
            )
            procedure.set_menu_label("Edit VT320 Preview Filter...")
        procedure.add_menu_path("<Image>/Filters/Gif320Sharp")

        return procedure

    def _add_preview_filter_options(self, procedure):
        flags = GObject.ParamFlags.READWRITE
        procedure.add_boolean_argument("live-preview", "Live canvas preview", "Show the non-destructive filter composited in the image display while adjusting.", True, flags)
        procedure.add_int_argument("crop-x", "Crop X", "Crop X in drawable pixels.", 0, 100000, 0, flags)
        procedure.add_int_argument("crop-y", "Crop Y", "Crop Y in drawable pixels.", 0, 100000, 0, flags)
        procedure.add_int_argument("crop-width", "Crop width", "0 means the drawable width.", 0, 100000, 0, flags)
        procedure.add_int_argument("crop-height", "Crop height", "0 means the drawable height.", 0, 100000, 0, flags)
        procedure.add_int_argument("size-mode", "Size mode", "0 fixed, 1 derive rows from columns, 2 derive columns from rows, 3 auto orientation.", 0, 3, 3, flags)
        procedure.add_int_argument("cells-x", "Character columns", "VT320 character columns.", 1, 240, 80, flags)
        procedure.add_int_argument("cells-y", "Character rows", "VT320 character rows.", 1, 120, 24, flags)
        procedure.add_int_argument("output-scale", "Output scale", "Raster pixels per VT320 terminal pixel.", 1, 12, 2, flags)
        procedure.add_int_argument("resize-mode", "Resize mode", "0 stretch, 1 contain, 2 cover.", 0, 2, 2, flags)
        procedure.add_int_argument("dither-mode", "Dither mode", "0 threshold, 1 checkerboard, 2 Floyd-Steinberg.", 0, 2, 1, flags)
        procedure.add_boolean_argument("auto-tune", "Auto tune", "Enable preview auto tuning.", True, flags)
        procedure.add_boolean_argument("allow-glyph-reduction", "Allow glyph reduction", "Reduce cells to the configured glyph budget.", True, flags)
        procedure.add_int_argument("max-glyphs", "Max glyphs", "DRCS glyph budget.", 1, 94, 94, flags)
        procedure.add_double_argument("red-balance", "Red balance", "Red weight.", 0.0, 100.0, 30.0, flags)
        procedure.add_double_argument("green-balance", "Green balance", "Green weight.", 0.0, 100.0, 40.0, flags)
        procedure.add_double_argument("blue-balance", "Blue balance", "Blue weight.", 0.0, 100.0, 10.0, flags)
        procedure.add_double_argument("full-threshold", "Full threshold", "Full threshold.", 0.0, 1.0, 0.5, flags)
        procedure.add_double_argument("half-threshold", "Half threshold", "Half threshold.", 0.0, 1.0, 0.25, flags)
        procedure.add_boolean_argument("lock-red-balance", "Lock red balance", "Keep red balance fixed when auto tuning.", False, flags)
        procedure.add_boolean_argument("lock-green-balance", "Lock green balance", "Keep green balance fixed when auto tuning.", False, flags)
        procedure.add_boolean_argument("lock-blue-balance", "Lock blue balance", "Keep blue balance fixed when auto tuning.", False, flags)
        procedure.add_boolean_argument("lock-full-threshold", "Lock full threshold", "Keep full threshold fixed when auto tuning.", False, flags)
        procedure.add_boolean_argument("lock-half-threshold", "Lock half threshold", "Keep half threshold fixed when auto tuning.", False, flags)
        procedure.add_int_argument("tune-frequency", "Tune frequency", "Prefer high frequency when positive, low frequency when negative.", -100, 100, 0, flags)
        procedure.add_int_argument("tune-smoothness", "Tune smoothness", "Prefer smooth lines when positive, inner detail when negative.", -100, 100, 0, flags)
        procedure.add_int_argument("tune-glyph-reuse", "Tune glyph reuse", "Prefer fewer glyphs when positive, fidelity when negative.", -100, 100, 0, flags)
        procedure.add_int_argument("reverse-video-tolerance", "Reverse video tolerance", "Cell pixels allowed when reusing inverted chunks.", 0, 180, 4, flags)
        procedure.add_string_argument("manual-atlas", "Manual atlas", "Resident DRCS atlas glyphs.", "", flags)
        procedure.add_string_argument("manual-cell-map", "Manual cell map", "Resident VT320 cell-to-atlas slot map.", "", flags)
        procedure.add_double_argument("tint-red", "Tint red", "Output tint red.", 0.0, 1.0, 1.0, flags)
        procedure.add_double_argument("tint-green", "Tint green", "Output tint green.", 0.0, 1.0, 191.0 / 255.0, flags)
        procedure.add_double_argument("tint-blue", "Tint blue", "Output tint blue.", 0.0, 1.0, 0.0, flags)
        procedure.add_boolean_argument("second-pass", "VT320 second pass", "Apply terminal-pixel shaping in the preview filter.", False, flags)
        procedure.add_double_argument("scanline-gap", "Scanline gap", "Darken vertical gaps between scanlines.", 0.0, 1.0, 0.15, flags)
        procedure.add_double_argument("pixel-roundness", "Pixel roundness", "Round terminal-pixel edges.", 0.0, 2.0, 0.85, flags)
        procedure.add_double_argument("roundness-aspect", "Roundness aspect", "Horizontal to vertical roundness aspect.", 0.1, 10.0, 0.8, flags)
        procedure.add_boolean_argument("hide-single-pixel", "Hide isolated pixels", "Hide isolated single terminal pixels.", True, flags)
        procedure.add_double_argument("glow", "Glow", "Small neighbor glow amount.", 0.0, 1.0, 0.0, flags)

    def _add_second_pass_filter_options(self, procedure):
        flags = GObject.ParamFlags.READWRITE
        procedure.add_boolean_argument("live-preview", "Live canvas preview", "Show the non-destructive filter composited in the image display while adjusting.", True, flags)
        procedure.add_int_argument("terminal-width", "Character columns", "0 auto: 80 columns for landscape input or inferred from rows.", 0, 4096, 0, flags)
        procedure.add_int_argument("terminal-height", "Character rows", "0 auto: 24 rows for portrait input or inferred from columns.", 0, 4096, 0, flags)
        procedure.add_double_argument("scanline-gap", "Scanline gap", "Darken vertical gaps between scanlines.", 0.0, 1.0, 0.15, flags)
        procedure.add_double_argument("pixel-roundness", "Pixel roundness", "Round terminal-pixel edges.", 0.0, 2.0, 0.85, flags)
        procedure.add_double_argument("roundness-aspect", "Roundness aspect", "Horizontal to vertical roundness aspect.", 0.1, 10.0, 0.8, flags)
        procedure.add_boolean_argument("hide-single-pixel", "Hide isolated pixels", "Hide isolated single terminal pixels.", True, flags)
        procedure.add_double_argument("glow", "Glow", "Small neighbor glow amount.", 0.0, 1.0, 0.0, flags)
        procedure.add_double_argument("tint-red", "Tint red", "Output tint red.", 0.0, 1.0, 1.0, flags)
        procedure.add_double_argument("tint-green", "Tint green", "Output tint green.", 0.0, 1.0, 191.0 / 255.0, flags)
        procedure.add_double_argument("tint-blue", "Tint blue", "Output tint blue.", 0.0, 1.0, 0.0, flags)

    def _export_from_filter_dialog(
        self,
        image,
        config,
        drawable,
        drawable_filters,
        hex_output,
        export_size,
        parent,
    ):
        path = None
        if not hex_output:
            path = _choose_save_path(parent)
            if not path:
                return

        preview_filter_index = _drawable_filter_index(
            drawable,
            drawable_filters[0] if drawable_filters else None,
        )
        output = _run_gif320sharp(
            config,
            image,
            drawable,
            preview_filter_index,
            hex_output,
            export_size,
        )

        if hex_output:
            _copy_text(output.decode("ascii"))
            _message("Copied Gif320Sharp VT320 bytes as ASCII hex.")
        else:
            with open(path, "wb") as output_file:
                output_file.write(output)
            _message("Saved Gif320Sharp VT320 bytes.")

    def _add_preview_export_buttons(self, dialog, image, config, drawable, drawable_filters):
        export_size = {"value": EXPORT_SIZE_NORMAL}
        export_frame = Gtk.Frame.new("Export")
        export_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        export_box.set_border_width(8)
        export_frame.add(export_box)

        export_row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        export_row.pack_start(Gtk.Label.new("Output size"), False, False, 0)
        export_combo = Gtk.ComboBoxText.new()
        export_combo.append(str(EXPORT_SIZE_NORMAL), "Normal")
        export_combo.append(str(EXPORT_SIZE_DOUBLE), "Double-width/double-height")
        export_combo.append(str(EXPORT_SIZE_FULL_SCREEN_DOUBLE), "Full-screen double-size")
        export_combo.set_active_id(str(EXPORT_SIZE_NORMAL))

        def export_size_changed(widget):
            active = widget.get_active_id()
            if active is not None:
                export_size["value"] = int(active)

        export_combo.connect("changed", export_size_changed)
        export_row.pack_start(export_combo, True, True, 0)
        export_box.pack_start(export_row, False, False, 0)

        button_box = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6)
        button_box.set_margin_top(6)
        button_box.set_margin_bottom(6)
        button_box.set_halign(Gtk.Align.START)

        save_button = Gtk.Button.new_with_label("Save VT320 Bytes...")
        copy_button = Gtk.Button.new_with_label("Copy Bytes as Hex")
        button_box.pack_start(save_button, False, False, 0)
        button_box.pack_start(copy_button, False, False, 0)

        def save_clicked(_button):
            try:
                self._export_from_filter_dialog(
                    image,
                    config,
                    drawable,
                    drawable_filters,
                    hex_output=False,
                    export_size=export_size["value"],
                    parent=dialog,
                )
            except Exception as exc:
                _message(str(exc))

        def copy_clicked(_button):
            try:
                self._export_from_filter_dialog(
                    image,
                    config,
                    drawable,
                    drawable_filters,
                    hex_output=True,
                    export_size=export_size["value"],
                    parent=dialog,
                )
            except Exception as exc:
                _message(str(exc))

        save_button.connect("clicked", save_clicked)
        copy_button.connect("clicked", copy_clicked)
        dialog.get_content_area().pack_start(export_frame, False, False, 0)
        dialog.get_content_area().pack_start(button_box, False, False, 0)
        export_frame.show_all()
        button_box.show_all()

    def _find_existing_preview_filter(self, drawable):
        try:
            filters = list(drawable.get_filters())
        except Exception:
            return None

        for drawable_filter in reversed(filters):
            try:
                if drawable_filter.get_operation_name() == "gif320:vt320-preview":
                    return drawable_filter
            except Exception:
                pass
        return None

    def _add_dialog_section(self, box, title):
        frame = Gtk.Frame.new(title)
        inner = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        inner.set_border_width(8)
        frame.add(inner)
        box.pack_start(frame, False, False, 0)
        return inner

    def _add_config_slider(self, box, config, name, step, page, digits):
        widget = GimpUi.prop_spin_scale_new(config, name, step, page, digits)
        box.pack_start(widget, False, False, 0)
        return widget

    def _add_config_check(self, box, config, name, label=None):
        widget = GimpUi.prop_check_button_new(config, name, label)
        box.pack_start(widget, False, False, 0)
        return widget

    def _add_int_combo(self, box, config, name, label, values):
        row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        row.pack_start(Gtk.Label.new(label), False, False, 0)
        combo = Gtk.ComboBoxText.new()
        for value, text in values:
            combo.append(str(value), text)
        combo.set_active_id(str(int(_config_value(config, name, values[0][0]))))

        def changed(widget):
            active = widget.get_active_id()
            if active is not None:
                _set_config_property(config, name, int(active))

        def sync(changed_config, _pspec):
            combo.set_active_id(str(int(_config_value(changed_config, name, values[0][0]))))

        combo.connect("changed", changed)
        config.connect("notify::" + name, sync)
        row.pack_start(combo, True, True, 0)
        box.pack_start(row, False, False, 0)
        return combo

    def _add_resolved_grid_label(self, box, drawable, config):
        label = Gtk.Label.new("")
        label.set_xalign(0.0)
        label.set_line_wrap(True)

        def update(_changed_config=None, _pspec=None):
            label.set_text(_resolved_grid_text(drawable, config))

        update()
        handler = config.connect("notify", update)
        box.pack_start(label, False, False, 0)
        label.show()
        return handler

    def _bind_cell_control_sensitivity(self, config, drawable, cells_x_widget, cells_y_widget):
        def update(_changed_config=None, _pspec=None):
            width, height = _source_size(drawable, config)
            size_mode = int(_config_value(config, "size-mode", 3))
            if size_mode == 0:
                cells_x_widget.set_sensitive(True)
                cells_y_widget.set_sensitive(True)
            elif size_mode == 1:
                cells_x_widget.set_sensitive(True)
                cells_y_widget.set_sensitive(False)
            elif size_mode == 2:
                cells_x_widget.set_sensitive(False)
                cells_y_widget.set_sensitive(True)
            elif height >= width:
                cells_x_widget.set_sensitive(False)
                cells_y_widget.set_sensitive(True)
            else:
                cells_x_widget.set_sensitive(True)
                cells_y_widget.set_sensitive(False)

        update()
        return config.connect("notify", update)

    def _add_atlas_editor(
        self,
        box,
        image,
        drawable,
        drawable_filter,
        config,
        atlas_edit_state=None,
    ):
        atlas_box = self._add_dialog_section(box, "Manual Atlas")
        state = {
            "glyphs": [],
            "glyph_index": 0,
            "drag_value": None,
            "drag_changed": False,
            "materialize_error": None,
        }

        toolbar = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6)
        glyph_label = Gtk.Label.new("Glyph")
        glyph_spin = Gtk.SpinButton.new_with_range(1, 1, 1)
        glyph_spin.set_digits(0)
        glyph_spin.set_numeric(True)
        status_label = Gtk.Label.new("")
        status_label.set_xalign(0.0)
        toolbar.pack_start(glyph_label, False, False, 0)
        toolbar.pack_start(glyph_spin, False, False, 0)
        atlas_box.pack_start(toolbar, False, False, 0)
        atlas_box.pack_start(status_label, False, False, 0)

        drawing = Gtk.DrawingArea.new()
        drawing.set_size_request(CELL_PIXEL_WIDTH * 18, CELL_PIXEL_HEIGHT * 18)
        drawing.add_events(
            Gdk.EventMask.BUTTON_PRESS_MASK
            | Gdk.EventMask.BUTTON_RELEASE_MASK
            | Gdk.EventMask.POINTER_MOTION_MASK
        )
        atlas_box.pack_start(drawing, False, False, 0)

        def set_status():
            count = len(state["glyphs"])
            if count == 0:
                status_label.set_text("Manual glyphs: none")
            else:
                status_label.set_text(f"Manual glyphs: {count}")
            drawing.set_sensitive(count > 0)
            glyph_spin.set_sensitive(count > 0)

        def selected_glyph():
            if not state["glyphs"]:
                return None
            index = max(0, min(state["glyph_index"], len(state["glyphs"]) - 1))
            return state["glyphs"][index]

        def update_from_config(_changed_config=None, _pspec=None):
            try:
                state["glyphs"] = _parse_atlas(_config_value(config, "manual-atlas", ""))
            except Exception as exc:
                state["glyphs"] = []
                status_label.set_text(str(exc))
                drawing.set_sensitive(False)
                glyph_spin.set_sensitive(False)
                drawing.queue_draw()
                return

            max_glyph = max(1, len(state["glyphs"]))
            if state["glyph_index"] >= max_glyph:
                state["glyph_index"] = max_glyph - 1
            glyph_spin.set_range(1, max_glyph)
            glyph_spin.set_value(state["glyph_index"] + 1)
            set_status()
            drawing.queue_draw()

        def write_atlas_to_config():
            _set_config_property(config, "manual-atlas", _format_atlas(state["glyphs"]))

        def commit_atlas_to_filter():
            atlas_text = _format_atlas(state["glyphs"])
            if atlas_edit_state is not None:
                commit = atlas_edit_state.get("commit_atlas")
                if commit is not None:
                    return commit(atlas_text)
            _set_config_property(config, "manual-atlas", atlas_text)
            return False

        def ensure_atlas_in_config():
            has_atlas = bool(str(_config_value(config, "manual-atlas", "") or "").strip())
            has_matching_state = _manual_state_matches_grid(drawable, config)
            if has_atlas and has_matching_state:
                return
            try:
                preview_filter_index = _drawable_filter_index(drawable, drawable_filter)
                atlas, cell_map = _materialize_gif320sharp_atlas_state(
                    config,
                    image,
                    drawable,
                    preview_filter_index,
                )
                if atlas and (not has_atlas or not has_matching_state):
                    _set_config_property(config, "manual-atlas", atlas)
                if cell_map:
                    _set_config_property(config, "manual-cell-map", cell_map)
            except Exception as exc:
                state["materialize_error"] = str(exc)
                status_label.set_text(str(exc))

        def draw(widget, cr):
            allocation = widget.get_allocation()
            width = allocation.width
            height = allocation.height
            cell = max(4.0, min(width / CELL_PIXEL_WIDTH, height / CELL_PIXEL_HEIGHT))
            grid_width = cell * CELL_PIXEL_WIDTH
            grid_height = cell * CELL_PIXEL_HEIGHT
            offset_x = (width - grid_width) * 0.5
            offset_y = (height - grid_height) * 0.5
            glyph = selected_glyph()

            cr.set_source_rgb(0.02, 0.02, 0.02)
            cr.rectangle(0, 0, width, height)
            cr.fill()

            for y in range(CELL_PIXEL_HEIGHT):
                for x in range(CELL_PIXEL_WIDTH):
                    if glyph is not None and _atlas_bit(glyph, x, y):
                        cr.set_source_rgb(1.0, 0.75, 0.0)
                    else:
                        cr.set_source_rgb(0.10, 0.10, 0.10)
                    cr.rectangle(
                        offset_x + x * cell + 1,
                        offset_y + y * cell + 1,
                        max(1.0, cell - 2),
                        max(1.0, cell - 2),
                    )
                    cr.fill()

            cr.set_source_rgb(0.28, 0.28, 0.28)
            cr.rectangle(offset_x, offset_y, grid_width, grid_height)
            cr.stroke()
            return False

        def event_to_cell(widget, event):
            allocation = widget.get_allocation()
            cell = max(
                4.0,
                min(
                    allocation.width / CELL_PIXEL_WIDTH,
                    allocation.height / CELL_PIXEL_HEIGHT,
                ),
            )
            grid_width = cell * CELL_PIXEL_WIDTH
            grid_height = cell * CELL_PIXEL_HEIGHT
            offset_x = (allocation.width - grid_width) * 0.5
            offset_y = (allocation.height - grid_height) * 0.5
            x = int((event.x - offset_x) / cell)
            y = int((event.y - offset_y) / cell)
            if x < 0 or x >= CELL_PIXEL_WIDTH or y < 0 or y >= CELL_PIXEL_HEIGHT:
                return None
            return x, y

        def set_cell_from_event(widget, event, value=None, write=False):
            glyph = selected_glyph()
            cell = event_to_cell(widget, event)
            if glyph is None or cell is None:
                return False
            x, y = cell
            next_value = (not _atlas_bit(glyph, x, y)) if value is None else value
            if _atlas_bit(glyph, x, y) == next_value:
                return True
            _set_atlas_bit(glyph, x, y, next_value)
            state["drag_changed"] = True
            if write:
                write_atlas_to_config()
            drawing.queue_draw()
            return True

        def button_press(widget, event):
            if event.button != 1:
                return False
            glyph = selected_glyph()
            cell = event_to_cell(widget, event)
            if glyph is None or cell is None:
                return False
            x, y = cell
            state["drag_value"] = not _atlas_bit(glyph, x, y)
            state["drag_changed"] = False
            if atlas_edit_state is not None:
                atlas_edit_state["dragging"] = True
            return set_cell_from_event(widget, event, state["drag_value"], write=False)

        def button_release(_widget, _event):
            if state["drag_changed"]:
                commit_atlas_to_filter()
            if atlas_edit_state is not None:
                atlas_edit_state["dragging"] = False
            state["drag_value"] = None
            state["drag_changed"] = False
            return False

        def motion(widget, event):
            if state["drag_value"] is None:
                return False
            return set_cell_from_event(widget, event, state["drag_value"], write=False)

        def glyph_changed(widget):
            state["glyph_index"] = max(0, int(widget.get_value()) - 1)
            drawing.queue_draw()

        glyph_spin.connect("value-changed", glyph_changed)
        drawing.connect("draw", draw)
        drawing.connect("button-press-event", button_press)
        drawing.connect("button-release-event", button_release)
        drawing.connect("motion-notify-event", motion)
        ensure_atlas_in_config()
        update_from_config()
        if state["materialize_error"] and not state["glyphs"]:
            status_label.set_text(state["materialize_error"])
        return config.connect("notify::manual-atlas", update_from_config)

    def _run_existing_preview_filter_dialog(self, image, drawable):
        drawable_filter = self._find_existing_preview_filter(drawable)
        if drawable_filter is None:
            return False

        config = drawable_filter.get_config()
        GimpUi.init("gif320sharp-existing-filter")
        dialog = Gtk.Dialog(
            title="Gif320Sharp VT320 Preview Filter",
            modal=True,
        )
        dialog.add_button("_Close", Gtk.ResponseType.CLOSE)
        dialog.set_default_size(520, 720)
        content = dialog.get_content_area()
        content.set_spacing(8)
        content.set_border_width(8)

        update_state = {
            "source": 0,
            "dirty": False,
            "closed": False,
        }
        atlas_edit_state = {
            "dragging": False,
            "committing": False,
        }

        def commit_filter_update():
            update_state["source"] = 0
            if update_state["closed"] or not update_state["dirty"]:
                return False
            update_state["dirty"] = False
            warning = _preview_cost_warning([drawable], config)
            if warning:
                status = getattr(self, "_last_preview_warning", None)
                if status != warning:
                    _log(warning)
                    self._last_preview_warning = warning
                return False

            try:
                image.undo_group_start()
                try:
                    drawable_filter.update()
                finally:
                    image.undo_group_end()
            except Exception as exc:
                _log("Could not update existing preview filter: " + str(exc))
                return False

            Gimp.displays_flush()
            return False

        def commit_atlas_filter_update(atlas_text):
            if update_state["closed"]:
                return False
            warning = _preview_cost_warning([drawable], config)
            if warning:
                _log(warning)
                return False

            atlas_edit_state["committing"] = True
            try:
                image.undo_group_start()
                try:
                    if not _set_config_property(config, "manual-atlas", atlas_text):
                        raise RuntimeError("manual-atlas property is not writable on this filter.")
                    drawable_filter.update()
                finally:
                    image.undo_group_end()
            except Exception as exc:
                _log("Could not update manual atlas: " + str(exc))
                return False
            finally:
                atlas_edit_state["committing"] = False

            update_state["dirty"] = False
            if update_state["source"]:
                try:
                    GLib.source_remove(update_state["source"])
                except Exception:
                    pass
                update_state["source"] = 0
            Gimp.displays_flush()
            return False

        def schedule_filter_update(delay_ms=120):
            if update_state["closed"]:
                return
            update_state["dirty"] = True
            if update_state["source"]:
                try:
                    GLib.source_remove(update_state["source"])
                except Exception:
                    pass
            update_state["source"] = GLib.timeout_add(delay_ms, commit_filter_update)

        atlas_edit_state["commit_atlas"] = commit_atlas_filter_update

        self._add_preview_export_buttons(dialog, image, config, drawable, [drawable_filter])

        scroller = Gtk.ScrolledWindow.new(None, None)
        scroller.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        controls = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        scroller.add(controls)
        content.pack_start(scroller, True, True, 0)

        layout = self._add_dialog_section(controls, "Layout")
        self._add_config_slider(layout, config, "crop-x", 1.0, 10.0, 0)
        self._add_config_slider(layout, config, "crop-y", 1.0, 10.0, 0)
        self._add_config_slider(layout, config, "crop-width", 1.0, 10.0, 0)
        self._add_config_slider(layout, config, "crop-height", 1.0, 10.0, 0)
        self._add_int_combo(layout, config, "size-mode", "Size mode", [
            (0, "Fixed"),
            (1, "Derive rows from columns"),
            (2, "Derive columns from rows"),
            (3, "Auto orientation"),
        ])
        cells_x_widget = self._add_config_slider(layout, config, "cells-x", 1.0, 10.0, 0)
        cells_y_widget = self._add_config_slider(layout, config, "cells-y", 1.0, 10.0, 0)
        grid_handler = self._add_resolved_grid_label(layout, drawable, config)
        sensitivity_handler = self._bind_cell_control_sensitivity(
            config,
            drawable,
            cells_x_widget,
            cells_y_widget,
        )
        self._add_config_slider(layout, config, "output-scale", 1.0, 1.0, 0)
        self._add_int_combo(layout, config, "resize-mode", "Resize mode", [
            (0, "Stretch"),
            (1, "Contain"),
            (2, "Cover"),
        ])

        conversion = self._add_dialog_section(controls, "Conversion")
        self._add_int_combo(conversion, config, "dither-mode", "Dither mode", [
            (0, "Threshold"),
            (1, "Checkerboard"),
            (2, "Floyd-Steinberg"),
        ])
        self._add_config_check(conversion, config, "auto-tune")
        self._add_config_check(conversion, config, "allow-glyph-reduction")
        self._add_config_slider(conversion, config, "max-glyphs", 1.0, 5.0, 0)
        self._add_config_slider(conversion, config, "red-balance", 1.0, 5.0, 1)
        self._add_config_slider(conversion, config, "green-balance", 1.0, 5.0, 1)
        self._add_config_slider(conversion, config, "blue-balance", 1.0, 5.0, 1)
        self._add_config_slider(conversion, config, "full-threshold", 0.01, 0.05, 2)
        self._add_config_slider(conversion, config, "half-threshold", 0.01, 0.05, 2)
        self._add_config_check(conversion, config, "lock-red-balance")
        self._add_config_check(conversion, config, "lock-green-balance")
        self._add_config_check(conversion, config, "lock-blue-balance")
        self._add_config_check(conversion, config, "lock-full-threshold")
        self._add_config_check(conversion, config, "lock-half-threshold")
        self._add_config_slider(conversion, config, "tune-frequency", 1.0, 10.0, 0)
        self._add_config_slider(conversion, config, "tune-smoothness", 1.0, 10.0, 0)
        self._add_config_slider(conversion, config, "tune-glyph-reuse", 1.0, 10.0, 0)
        self._add_config_slider(conversion, config, "reverse-video-tolerance", 1.0, 5.0, 0)

        atlas_handler = self._add_atlas_editor(
            controls,
            image,
            drawable,
            drawable_filter,
            config,
            atlas_edit_state,
        )

        color = self._add_dialog_section(controls, "Output")
        self._add_config_slider(color, config, "tint-red", 0.01, 0.05, 2)
        self._add_config_slider(color, config, "tint-green", 0.01, 0.05, 2)
        self._add_config_slider(color, config, "tint-blue", 0.01, 0.05, 2)
        self._add_config_check(color, config, "second-pass")
        self._add_config_slider(color, config, "scanline-gap", 0.01, 0.05, 2)
        self._add_config_slider(color, config, "pixel-roundness", 0.01, 0.10, 2)
        self._add_config_slider(color, config, "roundness-aspect", 0.01, 0.10, 2)
        self._add_config_check(color, config, "hide-single-pixel")
        self._add_config_slider(color, config, "glow", 0.01, 0.05, 2)

        def sync_preview(_changed_config, pspec):
            if (
                pspec is not None
                and getattr(pspec, "name", "") == "manual-atlas"
                and (
                    atlas_edit_state.get("dragging")
                    or atlas_edit_state.get("committing")
                )
            ):
                return
            schedule_filter_update()

        handler = config.connect("notify", sync_preview)
        try:
            dialog.show_all()
            dialog.run()
        finally:
            if update_state["source"]:
                try:
                    GLib.source_remove(update_state["source"])
                except Exception:
                    pass
                update_state["source"] = 0
                commit_filter_update()
            update_state["closed"] = True
            config.disconnect(handler)
            config.disconnect(grid_handler)
            config.disconnect(sensitivity_handler)
            config.disconnect(atlas_handler)
            dialog.destroy()
        Gimp.displays_flush()
        return True

    def _add_filter_dialog(
        self,
        procedure,
        config,
        title,
        on_change=None,
        source_drawable=None,
        image=None,
        export_drawable=None,
        export_drawable_filters=None,
    ):
        GimpUi.init("gif320sharp-filter")
        dialog = GimpUi.ProcedureDialog.new(procedure, config, title)
        dialog.fill(None)
        grid_handler = None
        if source_drawable is not None:
            grid_handler = self._add_resolved_grid_label(
                dialog.get_content_area(),
                source_drawable,
                config,
            )
        if export_drawable is not None:
            self._add_preview_export_buttons(
                dialog,
                image,
                config,
                export_drawable,
                export_drawable_filters or [],
            )
        handler = None
        if on_change is not None:
            handler = config.connect("notify", on_change)
        try:
            accepted = dialog.run()
        finally:
            if handler is not None:
                config.disconnect(handler)
            if grid_handler is not None:
                config.disconnect(grid_handler)
            dialog.destroy()
        return accepted

    def _set_filter_properties(self, drawable_filter, config, names):
        filter_config = drawable_filter.get_config()
        for name in names:
            try:
                filter_config.set_property(name, config.get_property(name))
            except Exception:
                pass
        drawable_filter.update()

    def _set_filter_visibility(self, drawable_filters, visible):
        for drawable_filter in drawable_filters:
            try:
                if drawable_filter.is_valid():
                    drawable_filter.set_visible(visible)
            except Exception:
                pass

    def _delete_filters(self, image, drawable_filters):
        if not drawable_filters:
            return
        image.undo_group_start()
        try:
            for drawable_filter in drawable_filters:
                try:
                    if drawable_filter.is_valid():
                        drawable_filter.delete()
                except Exception:
                    pass
        finally:
            image.undo_group_end()
        Gimp.displays_flush()

    def _create_gegl_filters(self, drawables, operation, filter_name, config, properties):
        drawable_filters = []
        for drawable in drawables:
            drawable_filter = Gimp.DrawableFilter.new(drawable, operation, filter_name)
            if drawable_filter is None:
                raise RuntimeError("GEGL operation is not available: " + operation)
            self._set_filter_properties(drawable_filter, config, properties)
            drawable.append_filter(drawable_filter)
            drawable_filters.append(drawable_filter)
        Gimp.displays_flush()
        return drawable_filters

    def _append_gegl_filter(self, image, drawables, operation, filter_name, config, properties):
        image.undo_group_start()
        try:
            self._create_gegl_filters(drawables, operation, filter_name, config, properties)
        finally:
            image.undo_group_end()
        Gimp.displays_flush()

    def _run_live_filter_dialog(
        self,
        procedure,
        image,
        drawables,
        config,
        title,
        operation,
        filter_name,
        properties,
        warning_func,
        allow_export=False,
    ):
        warning = warning_func(drawables, config)
        if warning:
            _message(warning)
            return False

        drawable_filters = []
        image.undo_group_start()
        try:
            drawable_filters = self._create_gegl_filters(
                drawables,
                operation,
                filter_name,
                config,
                properties,
            )
        finally:
            image.undo_group_end()

        def sync_preview(changed_config, _pspec):
            live_preview = bool(_config_value(changed_config, "live-preview", True))
            self._set_filter_visibility(drawable_filters, live_preview)
            if not live_preview:
                Gimp.displays_flush()
                return

            if warning_func(drawables, changed_config):
                return

            for drawable_filter in drawable_filters:
                try:
                    if drawable_filter.is_valid():
                        self._set_filter_properties(drawable_filter, changed_config, properties)
                except Exception:
                    pass
            Gimp.displays_flush()

        keep_filters = False
        try:
            if self._add_filter_dialog(
                procedure,
                config,
                title,
                sync_preview,
                source_drawable=drawables[0] if allow_export else None,
                image=image,
                export_drawable=drawables[0] if allow_export else None,
                export_drawable_filters=drawable_filters if allow_export else None,
            ):
                warning = warning_func(drawables, config)
                if warning:
                    _message(warning)
                    return False
                self._set_filter_visibility(drawable_filters, True)
                for drawable_filter in drawable_filters:
                    if drawable_filter.is_valid():
                        self._set_filter_properties(drawable_filter, config, properties)
                Gimp.displays_flush()
                keep_filters = True
                return True
            return False
        finally:
            if not keep_filters:
                self._delete_filters(image, drawable_filters)

    def run(self, procedure, run_mode, image, drawables, config, run_data):
        if len(drawables) == 0:
            return _return_error(
                procedure,
                Gimp.PDBStatusType.CANCEL,
                "No drawable is selected.",
            )

        name = procedure.get_name()
        try:
            if name == PREVIEW_FILTER_PROC:
                if run_mode == Gimp.RunMode.INTERACTIVE:
                    _apply_preview_interactive_defaults(drawables, config)
                    if not self._run_live_filter_dialog(
                        procedure,
                        image,
                        drawables,
                        config,
                        "Gif320Sharp VT320 Preview Filter",
                        "gif320:vt320-preview",
                        "Gif320Sharp VT320 Preview",
                        PREVIEW_FILTER_PROPERTIES,
                        _preview_cost_warning,
                        allow_export=True,
                    ):
                        return procedure.new_return_values(Gimp.PDBStatusType.CANCEL, GLib.Error())
                    _message("Added Gif320Sharp VT320 preview layer filter.")
                    return procedure.new_return_values(Gimp.PDBStatusType.SUCCESS, GLib.Error())

                warning = _preview_cost_warning(drawables, config)
                if warning:
                    return _return_error(procedure, Gimp.PDBStatusType.CANCEL, warning)
                self._append_gegl_filter(
                    image,
                    drawables,
                    "gif320:vt320-preview",
                    "Gif320Sharp VT320 Preview",
                    config,
                    PREVIEW_FILTER_PROPERTIES,
                )
                _message("Added Gif320Sharp VT320 preview layer filter.")
            elif name == EDIT_PREVIEW_FILTER_PROC:
                if run_mode != Gimp.RunMode.INTERACTIVE:
                    return _return_error(
                        procedure,
                        Gimp.PDBStatusType.CANCEL,
                        "Editing an existing Gif320Sharp VT320 preview filter requires interactive mode.",
                    )
                if not self._run_existing_preview_filter_dialog(image, drawables[0]):
                    return _return_error(
                        procedure,
                        Gimp.PDBStatusType.CANCEL,
                        "The selected layer does not have a Gif320Sharp VT320 preview filter.",
                    )
            elif name == SECOND_PASS_FILTER_PROC:
                if run_mode == Gimp.RunMode.INTERACTIVE:
                    _apply_second_pass_interactive_defaults(config)
                    if not self._run_live_filter_dialog(
                        procedure,
                        image,
                        drawables,
                        config,
                        "Gif320Sharp VT320 Second Pass Filter",
                        "gif320:vt320-second-pass",
                        "Gif320Sharp VT320 Second Pass",
                        SECOND_PASS_FILTER_PROPERTIES,
                        _second_pass_cost_warning,
                    ):
                        return procedure.new_return_values(Gimp.PDBStatusType.CANCEL, GLib.Error())
                    _message("Added Gif320Sharp VT320 second-pass layer filter.")
                    return procedure.new_return_values(Gimp.PDBStatusType.SUCCESS, GLib.Error())

                warning = _second_pass_cost_warning(drawables, config)
                if warning:
                    return _return_error(procedure, Gimp.PDBStatusType.CANCEL, warning)
                self._append_gegl_filter(
                    image,
                    drawables,
                    "gif320:vt320-second-pass",
                    "Gif320Sharp VT320 Second Pass",
                    config,
                    SECOND_PASS_FILTER_PROPERTIES,
                )
                _message("Added Gif320Sharp VT320 second-pass layer filter.")
        except Exception as exc:
            message = str(exc) or exc.__class__.__name__
            return _return_error(procedure, Gimp.PDBStatusType.EXECUTION_ERROR, message)

        return procedure.new_return_values(Gimp.PDBStatusType.SUCCESS, GLib.Error())


if __name__ == "__main__":
    Gimp.main(Gif320SharpExportPlugin.__gtype__, sys.argv)
