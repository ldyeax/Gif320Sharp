#ifdef GEGL_PROPERTIES

property_int (crop_x, "Crop X", 0)
  value_range (-100000, 100000)
property_int (crop_y, "Crop Y", 0)
  value_range (-100000, 100000)
property_int (crop_width, "Crop width", 0)
  value_range (0, 100000)
property_int (crop_height, "Crop height", 0)
  value_range (0, 100000)
property_int (size_mode, "Size mode", 3)
  value_range (0, 3)
  description ("0 fixed, 1 derive height from width, 2 derive width from height, 3 auto orientation")
property_int (cells_x, "Character columns", 80)
  value_range (1, 240)
  ui_range (1, 120)
property_int (cells_y, "Character rows", 24)
  value_range (1, 120)
  ui_range (1, 80)
property_int (output_scale, "Output scale", 2)
  value_range (1, 12)
  ui_range (1, 6)
property_int (resize_mode, "Resize mode", 2)
  value_range (0, 2)
  description ("0 stretch, 1 contain, 2 cover")
property_int (dither_mode, "Dither mode", 1)
  value_range (0, 2)
  description ("0 threshold, 1 checkerboard, 2 Floyd-Steinberg")
property_boolean (auto_tune, "Auto tune", TRUE)
property_boolean (allow_glyph_reduction, "Allow glyph reduction", TRUE)
property_int (max_glyphs, "Max glyphs", 94)
  value_range (1, 94)
property_double (red_balance, "Red balance", 30.0)
  value_range (0.0, 100.0)
property_double (green_balance, "Green balance", 40.0)
  value_range (0.0, 100.0)
property_double (blue_balance, "Blue balance", 10.0)
  value_range (0.0, 100.0)
property_double (full_threshold, "Full threshold", 0.50)
  value_range (0.0, 1.0)
property_double (half_threshold, "Half threshold", 0.25)
  value_range (0.0, 1.0)
property_boolean (lock_red_balance, "Lock red balance", FALSE)
property_boolean (lock_green_balance, "Lock green balance", FALSE)
property_boolean (lock_blue_balance, "Lock blue balance", FALSE)
property_boolean (lock_full_threshold, "Lock full threshold", FALSE)
property_boolean (lock_half_threshold, "Lock half threshold", FALSE)
property_int (tune_frequency, "Tune frequency", 0)
  value_range (-100, 100)
property_int (tune_smoothness, "Tune smoothness", 0)
  value_range (-100, 100)
property_int (tune_glyph_reuse, "Tune glyph reuse", 0)
  value_range (-100, 100)
property_int (reverse_video_tolerance, "Reverse video tolerance", 4)
  value_range (0, 180)
property_string (manual_atlas, "Manual atlas", "")
property_string (manual_cell_map, "Manual cell map", "")
property_double (tint_red, "Tint red", 1.0)
  value_range (0.0, 1.0)
property_double (tint_green, "Tint green", 0.7490196078431373)
  value_range (0.0, 1.0)
property_double (tint_blue, "Tint blue", 0.0)
  value_range (0.0, 1.0)
property_boolean (second_pass, "VT320 second pass", FALSE)
property_double (scanline_gap, "Scanline gap", 0.15)
  value_range (0.0, 1.0)
property_double (pixel_roundness, "Pixel roundness", 0.85)
  value_range (0.0, 2.0)
property_double (roundness_aspect, "Roundness aspect", 0.8)
  value_range (0.1, 10.0)
property_boolean (hide_single_pixel, "Hide isolated pixels", TRUE)
property_double (glow, "Glow", 0.0)
  value_range (0.0, 1.0)

#else

#define GEGL_OP_FILTER
#define GEGL_OP_NAME gif320_vt320_preview
#define GEGL_OP_C_SOURCE gif320_vt320_preview.c

#include "gegl-op.h"
#include "gif320_vt320_core.h"

#include <math.h>
#include <string.h>

typedef struct Gif320BufferSampleContext
{
	GeglBuffer *buffer;
	const Babl *format;
} Gif320BufferSampleContext;

typedef struct Gif320PreviewCache
{
	gboolean valid;
	GeglOperation *operation;
	GeglBuffer *source_buffer;
	Gif320Vt320Options options;
	Gif320Rect source;
	guint64 source_hash;
	GeglRectangle output_rect;
	GeglRectangle display_rect;
	gint level;
	int cells_x;
	int cells_y;
	float *pixels;
} Gif320PreviewCache;

