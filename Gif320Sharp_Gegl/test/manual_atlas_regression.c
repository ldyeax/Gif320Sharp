#include "gif320_vt320_core.h"

#include <math.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>

static void stroke_sample(
	void *context,
	double x,
	double y,
	float rgba[4]
)
{
	(void)context;
	(void)y;
	double cell_x = fmod(x, GIF320_CELL_PIXEL_WIDTH);
	bool on = fabs(cell_x - 7.0) < 1.0;
	rgba[0] = on ? 1.0f : 0.0f;
	rgba[1] = on ? 1.0f : 0.0f;
	rgba[2] = on ? 1.0f : 0.0f;
	rgba[3] = 1.0f;
}

static void black_sample(
	void *context,
	double x,
	double y,
	float rgba[4]
)
{
	(void)context;
	(void)x;
	(void)y;
	rgba[0] = 0.0f;
	rgba[1] = 0.0f;
	rgba[2] = 0.0f;
	rgba[3] = 1.0f;
}

static void configure_options(Gif320Vt320Options *options)
{
	gif320_vt320_options_init(options);
	options->size_mode = GIF320_SIZE_FIXED;
	options->cells_x = 4;
	options->cells_y = 1;
	options->output_scale = 1;
	options->resize_mode = GIF320_RESIZE_STRETCH;
	options->dither_mode = GIF320_DITHER_THRESHOLD;
	options->allow_glyph_reduction = true;
	options->max_glyphs = 4;
	options->auto_tune = false;
	options->full_threshold = 0.5;
	options->half_threshold = 0.25;
	options->tint_red = 1.0;
	options->tint_green = 1.0;
	options->tint_blue = 1.0;
	options->second_pass = false;
}

static float *render(
	const Gif320Vt320Options *options,
	Gif320SampleFunc sample,
	int *width,
	int *height
)
{
	Gif320Rect source = {
		0,
		0,
		options->cells_x * GIF320_CELL_PIXEL_WIDTH,
		options->cells_y * GIF320_CELL_PIXEL_HEIGHT
	};
	float *pixels;

	gif320_vt320_resolve_preview_size(options, &source, width, height);
	pixels = (float *)calloc((size_t)*width * (size_t)*height * 4, sizeof(float));
	if (pixels == NULL)
	{
		return NULL;
	}

	if (!gif320_vt320_render_preview(
		options,
		&source,
		sample,
		NULL,
		pixels,
		*width,
		*height
	))
	{
		free(pixels);
		return NULL;
	}

	return pixels;
}

static double average_rgb(
	const float *pixels,
	int width,
	int height,
	int x0,
	int x1
)
{
	double sum = 0.0;
	int count = 0;
	for (int y = 0; y < height; y++)
	{
		for (int x = x0; x < x1; x++)
		{
			int offset = (y * width + x) * 4;
			sum += pixels[offset] + pixels[offset + 1] + pixels[offset + 2];
			count += 3;
		}
	}

	return count == 0 ? 0.0 : sum / count;
}

static double average_rgb_diff(
	const float *left,
	const float *right,
	int width,
	int height
)
{
	double sum = 0.0;
	int count = width * height * 3;
	for (int i = 0; i < width * height; i++)
	{
		int offset = i * 4;
		sum += fabs(left[offset] - right[offset]);
		sum += fabs(left[offset + 1] - right[offset + 1]);
		sum += fabs(left[offset + 2] - right[offset + 2]);
	}

	return count == 0 ? 0.0 : sum / count;
}

static int fail(const char *message)
{
	fprintf(stderr, "%s\n", message);
	return 1;
}

int main(void)
{
	static const char *blank_full_atlas =
		"gif320-atlas-v1:"
		"0000000000000000000000000000000000000000000000,"
		"ffffffffffffffffffffffffffffffffffffffffffff0f";
	Gif320Vt320Options base;
	Gif320Vt320Options manual_without_map;
	Gif320Vt320Options manual_with_map;
	float *base_pixels;
	float *manual_pixels;
	float *mapped_pixels;
	int width;
	int height;
	int manual_width;
	int manual_height;
	int mapped_width;
	int mapped_height;
	double diff;
	double left_average;
	double right_average;

	configure_options(&base);
	base_pixels = render(&base, stroke_sample, &width, &height);
	if (base_pixels == NULL)
	{
		return fail("Could not render baseline preview.");
	}

	manual_without_map = base;
	manual_without_map.manual_atlas = blank_full_atlas;
	manual_without_map.manual_cell_map = "";
	manual_pixels = render(
		&manual_without_map,
		stroke_sample,
		&manual_width,
		&manual_height
	);
	if (manual_pixels == NULL)
	{
		free(base_pixels);
		return fail("Could not render manual-atlas preview without map.");
	}

	if (manual_width != width || manual_height != height)
	{
		free(base_pixels);
		free(manual_pixels);
		return fail("Manual-atlas preview changed output dimensions.");
	}

	diff = average_rgb_diff(base_pixels, manual_pixels, width, height);
	free(base_pixels);
	free(manual_pixels);
	if (diff > 0.000001)
	{
		fprintf(stderr, "Manual atlas without cell map changed unrelated output: %.8f\n", diff);
		return 1;
	}

	configure_options(&manual_with_map);
	manual_with_map.cells_x = 2;
	manual_with_map.manual_atlas = blank_full_atlas;
	manual_with_map.manual_cell_map = "gif320-map-v1:2x1:0201";
	mapped_pixels = render(&manual_with_map, black_sample, &mapped_width, &mapped_height);
	if (mapped_pixels == NULL)
	{
		return fail("Could not render manual-atlas preview with map.");
	}

	left_average = average_rgb(mapped_pixels, mapped_width, mapped_height, 0, mapped_width / 2);
	right_average = average_rgb(
		mapped_pixels,
		mapped_width,
		mapped_height,
		mapped_width / 2,
		mapped_width
	);
	free(mapped_pixels);
	if (left_average < 0.95 || right_average > 0.05)
	{
		fprintf(
			stderr,
			"Manual cell map was not honored: left %.4f right %.4f\n",
			left_average,
			right_average
		);
		return 1;
	}

	return 0;
}
