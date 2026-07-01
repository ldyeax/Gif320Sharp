#ifdef GEGL_PROPERTIES

property_int (terminal_width, "Character columns", 0)
  value_range (0, 4096)
  description ("0 auto: 80 columns for landscape input or inferred from rows")
property_int (terminal_height, "Character rows", 0)
  value_range (0, 4096)
  description ("0 auto: 24 rows for portrait input or inferred from columns")
property_double (tint_red, "Tint red", 1.0)
  value_range (0.0, 1.0)
property_double (tint_green, "Tint green", 0.7490196078431373)
  value_range (0.0, 1.0)
property_double (tint_blue, "Tint blue", 0.0)
  value_range (0.0, 1.0)
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
#define GEGL_OP_NAME gif320_vt320_second_pass
#define GEGL_OP_C_SOURCE gif320_vt320_second_pass.c

#include "gegl-op.h"
#include "gif320_vt320_core.h"

#include <string.h>

typedef struct Gif320BufferSampleContext
{
	GeglBuffer *buffer;
	const Babl *format;
} Gif320BufferSampleContext;

typedef struct Gif320SecondPassCache
{
	gboolean valid;
	Gif320Vt320Options options;
	Gif320Rect source;
	guint64 source_hash;
	GeglRectangle output_rect;
	gint level;
	int cells_x;
	int cells_y;
	float *pixels;
} Gif320SecondPassCache;

#define GIF320_SECOND_PASS_MAX_CELLS 500000
#define GIF320_SECOND_PASS_MAX_OUTPUT_PIXELS 12000000

static GMutex second_pass_cache_mutex;
static Gif320SecondPassCache second_pass_cache;

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
	const Gif320SecondPassCache *cache
)
{
	if (!log_enabled ())
	{
		return;
	}

	g_message (
		"Gif320Sharp GEGL second pass %s in %.3f ms: cells=%dx%d "
		"source=%dx%d output=%dx%d",
		label,
		(g_get_monotonic_time () - start_us) / 1000.0,
		cache->cells_x,
		cache->cells_y,
		cache->source.width,
		cache->source.height,
		cache->output_rect.width,
		cache->output_rect.height
	);
}