#define GIF320_PREVIEW_MAX_CELLS 12000
#define GIF320_PREVIEW_MAX_TERMINAL_PIXELS 2500000
#define GIF320_PREVIEW_MAX_OUTPUT_PIXELS 12000000

static GMutex preview_cache_mutex;
static Gif320PreviewCache preview_cache;

static gboolean
log_enabled (void)
{
	const gchar *enabled = g_getenv ("GIF320_GEGL_LOG");
	return enabled != NULL && enabled[0] != '\0' && g_strcmp0 (enabled, "0") != 0;
}

static void
log_elapsed (
	const gchar *label,
	gint64 start_us,
	const Gif320PreviewCache *cache
)
{
	if (!log_enabled ())
	{
		return;
	}

	g_message (
		"Gif320Sharp GEGL preview %s in %.3f ms: cells=%dx%d source=%dx%d "
		"output=%dx%d display=%dx%d+%d+%d second-pass=%d",
		label,
		(g_get_monotonic_time () - start_us) / 1000.0,
		cache->cells_x,
		cache->cells_y,
		cache->source.width,
		cache->source.height,
		cache->output_rect.width,
		cache->output_rect.height,
		cache->display_rect.width,
		cache->display_rect.height,
		cache->display_rect.x,
		cache->display_rect.y,
		cache->options.second_pass ? 1 : 0
	);
}

static void
clear_preview_cache (void)
{
	g_free ((gchar *)preview_cache.options.manual_atlas);
	g_free ((gchar *)preview_cache.options.manual_cell_map);
	g_free (preview_cache.pixels);
	memset (&preview_cache, 0, sizeof (preview_cache));
}

static gboolean
same_rect (const GeglRectangle *left, const GeglRectangle *right)
{
	return left->x == right->x
		&& left->y == right->y
		&& left->width == right->width
		&& left->height == right->height;
}

static gboolean
same_source (const Gif320Rect *left, const Gif320Rect *right)
{
	return left->x == right->x
		&& left->y == right->y
		&& left->width == right->width
		&& left->height == right->height;
}

static gboolean
same_options (
	const Gif320Vt320Options *left,
	const Gif320Vt320Options *right
)
{
	return left->size_mode == right->size_mode
		&& left->cells_x == right->cells_x
		&& left->cells_y == right->cells_y
		&& left->output_scale == right->output_scale
		&& left->resize_mode == right->resize_mode
		&& left->dither_mode == right->dither_mode
		&& left->allow_glyph_reduction == right->allow_glyph_reduction
		&& left->max_glyphs == right->max_glyphs
		&& left->red_balance == right->red_balance
		&& left->green_balance == right->green_balance
		&& left->blue_balance == right->blue_balance
		&& left->full_threshold == right->full_threshold
		&& left->half_threshold == right->half_threshold
		&& left->lock_red_balance == right->lock_red_balance
		&& left->lock_green_balance == right->lock_green_balance
		&& left->lock_blue_balance == right->lock_blue_balance
		&& left->lock_full_threshold == right->lock_full_threshold
		&& left->lock_half_threshold == right->lock_half_threshold
		&& left->auto_tune == right->auto_tune
		&& left->tune_frequency == right->tune_frequency
		&& left->tune_smoothness == right->tune_smoothness
		&& left->tune_glyph_reuse == right->tune_glyph_reuse
		&& left->reverse_video_tolerance == right->reverse_video_tolerance
		&& g_strcmp0 (left->manual_atlas, right->manual_atlas) == 0
		&& g_strcmp0 (left->manual_cell_map, right->manual_cell_map) == 0
		&& left->tint_red == right->tint_red
		&& left->tint_green == right->tint_green
		&& left->tint_blue == right->tint_blue
		&& left->second_pass == right->second_pass
		&& left->scanline_gap == right->scanline_gap
		&& left->pixel_roundness == right->pixel_roundness
		&& left->roundness_aspect == right->roundness_aspect
		&& left->hide_single_pixel == right->hide_single_pixel
		&& left->glow == right->glow;
}

