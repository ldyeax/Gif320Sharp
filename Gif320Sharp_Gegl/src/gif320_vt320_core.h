#ifndef GIF320_VT320_CORE_H
#define GIF320_VT320_CORE_H

#include <stdbool.h>

#define GIF320_CELL_PIXEL_WIDTH 15
#define GIF320_CELL_PIXEL_HEIGHT 12
#define GIF320_DISPLAY_CELL_ASPECT (4.0 / 11.0)
#define GIF320_DISPLAY_PIXEL_HEIGHT_SCALE \
	(GIF320_CELL_PIXEL_WIDTH / (GIF320_CELL_PIXEL_HEIGHT * GIF320_DISPLAY_CELL_ASPECT))
#define GIF320_CELL_PATTERN_BITS (GIF320_CELL_PIXEL_WIDTH * GIF320_CELL_PIXEL_HEIGHT)
#define GIF320_CELL_PATTERN_BYTES ((GIF320_CELL_PATTERN_BITS + 7) / 8)

typedef enum Gif320ResizeMode
{
	GIF320_RESIZE_STRETCH = 0,
	GIF320_RESIZE_CONTAIN = 1,
	GIF320_RESIZE_COVER = 2,
} Gif320ResizeMode;

typedef enum Gif320DitherMode
{
	GIF320_DITHER_THRESHOLD = 0,
	GIF320_DITHER_CHECKERBOARD = 1,
	GIF320_DITHER_FLOYD_STEINBERG = 2,
} Gif320DitherMode;

typedef enum Gif320SizeMode
{
	GIF320_SIZE_FIXED = 0,
	GIF320_SIZE_HEIGHT_FROM_WIDTH = 1,
	GIF320_SIZE_WIDTH_FROM_HEIGHT = 2,
	GIF320_SIZE_AUTO_ORIENTATION = 3,
} Gif320SizeMode;

typedef struct Gif320Rect
{
	int x;
	int y;
	int width;
	int height;
} Gif320Rect;

typedef struct Gif320Vt320Options
{
	Gif320SizeMode size_mode;
	int cells_x;
	int cells_y;
	int output_scale;

	Gif320ResizeMode resize_mode;
	Gif320DitherMode dither_mode;
	bool allow_glyph_reduction;
	int max_glyphs;

	double red_balance;
	double green_balance;
	double blue_balance;
	double full_threshold;
	double half_threshold;
	bool lock_red_balance;
	bool lock_green_balance;
	bool lock_blue_balance;
	bool lock_full_threshold;
	bool lock_half_threshold;
	bool auto_tune;
	int tune_frequency;
	int tune_smoothness;
	int tune_glyph_reuse;
	int reverse_video_tolerance;
	const char *manual_atlas;
	const char *manual_cell_map;

	double tint_red;
	double tint_green;
	double tint_blue;

	bool second_pass;
	double scanline_gap;
	double pixel_roundness;
	double roundness_aspect;
	bool hide_single_pixel;
	double glow;
} Gif320Vt320Options;

typedef void (*Gif320SampleFunc)(
	void *context,
	double x,
	double y,
	float rgba[4]
);

void gif320_vt320_options_init(Gif320Vt320Options *options);

void gif320_vt320_resolve_cells(
	const Gif320Vt320Options *options,
	const Gif320Rect *source,
	int *cells_x,
	int *cells_y
);

void gif320_vt320_resolve_preview_size(
	const Gif320Vt320Options *options,
	const Gif320Rect *source,
	int *width,
	int *height
);

bool gif320_vt320_render_preview(
	const Gif320Vt320Options *options,
	const Gif320Rect *source,
	Gif320SampleFunc sample,
	void *sample_context,
	float *rgba,
	int width,
	int height
);

bool gif320_vt320_render_preview_region(
	const Gif320Vt320Options *options,
	const Gif320Rect *source,
	Gif320SampleFunc sample,
	void *sample_context,
	float *rgba,
	int output_x,
	int output_y,
	int width,
	int height,
	int full_width,
	int full_height
);

void gif320_vt320_resolve_second_pass_cells(
	const Gif320Rect *source,
	int configured_cells_x,
	int configured_cells_y,
	int *cells_x,
	int *cells_y
);

bool gif320_vt320_render_second_pass_sampled(
	const Gif320Vt320Options *options,
	const Gif320Rect *source,
	int terminal_width,
	int terminal_height,
	Gif320SampleFunc sample,
	void *sample_context,
	float *rgba,
	int output_x,
	int output_y,
	int width,
	int height
);

#endif