static void
clear_second_pass_cache (void)
{
	g_free (second_pass_cache.pixels);
	memset (&second_pass_cache, 0, sizeof (second_pass_cache));
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
	return memcmp (left, right, sizeof (*left)) == 0;
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
copy_tile_from_cache (
	const Gif320SecondPassCache *cache,
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
	options->tint_red = o->tint_red;
	options->tint_green = o->tint_green;
	options->tint_blue = o->tint_blue;
	options->scanline_gap = o->scanline_gap;
	options->pixel_roundness = o->pixel_roundness;
	options->roundness_aspect = o->roundness_aspect;
	options->hide_single_pixel = o->hide_single_pixel;
	options->glow = o->glow;
}

static gboolean
validate_second_pass_cost (
	const Gif320Rect *source,
	int configured_cells_x,
	int configured_cells_y,
	int output_width,
	int output_height,
	int *cells_x,
	int *cells_y
)
{
	gint64 cell_count;
	gint64 output_pixels;

	gif320_vt320_resolve_second_pass_cells (
		source,
		configured_cells_x,
		configured_cells_y,
		cells_x,
		cells_y
	);
	cell_count = (gint64)(*cells_x) * (gint64)(*cells_y);
	output_pixels = (gint64)output_width * (gint64)output_height;
	if (cell_count > GIF320_SECOND_PASS_MAX_CELLS
		|| output_pixels > GIF320_SECOND_PASS_MAX_OUTPUT_PIXELS)
	{
		static gboolean warned_too_large = FALSE;
		if (!warned_too_large)
		{
			g_warning (
				"Gif320Sharp VT320 second pass skipped: %d x %d cells, "
				"%lld output pixels is too intensive for live GIMP preview. "
				"Reduce character rows/columns or layer size.",
				*cells_x,
				*cells_y,
				(long long)output_pixels
			);
			warned_too_large = TRUE;
		}
		return FALSE;
	}

	return TRUE;
}

static gboolean
ensure_second_pass_cache (
	GeglBuffer *source_buffer,
	const Gif320Vt320Options *options,
	const Gif320Rect *source,
	const GeglRectangle *output_rect,
	int configured_cells_x,
	int configured_cells_y,
	gint level,
	const Babl *format
)
{
	Gif320BufferSampleContext sample_context = { source_buffer, format };
	Gif320SecondPassCache next;
	gint64 start_us = g_get_monotonic_time ();
	guint64 source_hash = hash_source (source_buffer, source);
	int cells_x;
	int cells_y;

	if (!validate_second_pass_cost (
		source,
		configured_cells_x,
		configured_cells_y,
		output_rect->width,
		output_rect->height,
		&cells_x,
		&cells_y
	))
	{
		return FALSE;
	}

	if (second_pass_cache.valid
		&& second_pass_cache.source_hash == source_hash
		&& second_pass_cache.level == level
		&& second_pass_cache.cells_x == cells_x
		&& second_pass_cache.cells_y == cells_y
		&& same_options (&second_pass_cache.options, options)
		&& same_source (&second_pass_cache.source, source)
		&& same_rect (&second_pass_cache.output_rect, output_rect))
	{
		if (g_strcmp0 (g_getenv ("GIF320_GEGL_LOG"), "verbose") == 0)
		{
			log_elapsed ("cache hit", start_us, &second_pass_cache);
		}
		return TRUE;
	}

	memset (&next, 0, sizeof (next));
	next.options = *options;
	next.source = *source;
	next.source_hash = source_hash;
	next.output_rect = *output_rect;
	next.level = level;
	next.cells_x = cells_x;
	next.cells_y = cells_y;
	next.pixels = g_new0 (
		float,
		(gsize)output_rect->width * (gsize)output_rect->height * 4
	);
	if (next.pixels == NULL)
	{
		return FALSE;
	}

	if (!gif320_vt320_render_second_pass_sampled (
		options,
		source,
		cells_x,
		cells_y,
		sample_buffer,
		&sample_context,
		next.pixels,
		output_rect->x,
		output_rect->y,
		output_rect->width,
		output_rect->height
	))
	{
		g_free (next.pixels);
		return FALSE;
	}

	clear_second_pass_cache ();
	second_pass_cache = next;
	second_pass_cache.valid = TRUE;
	log_elapsed ("cache miss/render", start_us, &second_pass_cache);
	return TRUE;
}

static void
prepare (GeglOperation *operation)
{
	const Babl *format = babl_format ("R'G'B'A float");

	gegl_operation_set_format (operation, "input", format);
	gegl_operation_set_format (operation, "output", format);
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
	const GeglRectangle *input_extent =
		gegl_operation_source_get_bounding_box (operation, input_pad);
	GeglRectangle required = { 0, 0, 0, 0 };

	(void)output_roi;
	if (input_extent == NULL)
	{
		return required;
	}

	return *input_extent;
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
	Gif320Vt320Options options;
	const GeglRectangle *extent = gegl_buffer_get_extent (input);
	GeglRectangle output_rect;
	GeglRectangle render_rect;
	Gif320Rect source;
	float *pixels;

	if (input == NULL || result == NULL || result->width <= 0 || result->height <= 0)
	{
		return FALSE;
	}

	fill_options (o, &options);
	source.x = extent->x;
	source.y = extent->y;
	source.width = extent->width;
	source.height = extent->height;
	output_rect = *extent;

	if (!gegl_rectangle_intersect (&render_rect, result, &output_rect))
	{
		return TRUE;
	}

	g_mutex_lock (&second_pass_cache_mutex);
	if (!ensure_second_pass_cache (
		input,
		&options,
		&source,
		&output_rect,
		o->terminal_width,
		o->terminal_height,
		level,
		format
	))
	{
		g_mutex_unlock (&second_pass_cache_mutex);
		return FALSE;
	}

	pixels = g_new0 (
		float,
		(gsize)render_rect.width * (gsize)render_rect.height * 4
	);
	if (pixels == NULL)
	{
		g_mutex_unlock (&second_pass_cache_mutex);
		return FALSE;
	}

	copy_tile_from_cache (&second_pass_cache, &render_rect, pixels);
	g_mutex_unlock (&second_pass_cache_mutex);

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
		"name", "gif320:vt320-second-pass",
		"title", "Gif320Sharp VT320 Second Pass",
		"categories", "artistic:light",
		"description",
			"Apply VT320 terminal-pixel scanline and phosphor shaping to a layer.",
		NULL
	);
}

#endif