static guint64
hash_source (
	GeglBuffer *source_buffer,
	const Gif320Rect *source
)
{
	const Babl *format = babl_format ("R'G'B'A u8");
	guint64 hash = 1469598103934665603ULL;
	guint8 *row;
	GeglRectangle row_rect;

	hash ^= (guint64)(guint32)source->x;
	hash *= 1099511628211ULL;
	hash ^= (guint64)(guint32)source->y;
	hash *= 1099511628211ULL;
	hash ^= (guint64)(guint32)source->width;
	hash *= 1099511628211ULL;
	hash ^= (guint64)(guint32)source->height;
	hash *= 1099511628211ULL;

	if (source->width <= 0 || source->height <= 0)
	{
		return hash;
	}

	row = g_try_malloc_n ((gsize)source->width, 4);
	if (row == NULL)
	{
		return hash ^ 0xffffffffffffffffULL;
	}

	row_rect.x = source->x;
	row_rect.y = source->y;
	row_rect.width = source->width;
	row_rect.height = 1;
	for (int y = 0; y < source->height; y++)
	{
		row_rect.y = source->y + y;
		gegl_buffer_get (
			source_buffer,
			&row_rect,
			1.0,
			format,
			row,
			GEGL_AUTO_ROWSTRIDE,
			GEGL_ABYSS_NONE
		);

		for (int i = 0; i < source->width * 4; i++)
		{
			hash ^= row[i];
			hash *= 1099511628211ULL;
		}
	}

	g_free (row);
	return hash;
}

static void
fill_black_background (float *pixels, int width, int height)
{
	gsize count = (gsize)width * (gsize)height;

	for (gsize i = 0; i < count; i++)
	{
		pixels[i * 4 + 0] = 0.0f;
		pixels[i * 4 + 1] = 0.0f;
		pixels[i * 4 + 2] = 0.0f;
		pixels[i * 4 + 3] = 1.0f;
	}
}

static void
copy_rect (
	float *target,
	int target_width,
	int target_x,
	int target_y,
	const float *source,
	int source_width,
	int source_height
)
{
	for (int y = 0; y < source_height; y++)
	{
		memcpy (
			target + (((target_y + y) * target_width + target_x) * 4),
			source + (y * source_width * 4),
			(gsize)source_width * 4 * sizeof (float)
		);
	}
}

static void
copy_tile_from_cache (
	const Gif320PreviewCache *cache,
	const GeglRectangle *tile_rect,
	float *pixels
)
{
	for (int y = 0; y < tile_rect->height; y++)
	{
		int source_y = tile_rect->y - cache->output_rect.y + y;
		int source_x = tile_rect->x - cache->output_rect.x;
		memcpy (
			pixels + (y * tile_rect->width * 4),
			cache->pixels + ((source_y * cache->output_rect.width + source_x) * 4),
			(gsize)tile_rect->width * 4 * sizeof (float)
		);
	}
}

static GeglRectangle
resolve_display_rect (
	const Gif320Vt320Options *options,
	const Gif320Rect *source,
	const GeglRectangle *output_rect,
	int *cells_x,
	int *cells_y
)
{
	GeglRectangle display = *output_rect;
	double terminal_aspect;
	double output_aspect;

	gif320_vt320_resolve_cells (options, source, cells_x, cells_y);
	terminal_aspect = (*cells_x * GIF320_DISPLAY_CELL_ASPECT) / (double)*cells_y;
	output_aspect = output_rect->width / (double)output_rect->height;

	if (output_aspect > terminal_aspect)
	{
		display.height = output_rect->height;
		display.width = MAX (1, (int)floor (display.height * terminal_aspect + 0.5));
		display.x = output_rect->x + (output_rect->width - display.width) / 2;
		display.y = output_rect->y;
	}
	else
	{
		display.width = output_rect->width;
		display.height = MAX (1, (int)floor (display.width / terminal_aspect + 0.5));
		display.x = output_rect->x;
		display.y = output_rect->y + (output_rect->height - display.height) / 2;
	}

	display.width = MIN (display.width, output_rect->width);
	display.height = MIN (display.height, output_rect->height);
	return display;
}

static void
sample_buffer (void *context, double x, double y, float rgba[4])
{
	Gif320BufferSampleContext *sample_context = context;
	gegl_buffer_sample (
		sample_context->buffer,
		x,
		y,
		NULL,
		rgba,
		sample_context->format,
		GEGL_SAMPLER_LINEAR,
		GEGL_ABYSS_NONE
	);
}

static void
fill_options (GeglProperties *o, Gif320Vt320Options *options)
{
	memset (options, 0, sizeof (*options));
	gif320_vt320_options_init (options);
	options->size_mode = (Gif320SizeMode)o->size_mode;
	options->cells_x = o->cells_x;
	options->cells_y = o->cells_y;
	options->output_scale = o->output_scale;
	options->resize_mode = (Gif320ResizeMode)o->resize_mode;
	options->dither_mode = (Gif320DitherMode)o->dither_mode;
	options->allow_glyph_reduction = o->allow_glyph_reduction;
	options->max_glyphs = o->max_glyphs;
	options->red_balance = o->red_balance;
	options->green_balance = o->green_balance;
	options->blue_balance = o->blue_balance;
	options->full_threshold = o->full_threshold;
	options->half_threshold = o->half_threshold;
	options->lock_red_balance = o->lock_red_balance;
	options->lock_green_balance = o->lock_green_balance;
	options->lock_blue_balance = o->lock_blue_balance;
	options->lock_full_threshold = o->lock_full_threshold;
	options->lock_half_threshold = o->lock_half_threshold;
	options->auto_tune = o->auto_tune;
	options->tune_frequency = o->tune_frequency;
	options->tune_smoothness = o->tune_smoothness;
	options->tune_glyph_reuse = o->tune_glyph_reuse;
	options->reverse_video_tolerance = o->reverse_video_tolerance;
	options->manual_atlas = o->manual_atlas != NULL ? o->manual_atlas : "";
	options->manual_cell_map = o->manual_cell_map != NULL ? o->manual_cell_map : "";
	options->tint_red = o->tint_red;
	options->tint_green = o->tint_green;
	options->tint_blue = o->tint_blue;
	options->second_pass = o->second_pass;
	options->scanline_gap = o->scanline_gap;
	options->pixel_roundness = o->pixel_roundness;
	options->roundness_aspect = o->roundness_aspect;
	options->hide_single_pixel = o->hide_single_pixel;
	options->glow = o->glow;
}

static Gif320Rect
resolve_source_rect (GeglProperties *o, GeglBuffer *source_buffer)
{
	const GeglRectangle *extent = gegl_buffer_get_extent (source_buffer);
	Gif320Rect source;

	source.x = extent->x;
	source.y = extent->y;
	source.width = extent->width;
	source.height = extent->height;

	if (o->crop_width > 0 && o->crop_height > 0)
	{
		int right;
		int bottom;

		source.x = o->crop_x;
		source.y = o->crop_y;
		source.width = o->crop_width;
		source.height = o->crop_height;

		right = source.x + source.width;
		bottom = source.y + source.height;
		source.x = MAX (source.x, extent->x);
		source.y = MAX (source.y, extent->y);
		right = MIN (right, extent->x + extent->width);
		bottom = MIN (bottom, extent->y + extent->height);
		source.width = MAX (0, right - source.x);
		source.height = MAX (0, bottom - source.y);
	}

	return source;
}

static GeglRectangle
resolve_output_rect (GeglBuffer *source_buffer)
{
	const GeglRectangle *extent = gegl_buffer_get_extent (source_buffer);
	return *extent;
}

static gboolean
validate_preview_cost (
	const Gif320Vt320Options *options,
	const Gif320Rect *source,
	int output_width,
	int output_height
)
{
	int cells_x;
	int cells_y;
	gint64 cell_count;
	gint64 terminal_pixels;
	gint64 output_pixels;

	gif320_vt320_resolve_cells (options, source, &cells_x, &cells_y);
	cell_count = (gint64)cells_x * (gint64)cells_y;
	terminal_pixels = cell_count
		* GIF320_CELL_PIXEL_WIDTH
		* GIF320_CELL_PIXEL_HEIGHT;
	output_pixels = (gint64)output_width * (gint64)output_height;

	if (cell_count > GIF320_PREVIEW_MAX_CELLS
		|| terminal_pixels > GIF320_PREVIEW_MAX_TERMINAL_PIXELS
		|| output_pixels > GIF320_PREVIEW_MAX_OUTPUT_PIXELS)
	{
		static gboolean warned_too_large = FALSE;
		if (!warned_too_large)
		{
			g_warning (
				"Gif320Sharp VT320 preview skipped: %d x %d cells, "
				"%lld terminal samples, %lld output pixels is too intensive "
				"for live GIMP preview. Reduce character rows/columns or layer size.",
				cells_x,
				cells_y,
				(long long)terminal_pixels,
				(long long)output_pixels
			);
			warned_too_large = TRUE;
		}
		return FALSE;
	}

	return TRUE;
}

static gboolean
ensure_preview_cache (
	GeglOperation *operation,
	GeglBuffer *source_buffer,
	const Gif320Vt320Options *options,
	const Gif320Rect *source,
	const GeglRectangle *output_rect,
	gint level
)
{
	const Babl *format = babl_format ("R'G'B'A float");
	Gif320BufferSampleContext sample_context = { source_buffer, format };
	GeglRectangle display_rect;
	Gif320PreviewCache next;
	float *visual_pixels = NULL;
	gint64 start_us = g_get_monotonic_time ();
	int cells_x;
	int cells_y;
	guint64 source_hash;

	display_rect = resolve_display_rect (
		options,
		source,
		output_rect,
		&cells_x,
		&cells_y
	);
	source_hash = hash_source (source_buffer, source);

	if (preview_cache.valid
		&& preview_cache.source_hash == source_hash
		&& preview_cache.level == level
		&& same_options (&preview_cache.options, options)
		&& same_source (&preview_cache.source, source)
		&& same_rect (&preview_cache.output_rect, output_rect)
		&& same_rect (&preview_cache.display_rect, &display_rect))
	{
		if (g_strcmp0 (g_getenv ("GIF320_GEGL_LOG"), "verbose") == 0)
		{
			log_elapsed ("cache hit", start_us, &preview_cache);
		}
		return TRUE;
	}

	memset (&next, 0, sizeof (next));
	next.operation = operation;
	next.source_buffer = source_buffer;
	next.source = *source;
	next.source_hash = source_hash;
	next.output_rect = *output_rect;
	next.display_rect = display_rect;
	next.level = level;
	next.cells_x = cells_x;
	next.cells_y = cells_y;

	if (!validate_preview_cost (
		options,
		source,
		output_rect->width,
		output_rect->height
	))
	{
		return FALSE;
	}

	next.pixels = g_new0 (
		float,
		(gsize)output_rect->width * (gsize)output_rect->height * 4
	);
	visual_pixels = g_new0 (
		float,
		(gsize)display_rect.width * (gsize)display_rect.height * 4
	);
	if (next.pixels == NULL || visual_pixels == NULL)
	{
		g_free (next.pixels);
		g_free (visual_pixels);
		return FALSE;
	}

	fill_black_background (next.pixels, output_rect->width, output_rect->height);
	if (!gif320_vt320_render_preview (
		options,
		source,
		sample_buffer,
		&sample_context,
		visual_pixels,
		display_rect.width,
		display_rect.height
	))
	{
		g_free (next.pixels);
		g_free (visual_pixels);
		return FALSE;
	}

	copy_rect (
		next.pixels,
		output_rect->width,
		display_rect.x - output_rect->x,
		display_rect.y - output_rect->y,
		visual_pixels,
		display_rect.width,
		display_rect.height
	);
	g_free (visual_pixels);
	next.options = *options;
	next.options.manual_atlas = g_strdup (options->manual_atlas != NULL
		? options->manual_atlas
		: "");
	next.options.manual_cell_map = g_strdup (options->manual_cell_map != NULL
		? options->manual_cell_map
		: "");
	if (next.options.manual_atlas == NULL || next.options.manual_cell_map == NULL)
	{
		g_free ((gchar *)next.options.manual_atlas);
		g_free ((gchar *)next.options.manual_cell_map);
		g_free (next.pixels);
		return FALSE;
	}

	clear_preview_cache ();
	preview_cache = next;
	preview_cache.valid = TRUE;
	log_elapsed ("cache miss/render", start_us, &preview_cache);
	return TRUE;
}

static GeglRectangle
get_bounding_box (GeglOperation *operation)
{
	const GeglRectangle *input_extent =
		gegl_operation_source_get_bounding_box (operation, "input");
	GeglRectangle result = { 0, 0, 1, 1 };

	if (input_extent == NULL)
	{
		return result;
	}

	return *input_extent;
}

static GeglRectangle
get_cached_region (GeglOperation *operation, const GeglRectangle *output_roi)
{
	(void)output_roi;
	return get_bounding_box (operation);
}

static GeglRectangle
get_invalidated_by_change (
	GeglOperation *operation,
	const gchar *input_pad,
	const GeglRectangle *input_roi
)
{
	(void)input_pad;
	(void)input_roi;
	return get_bounding_box (operation);
}

static GeglRectangle
get_required_for_output (
	GeglOperation *operation,
	const gchar *input_pad,
	const GeglRectangle *output_roi
)
{
	GeglProperties *o = GEGL_PROPERTIES (operation);
	const GeglRectangle *input_extent =
		gegl_operation_source_get_bounding_box (operation, input_pad);
	GeglRectangle required = { 0, 0, 0, 0 };
	GeglRectangle crop;

	(void)output_roi;

	if (input_extent == NULL)
	{
		return required;
	}

	required = *input_extent;
	if (o->crop_width > 0 && o->crop_height > 0)
	{
		crop.x = o->crop_x;
		crop.y = o->crop_y;
		crop.width = o->crop_width;
		crop.height = o->crop_height;
		gegl_rectangle_intersect (&required, &required, &crop);
	}

	return required;
}

static void
prepare (GeglOperation *operation)
{
	const Babl *format = babl_format ("R'G'B'A float");

	gegl_operation_set_format (operation, "input", format);
	gegl_operation_set_format (operation, "output", format);
}

static gboolean
process (
	GeglOperation *operation,
	GeglBuffer *input,
	GeglBuffer *output,
	const GeglRectangle *result,
	gint level
)
{
	GeglProperties *o = GEGL_PROPERTIES (operation);
	const Babl *format = babl_format ("R'G'B'A float");
	GeglBuffer *source_buffer = input;
	Gif320Vt320Options options;
	Gif320Rect source;
	GeglRectangle output_rect;
	GeglRectangle render_rect;
	float *pixels;

	if (source_buffer == NULL || result == NULL)
	{
		return FALSE;
	}

	fill_options (o, &options);
	source = resolve_source_rect (o, source_buffer);
	output_rect = resolve_output_rect (source_buffer);

	if (!gegl_rectangle_intersect (&render_rect, result, &output_rect))
	{
		return TRUE;
	}

	g_mutex_lock (&preview_cache_mutex);
	if (!ensure_preview_cache (
		operation,
		source_buffer,
		&options,
		&source,
		&output_rect,
		level
	))
	{
		g_mutex_unlock (&preview_cache_mutex);
		return FALSE;
	}

	pixels = g_new0 (
		float,
		(gsize)render_rect.width * (gsize)render_rect.height * 4
	);
	if (pixels == NULL)
	{
		g_mutex_unlock (&preview_cache_mutex);
		return FALSE;
	}

	copy_tile_from_cache (&preview_cache, &render_rect, pixels);
	g_mutex_unlock (&preview_cache_mutex);

	gegl_buffer_set (
		output,
		&render_rect,
		0,
		format,
		pixels,
		GEGL_AUTO_ROWSTRIDE
	);
	g_free (pixels);
	return TRUE;
}

static void
gegl_op_class_init (GeglOpClass *klass)
{
	GeglOperationClass *operation_class = GEGL_OPERATION_CLASS (klass);
	GeglOperationFilterClass *filter_class = GEGL_OPERATION_FILTER_CLASS (klass);

	operation_class->prepare = prepare;
	operation_class->get_bounding_box = get_bounding_box;
	operation_class->get_cached_region = get_cached_region;
	operation_class->get_invalidated_by_change = get_invalidated_by_change;
	operation_class->get_required_for_output = get_required_for_output;
	operation_class->threaded = TRUE;
	filter_class->process = process;

	gegl_operation_class_set_keys (
		operation_class,
		"name", "gif320:vt320-preview",
		"title", "Gif320Sharp VT320 Preview",
		"categories", "artistic:render",
		"description",
			"Render the layer as a Gif320Sharp/VT320 character-cell raster preview.",
		NULL
	);
}

#endif
