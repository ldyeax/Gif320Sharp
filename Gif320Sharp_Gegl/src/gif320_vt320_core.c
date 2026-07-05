#include "gif320_vt320_core.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

static int clamp_int(int value, int minimum, int maximum)
{
	if (value < minimum)
	{
		return minimum;
	}

	return value > maximum ? maximum : value;
}

static double clamp_double(double value, double minimum, double maximum)
{
	if (value < minimum)
	{
		return minimum;
	}

	return value > maximum ? maximum : value;
}

static double lerp(double a, double b, double t)
{
	return a + (b - a) * t;
}

static int derive_rows_from_columns(
	int columns,
	double source_width,
	double source_height
)
{
	return (int)floor(
		columns
			* GIF320_DISPLAY_CELL_ASPECT
			* source_height
			/ source_width
			+ 0.5
	);
}

static int derive_columns_from_rows(
	int rows,
	double source_width,
	double source_height
)
{
	return (int)floor(
		rows
			* source_width
			/ (source_height * GIF320_DISPLAY_CELL_ASPECT)
			+ 0.5
	);
}

static double smoothstep(double edge0, double edge1, double x)
{
	double t;
	if (fabs(edge1 - edge0) < 0.000001)
	{
		return x < edge0 ? 0.0 : 1.0;
	}

	t = clamp_double((x - edge0) / (edge1 - edge0), 0.0, 1.0);
	return t * t * (3.0 - 2.0 * t);
}

static double pattern_bit(const unsigned char *bits, int x, int y, int width, int height)
{
	if (x < 0 || y < 0 || x >= width || y >= height)
	{
		return 0.0;
	}

	return bits[y * width + x] ? 1.0 : 0.0;
}

static double rgba_intensity(
	const Gif320Vt320Options *options,
	const float rgba[4]
)
{
	double red = clamp_double(rgba[0], 0.0, 1.0);
	double green = clamp_double(rgba[1], 0.0, 1.0);
	double blue = clamp_double(rgba[2], 0.0, 1.0);
	double alpha = clamp_double(rgba[3], 0.0, 1.0);
	double red_weight = options->red_balance;
	double green_weight = options->green_balance;
	double blue_weight = options->blue_balance;
	double total = red_weight + green_weight + blue_weight;

	if (total <= 0.0)
	{
		red_weight = 0.2126;
		green_weight = 0.7152;
		blue_weight = 0.0722;
		total = 1.0;
	}

	red *= alpha;
	green *= alpha;
	blue *= alpha;
	return clamp_double(
		(red * red_weight + green * green_weight + blue * blue_weight) / total,
		0.0,
		1.0
	);
}

static void sample_source_for_terminal_pixel(
	const Gif320Vt320Options *options,
	const Gif320Rect *source,
	Gif320SampleFunc sample,
	void *sample_context,
	int terminal_x,
	int terminal_y,
	int terminal_width,
	int terminal_height,
	float rgba[4]
)
{
	double display_terminal_height = terminal_height
		* GIF320_DISPLAY_PIXEL_HEIGHT_SCALE;
	double scale_x = (double)terminal_width / (double)source->width;
	double scale_y = display_terminal_height / (double)source->height;
	double scale;
	double scaled_width;
	double scaled_height;
	double offset_x;
	double offset_y;
	double display_y;
	double source_x;
	double source_y;

	rgba[0] = 0.0f;
	rgba[1] = 0.0f;
	rgba[2] = 0.0f;
	rgba[3] = 1.0f;

	if (source->width <= 0 || source->height <= 0)
	{
		return;
	}

	if (options->resize_mode == GIF320_RESIZE_STRETCH)
	{
		source_x = source->x
			+ ((terminal_x + 0.5) * source->width / terminal_width);
		source_y = source->y
			+ ((terminal_y + 0.5) * source->height / terminal_height);
	}
	else
	{
		scale = options->resize_mode == GIF320_RESIZE_CONTAIN
			? fmin(scale_x, scale_y)
			: fmax(scale_x, scale_y);
		if (scale <= 0.0)
		{
			return;
		}

		scaled_width = source->width * scale;
		scaled_height = source->height * scale;
		offset_x = (terminal_width - scaled_width) * 0.5;
		offset_y = (display_terminal_height - scaled_height) * 0.5;
		display_y = (terminal_y + 0.5) * GIF320_DISPLAY_PIXEL_HEIGHT_SCALE;
		source_x = source->x + ((terminal_x + 0.5 - offset_x) / scale);
		source_y = source->y + ((display_y - offset_y) / scale);
	}

	if (source_x < source->x
		|| source_y < source->y
		|| source_x >= source->x + source->width
		|| source_y >= source->y + source->height)
	{
		return;
	}

	sample(sample_context, source_x, source_y, rgba);
}

static void smooth_or_sharpen(
	const Gif320Vt320Options *options,
	double *intensity,
	int width,
	int height
)
{
	double amount = clamp_double(options->tune_smoothness / 100.0, -1.0, 1.0);
	double *copy;

	if (fabs(amount) < 0.001)
	{
		return;
	}

	copy = (double *)malloc((size_t)width * (size_t)height * sizeof(double));
	if (copy == NULL)
	{
		return;
	}

	memcpy(copy, intensity, (size_t)width * (size_t)height * sizeof(double));
	for (int y = 0; y < height; y++)
	{
		for (int x = 0; x < width; x++)
		{
			int index = y * width + x;
			double center = copy[index];
			double sum = center * 4.0;
			double count = 4.0;

			if (x > 0)
			{
				sum += copy[index - 1];
				count += 1.0;
			}

			if (x + 1 < width)
			{
				sum += copy[index + 1];
				count += 1.0;
			}

			if (y > 0)
			{
				sum += copy[index - width];
				count += 1.0;
			}

			if (y + 1 < height)
			{
				sum += copy[index + width];
				count += 1.0;
			}

			double blurred = sum / count;
			if (amount > 0.0)
			{
				intensity[index] = lerp(center, blurred, amount * 0.85);
			}
			else
			{
				intensity[index] = clamp_double(center + (center - blurred) * -amount, 0.0, 1.0);
			}
		}
	}

	free(copy);
}

static void dither_threshold(
	const Gif320Vt320Options *options,
	const double *intensity,
	unsigned char *bits,
	int width,
	int height
)
{
	double threshold = clamp_double(options->full_threshold, 0.0, 1.0);

	for (int i = 0; i < width * height; i++)
	{
		bits[i] = intensity[i] >= threshold ? 1 : 0;
	}
}

static void dither_checkerboard(
	const Gif320Vt320Options *options,
	const double *intensity,
	unsigned char *bits,
	int width,
	int height
)
{
	double full_threshold = clamp_double(options->full_threshold, 0.0, 1.0);
	double half_threshold = clamp_double(options->half_threshold, 0.0, full_threshold);
	double frequency = clamp_double(options->tune_frequency / 100.0, -1.0, 1.0);
	double strength = clamp_double(full_threshold - half_threshold, 0.02, 0.45);

	strength *= lerp(0.35, 1.35, (frequency + 1.0) * 0.5);
	for (int y = 0; y < height; y++)
	{
		for (int x = 0; x < width; x++)
		{
			int index = y * width + x;
			double checker = ((x ^ y) & 1) ? strength : -strength;
			bits[index] = intensity[index] + checker >= full_threshold ? 1 : 0;
		}
	}
}

static void dither_floyd_steinberg(
	const Gif320Vt320Options *options,
	const double *intensity,
	unsigned char *bits,
	int width,
	int height
)
{
	double threshold = clamp_double(options->full_threshold, 0.0, 1.0);
	double *work = (double *)malloc((size_t)width * (size_t)height * sizeof(double));

	if (work == NULL)
	{
		dither_checkerboard(options, intensity, bits, width, height);
		return;
	}

	memcpy(work, intensity, (size_t)width * (size_t)height * sizeof(double));
	for (int y = 0; y < height; y++)
	{
		for (int x = 0; x < width; x++)
		{
			int index = y * width + x;
			double old_value = work[index];
			double new_value = old_value >= threshold ? 1.0 : 0.0;
			double error = old_value - new_value;

			bits[index] = new_value > 0.5 ? 1 : 0;
			if (x + 1 < width)
			{
				work[index + 1] += error * 7.0 / 16.0;
			}

			if (y + 1 < height)
			{
				if (x > 0)
				{
					work[index + width - 1] += error * 3.0 / 16.0;
				}

				work[index + width] += error * 5.0 / 16.0;
				if (x + 1 < width)
				{
					work[index + width + 1] += error * 1.0 / 16.0;
				}
			}
		}
	}

	free(work);
}

static void dither(
	const Gif320Vt320Options *options,
	const double *intensity,
	unsigned char *bits,
	int width,
	int height
)
{
	switch (options->dither_mode)
	{
		case GIF320_DITHER_THRESHOLD:
			dither_threshold(options, intensity, bits, width, height);
			break;
		case GIF320_DITHER_FLOYD_STEINBERG:
			dither_floyd_steinberg(options, intensity, bits, width, height);
			break;
		case GIF320_DITHER_CHECKERBOARD:
		default:
			dither_checkerboard(options, intensity, bits, width, height);
			break;
	}
}

static void read_cell_pattern(
	const unsigned char *bits,
	int terminal_width,
	int cell_x,
	int cell_y,
	unsigned char pattern[GIF320_CELL_PATTERN_BYTES]
)
{
	memset(pattern, 0, GIF320_CELL_PATTERN_BYTES);
	for (int y = 0; y < GIF320_CELL_PIXEL_HEIGHT; y++)
	{
		for (int x = 0; x < GIF320_CELL_PIXEL_WIDTH; x++)
		{
			int bit = y * GIF320_CELL_PIXEL_WIDTH + x;
			int source = (cell_y * GIF320_CELL_PIXEL_HEIGHT + y) * terminal_width
				+ cell_x * GIF320_CELL_PIXEL_WIDTH
				+ x;
			if (bits[source] != 0)
			{
				pattern[bit / 8] |= (unsigned char)(1U << (bit % 8));
			}
		}
	}
}

static bool pattern_is_blank(const unsigned char pattern[GIF320_CELL_PATTERN_BYTES])
{
	for (int i = 0; i < GIF320_CELL_PATTERN_BYTES; i++)
	{
		if (pattern[i] != 0)
		{
			return false;
		}
	}

	return true;
}

static bool pattern_is_full(const unsigned char pattern[GIF320_CELL_PATTERN_BYTES])
{
	for (int bit = 0; bit < GIF320_CELL_PATTERN_BITS; bit++)
	{
		if ((pattern[bit / 8] & (1U << (bit % 8))) == 0)
		{
			return false;
		}
	}

	return true;
}

static int pattern_distance(
	const unsigned char left[GIF320_CELL_PATTERN_BYTES],
	const unsigned char right[GIF320_CELL_PATTERN_BYTES],
	bool inverted
)
{
	int distance = 0;

	for (int bit = 0; bit < GIF320_CELL_PATTERN_BITS; bit++)
	{
		bool left_on = (left[bit / 8] & (1U << (bit % 8))) != 0;
		bool right_on = (right[bit / 8] & (1U << (bit % 8))) != 0;
		if (inverted)
		{
			right_on = !right_on;
		}

		if (left_on != right_on)
		{
			distance++;
		}
	}

	return distance;
}

static int hex_value(int value)
{
	if (value >= '0' && value <= '9')
	{
		return value - '0';
	}

	if (value >= 'a' && value <= 'f')
	{
		return value - 'a' + 10;
	}

	if (value >= 'A' && value <= 'F')
	{
		return value - 'A' + 10;
	}

	return -1;
}

static int lower_ascii(int value)
{
	return value >= 'A' && value <= 'Z' ? value + ('a' - 'A') : value;
}

static bool starts_with_ignore_case(const char *text, const char *prefix)
{
	for (int i = 0; prefix[i] != '\0'; i++)
	{
		if (text[i] == '\0'
			|| lower_ascii((unsigned char)text[i])
				!= lower_ascii((unsigned char)prefix[i]))
		{
			return false;
		}
	}

	return true;
}

static bool parse_manual_atlas(
	const char *text,
	unsigned char **patterns,
	int *pattern_count
)
{
	static const char prefix[] = "gif320-atlas-v1:";
	size_t prefix_length = sizeof(prefix) - 1;
	unsigned char *parsed;
	char token[GIF320_CELL_PATTERN_BYTES * 2];
	int token_length = 0;
	int count = 0;

	*patterns = NULL;
	*pattern_count = 0;
	if (text == NULL || text[0] == '\0')
	{
		return true;
	}

	if (starts_with_ignore_case(text, prefix))
	{
		text += prefix_length;
	}

	parsed = (unsigned char *)calloc(
		94,
		GIF320_CELL_PATTERN_BYTES
	);
	if (parsed == NULL)
	{
		return false;
	}

	for (;; text++)
	{
		int value = hex_value((unsigned char)*text);
		if (value >= 0)
		{
			if (token_length >= (int)sizeof(token))
			{
				free(parsed);
				return false;
			}

			token[token_length++] = *text;
			continue;
		}

		if (*text == ',' || *text == ';' || *text == '\0'
			|| *text == ' ' || *text == '\t' || *text == '\r' || *text == '\n')
		{
			if (token_length > 0)
			{
				if (token_length != (int)sizeof(token) || count >= 94)
				{
					free(parsed);
					return false;
				}

				for (int byte_index = 0; byte_index < GIF320_CELL_PATTERN_BYTES; byte_index++)
				{
					int high = hex_value(token[byte_index * 2]);
					int low = hex_value(token[byte_index * 2 + 1]);
					parsed[count * GIF320_CELL_PATTERN_BYTES + byte_index] =
						(unsigned char)((high << 4) | low);
				}

				count++;
				token_length = 0;
			}

			if (*text == '\0')
			{
				break;
			}

			continue;
		}

		free(parsed);
		return false;
	}

	*patterns = parsed;
	*pattern_count = count;
	return true;
}

static bool parse_manual_cell_map(
	const char *text,
	int cells_x,
	int cells_y,
	unsigned char **cell_map
)
{
	static const char prefix[] = "gif320-map-v1:";
	size_t prefix_length = sizeof(prefix) - 1;
	int parsed_cells_x;
	int parsed_cells_y;
	int cell_count = cells_x * cells_y;
	unsigned char *parsed;
	char *end;

	*cell_map = NULL;
	if (text == NULL || text[0] == '\0')
	{
		return true;
	}

	if (!starts_with_ignore_case(text, prefix))
	{
		return false;
	}

	text += prefix_length;
	parsed_cells_x = (int)strtol(text, &end, 10);
	if (end == text || *end != 'x')
	{
		return false;
	}

	text = end + 1;
	parsed_cells_y = (int)strtol(text, &end, 10);
	if (end == text || *end != ':')
	{
		return false;
	}

	if (parsed_cells_x != cells_x || parsed_cells_y != cells_y)
	{
		return true;
	}

	text = end + 1;
	if (strlen(text) != (size_t)cell_count * 2)
	{
		return false;
	}

	parsed = (unsigned char *)malloc((size_t)cell_count);
	if (parsed == NULL)
	{
		return false;
	}

	for (int i = 0; i < cell_count; i++)
	{
		int high = hex_value((unsigned char)text[i * 2]);
		int low = hex_value((unsigned char)text[i * 2 + 1]);
		if (high < 0 || low < 0)
		{
			free(parsed);
			return false;
		}

		parsed[i] = (unsigned char)((high << 4) | low);
	}

	if (text[cell_count * 2] != '\0')
	{
		free(parsed);
		return false;
	}

	*cell_map = parsed;
	return true;
}

static void write_cell_pattern(
	unsigned char *bits,
	int terminal_width,
	int cell_x,
	int cell_y,
	const unsigned char pattern[GIF320_CELL_PATTERN_BYTES],
	bool inverted
)
{
	for (int y = 0; y < GIF320_CELL_PIXEL_HEIGHT; y++)
	{
		for (int x = 0; x < GIF320_CELL_PIXEL_WIDTH; x++)
		{
			int bit = y * GIF320_CELL_PIXEL_WIDTH + x;
			int target = (cell_y * GIF320_CELL_PIXEL_HEIGHT + y) * terminal_width
				+ cell_x * GIF320_CELL_PIXEL_WIDTH
				+ x;
			bool on = (pattern[bit / 8] & (1U << (bit % 8))) != 0;
			bits[target] = (unsigned char)((inverted ? !on : on) ? 1 : 0);
		}
	}
}

static void render_manual_cell_map(
	unsigned char *target_bits,
	int cells_x,
	int cells_y,
	int terminal_width,
	const unsigned char *manual_patterns,
	int manual_pattern_count,
	const unsigned char *cell_map
)
{
	unsigned char blank[GIF320_CELL_PATTERN_BYTES];
	memset(blank, 0, sizeof(blank));
	memset(
		target_bits,
		0,
		(size_t)cells_x
			* GIF320_CELL_PIXEL_WIDTH
			* cells_y
			* GIF320_CELL_PIXEL_HEIGHT
	);

	for (int cell = 0; cell < cells_x * cells_y; cell++)
	{
		int cell_x = cell % cells_x;
		int cell_y = cell / cells_x;
		unsigned char mapped = cell_map[cell];
		int glyph_code = mapped & 0x7f;
		bool inverted = (mapped & 0x80) != 0;
		const unsigned char *pattern;

		if (glyph_code == 0)
		{
			if (inverted)
			{
				write_cell_pattern(
					target_bits,
					terminal_width,
					cell_x,
					cell_y,
					blank,
					true
				);
			}

			continue;
		}

		if (glyph_code > manual_pattern_count)
		{
			continue;
		}

		pattern = manual_patterns + (glyph_code - 1) * GIF320_CELL_PATTERN_BYTES;
		write_cell_pattern(
			target_bits,
			terminal_width,
			cell_x,
			cell_y,
			pattern,
			inverted
		);
	}
}

static int effective_glyph_limit(const Gif320Vt320Options *options)
{
	int max_glyphs = clamp_int(options->max_glyphs, 1, 94);
	double reuse = clamp_double(options->tune_glyph_reuse / 100.0, -1.0, 1.0);

	if (!options->allow_glyph_reduction)
	{
		return 32767;
	}

	if (reuse > 0.0)
	{
		max_glyphs = clamp_int((int)floor(max_glyphs * (1.0 - reuse * 0.45)), 1, 94);
	}

	return max_glyphs;
}

static int find_unique_pattern(
	const unsigned char *unique_patterns,
	int unique_count,
	const unsigned char pattern[GIF320_CELL_PATTERN_BYTES]
)
{
	for (int i = 0; i < unique_count; i++)
	{
		const unsigned char *candidate = unique_patterns
			+ i * GIF320_CELL_PATTERN_BYTES;
		if (memcmp(candidate, pattern, GIF320_CELL_PATTERN_BYTES) == 0)
		{
			return i;
		}
	}

	return -1;
}

static int best_pattern_distance(
	const unsigned char pattern[GIF320_CELL_PATTERN_BYTES],
	const unsigned char prototype[GIF320_CELL_PATTERN_BYTES],
	bool *inverted
)
{
	int normal = pattern_distance(pattern, prototype, false);
	int reverse = pattern_distance(pattern, prototype, true);

	if (reverse < normal)
	{
		*inverted = true;
		return reverse;
	}

	*inverted = false;
	return normal;
}

static void select_global_prototypes(
	const unsigned char *unique_patterns,
	const int *unique_weights,
	int unique_count,
	unsigned char *prototypes,
	int max_glyphs,
	int *prototype_count
)
{
	bool *selected = (bool *)calloc((size_t)unique_count, sizeof(bool));
	int *nearest = (int *)malloc((size_t)unique_count * sizeof(int));

	*prototype_count = 0;
	if (selected == NULL || nearest == NULL || unique_count <= 0 || max_glyphs <= 0)
	{
		free(selected);
		free(nearest);
		return;
	}

	for (int i = 0; i < unique_count; i++)
	{
		nearest[i] = GIF320_CELL_PATTERN_BITS + 1;
	}

	while (*prototype_count < max_glyphs)
	{
		int selected_index = -1;
		double selected_score = -1.0;

		for (int i = 0; i < unique_count; i++)
		{
			if (selected[i])
			{
				continue;
			}

			double distance = nearest[i] > GIF320_CELL_PATTERN_BITS
				? GIF320_CELL_PATTERN_BITS
				: nearest[i];
			double normalized = distance / (double)GIF320_CELL_PATTERN_BITS;
			double score = normalized
				+ normalized * normalized * 1.75
				+ log(unique_weights[i] + 1.0) * 0.03;

			if (selected_index < 0 || score > selected_score)
			{
				selected_index = i;
				selected_score = score;
			}
		}

		if (selected_index < 0)
		{
			break;
		}

		memcpy(
			prototypes + *prototype_count * GIF320_CELL_PATTERN_BYTES,
			unique_patterns + selected_index * GIF320_CELL_PATTERN_BYTES,
			GIF320_CELL_PATTERN_BYTES
		);
		selected[selected_index] = true;
		(*prototype_count)++;

		for (int i = 0; i < unique_count; i++)
		{
			bool inverted;
			int distance = best_pattern_distance(
				unique_patterns + i * GIF320_CELL_PATTERN_BYTES,
				unique_patterns + selected_index * GIF320_CELL_PATTERN_BYTES,
				&inverted
			);
			if (distance < nearest[i])
			{
				nearest[i] = distance;
			}
		}
	}

	free(selected);
	free(nearest);
}

static void assign_pattern_to_prototypes(
	const unsigned char pattern[GIF320_CELL_PATTERN_BYTES],
	const unsigned char *prototypes,
	int prototype_count,
	int *chosen,
	bool *inverted
)
{
	int best_distance = GIF320_CELL_PATTERN_BITS + 1;

	*chosen = -1;
	*inverted = false;
	for (int i = 0; i < prototype_count; i++)
	{
		bool candidate_inverted;
		int distance = best_pattern_distance(
			pattern,
			prototypes + i * GIF320_CELL_PATTERN_BYTES,
			&candidate_inverted
		);
		if (distance < best_distance)
		{
			best_distance = distance;
			*chosen = i;
			*inverted = candidate_inverted;
			if (distance == 0)
			{
				break;
			}
		}
	}
}

static void map_manual_patterns_to_prototypes(
	const unsigned char *prototypes,
	int prototype_count,
	const unsigned char *manual_patterns,
	int manual_pattern_count,
	unsigned char *mapped_patterns
)
{
	bool *prototype_mapped = (bool *)calloc((size_t)prototype_count, sizeof(bool));
	bool *manual_used = (bool *)calloc((size_t)manual_pattern_count, sizeof(bool));

	if (prototype_mapped == NULL || manual_used == NULL)
	{
		memcpy(
			mapped_patterns,
			prototypes,
			(size_t)prototype_count * GIF320_CELL_PATTERN_BYTES
		);
		free(prototype_mapped);
		free(manual_used);
		return;
	}

	for (int prototype_index = 0; prototype_index < prototype_count; prototype_index++)
	{
		const unsigned char *prototype = prototypes
			+ prototype_index * GIF320_CELL_PATTERN_BYTES;
		for (int manual_index = 0; manual_index < manual_pattern_count; manual_index++)
		{
			const unsigned char *manual = manual_patterns
				+ manual_index * GIF320_CELL_PATTERN_BYTES;
			if (manual_used[manual_index]
				|| memcmp(prototype, manual, GIF320_CELL_PATTERN_BYTES) != 0)
			{
				continue;
			}

			memcpy(
				mapped_patterns + prototype_index * GIF320_CELL_PATTERN_BYTES,
				manual,
				GIF320_CELL_PATTERN_BYTES
			);
			prototype_mapped[prototype_index] = true;
			manual_used[manual_index] = true;
			break;
		}
	}

	for (int prototype_index = 0; prototype_index < prototype_count; prototype_index++)
	{
		const unsigned char *prototype = prototypes
			+ prototype_index * GIF320_CELL_PATTERN_BYTES;
		int best_manual_index = -1;
		int best_distance = GIF320_CELL_PATTERN_BITS + 1;

		if (prototype_mapped[prototype_index])
		{
			continue;
		}

		for (int manual_index = 0; manual_index < manual_pattern_count; manual_index++)
		{
			const unsigned char *manual = manual_patterns
				+ manual_index * GIF320_CELL_PATTERN_BYTES;
			int distance;
			if (manual_used[manual_index])
			{
				continue;
			}

			distance = pattern_distance(prototype, manual, false);
			if (distance < best_distance)
			{
				best_distance = distance;
				best_manual_index = manual_index;
			}
		}

		if (best_manual_index >= 0)
		{
			memcpy(
				mapped_patterns + prototype_index * GIF320_CELL_PATTERN_BYTES,
				manual_patterns + best_manual_index * GIF320_CELL_PATTERN_BYTES,
				GIF320_CELL_PATTERN_BYTES
			);
			manual_used[best_manual_index] = true;
		}
		else
		{
			memcpy(
				mapped_patterns + prototype_index * GIF320_CELL_PATTERN_BYTES,
				prototype,
				GIF320_CELL_PATTERN_BYTES
			);
		}
	}

	free(prototype_mapped);
	free(manual_used);
}

static bool reduce_cells_to_glyph_budget(
	const Gif320Vt320Options *options,
	const unsigned char *source_bits,
	unsigned char *target_bits,
	int cells_x,
	int cells_y,
	int terminal_width
)
{
	int cell_count = cells_x * cells_y;
	int max_glyphs = effective_glyph_limit(options);
	unsigned char *unique_patterns = (unsigned char *)calloc(
		(size_t)cell_count,
		GIF320_CELL_PATTERN_BYTES
	);
	int *unique_weights = (int *)calloc((size_t)cell_count, sizeof(int));
	int *cell_unique = (int *)malloc((size_t)cell_count * sizeof(int));
	unsigned char *prototypes = (unsigned char *)calloc(
		(size_t)max_glyphs,
		GIF320_CELL_PATTERN_BYTES
	);
	unsigned char *manual_patterns = NULL;
	unsigned char *manual_cell_map = NULL;
	int manual_pattern_count = 0;
	int prototype_count = 0;
	int unique_count = 0;
	unsigned char *mapped_manual_patterns = NULL;
	const unsigned char *output_patterns;
	unsigned char pattern[GIF320_CELL_PATTERN_BYTES];

	if (unique_patterns == NULL
		|| unique_weights == NULL
		|| cell_unique == NULL
		|| prototypes == NULL)
	{
		memcpy(
			target_bits,
			source_bits,
			(size_t)cells_x
				* GIF320_CELL_PIXEL_WIDTH
				* cells_y
				* GIF320_CELL_PIXEL_HEIGHT
		);
		free(unique_patterns);
		free(unique_weights);
		free(cell_unique);
		free(prototypes);
		return false;
	}

	if (!parse_manual_atlas(
		options->manual_atlas,
		&manual_patterns,
		&manual_pattern_count
	))
	{
		manual_pattern_count = 0;
	}

	if (!parse_manual_cell_map(
		options->manual_cell_map,
		cells_x,
		cells_y,
		&manual_cell_map
	))
	{
		free(manual_patterns);
		manual_patterns = NULL;
		manual_pattern_count = 0;
	}

	if (manual_patterns != NULL
		&& manual_pattern_count > 0
		&& manual_cell_map != NULL)
	{
		render_manual_cell_map(
			target_bits,
			cells_x,
			cells_y,
			terminal_width,
			manual_patterns,
			manual_pattern_count,
			manual_cell_map
		);
		free(unique_patterns);
		free(unique_weights);
		free(cell_unique);
		free(prototypes);
		free(manual_patterns);
		free(manual_cell_map);
		return true;
	}

	if (manual_patterns != NULL)
	{
		free(manual_patterns);
		manual_patterns = NULL;
		manual_pattern_count = 0;
	}

	memset(
		target_bits,
		0,
		(size_t)cells_x
			* GIF320_CELL_PIXEL_WIDTH
			* cells_y
			* GIF320_CELL_PIXEL_HEIGHT
	);

	for (int cell = 0; cell < cell_count; cell++)
	{
		int cell_x = cell % cells_x;
		int cell_y = cell / cells_x;
		int unique_index;

		read_cell_pattern(source_bits, terminal_width, cell_x, cell_y, pattern);

		if (pattern_is_blank(pattern))
		{
			cell_unique[cell] = -1;
			continue;
		}

		if (pattern_is_full(pattern))
		{
			for (int i = 0; i < GIF320_CELL_PATTERN_BYTES; i++)
			{
				pattern[i] = 0;
			}

			write_cell_pattern(target_bits, terminal_width, cell_x, cell_y, pattern, true);
			cell_unique[cell] = -1;
			continue;
		}

		unique_index = find_unique_pattern(unique_patterns, unique_count, pattern);
		if (unique_index < 0)
		{
			unique_index = unique_count;
			memcpy(
				unique_patterns + unique_index * GIF320_CELL_PATTERN_BYTES,
				pattern,
				GIF320_CELL_PATTERN_BYTES
			);
			unique_count++;
		}

		unique_weights[unique_index]++;
		cell_unique[cell] = unique_index;
	}

	select_global_prototypes(
		unique_patterns,
		unique_weights,
		unique_count,
		prototypes,
		max_glyphs,
		&prototype_count
	);

	output_patterns = prototypes;
	if (manual_pattern_count > 0 && prototype_count > 0)
	{
		mapped_manual_patterns = (unsigned char *)calloc(
			(size_t)prototype_count,
			GIF320_CELL_PATTERN_BYTES
		);
		if (mapped_manual_patterns != NULL)
		{
			map_manual_patterns_to_prototypes(
				prototypes,
				prototype_count,
				manual_patterns,
				manual_pattern_count,
				mapped_manual_patterns
			);
			output_patterns = mapped_manual_patterns;
		}
	}

	for (int cell = 0; cell < cell_count; cell++)
	{
		int cell_x = cell % cells_x;
		int cell_y = cell / cells_x;
		int unique_index = cell_unique[cell];
		int chosen = -1;
		bool inverted = false;
		const unsigned char *output_pattern;

		if (unique_index < 0)
		{
			continue;
		}

		assign_pattern_to_prototypes(
			unique_patterns + unique_index * GIF320_CELL_PATTERN_BYTES,
			prototypes,
			prototype_count,
			&chosen,
			&inverted
		);
		if (chosen < 0)
		{
			continue;
		}

		output_pattern = output_patterns + chosen * GIF320_CELL_PATTERN_BYTES;
		write_cell_pattern(
			target_bits,
			terminal_width,
			cell_x,
			cell_y,
			output_pattern,
			inverted
		);
	}

	free(unique_patterns);
	free(unique_weights);
	free(cell_unique);
	free(prototypes);
	free(mapped_manual_patterns);
	free(manual_patterns);
	free(manual_cell_map);
	return true;
}

static bool has_lit_neighbor(
	const unsigned char *bits,
	int terminal_width,
	int terminal_height,
	int x,
	int y
)
{
	for (int yy = -1; yy <= 1; yy++)
	{
		for (int xx = -1; xx <= 1; xx++)
		{
			if (xx == 0 && yy == 0)
			{
				continue;
			}

			if (pattern_bit(bits, x + xx, y + yy, terminal_width, terminal_height) > 0.0)
			{
				return true;
			}
		}
	}

	return false;
}

static double shape_terminal_pixel(
	const Gif320Vt320Options *options,
	double current,
	double left,
	double right,
	bool has_neighbor,
	double local_x,
	double local_y
)
{
	double intensity = current;
	double scanline_gap = clamp_double(options->scanline_gap, 0.0, 1.0);
	double roundness = clamp_double(options->pixel_roundness, 0.0, 2.0);

	if (intensity <= 0.00001)
	{
		return 0.0;
	}

	if (scanline_gap > 0.0)
	{
		double scanline_mask = smoothstep(
			0.0,
			fmax(0.00001, 1.0 - scanline_gap),
			fabs(0.5 - local_y) * 2.0
		);
		intensity *= 1.0 - scanline_mask;
	}

	if (roundness > 0.001)
	{
		double aspect = fmax(options->roundness_aspect, 0.00001);
		double vertical_distance = fabs(local_y - 0.5) / aspect;
		double left_distance = sqrt(
			local_x * local_x
				+ ((local_y - 0.5) / aspect) * ((local_y - 0.5) / aspect)
		);
		double right_distance = sqrt(
			(local_x - 1.0) * (local_x - 1.0)
				+ ((local_y - 0.5) / aspect) * ((local_y - 0.5) / aspect)
		);
		double dot_distance = sqrt(
			((local_x - 0.5) * 2.0) * ((local_x - 0.5) * 2.0)
				+ ((local_y - 0.5) / aspect) * ((local_y - 0.5) / aspect)
		);
		double edge_distance;

		if (left > 0.00001 && right > 0.00001)
		{
			edge_distance = vertical_distance;
		}
		else if (left > 0.00001)
		{
			edge_distance = left_distance;
		}
		else if (right > 0.00001)
		{
			edge_distance = right_distance;
		}
		else
		{
			if (options->hide_single_pixel && !has_neighbor)
			{
				return 0.0;
			}

			edge_distance = dot_distance;
		}

		intensity *= lerp(1.0, clamp_double(1.0 - edge_distance, 0.0, 1.0), fmin(roundness, 1.0));
	}

	return clamp_double(intensity, 0.0, 1.0);
}

static double shaped_bit_intensity(
	const Gif320Vt320Options *options,
	const unsigned char *bits,
	int terminal_width,
	int terminal_height,
	int terminal_x,
	int terminal_y,
	double local_x,
	double local_y
)
{
	double current = pattern_bit(bits, terminal_x, terminal_y, terminal_width, terminal_height);
	double left = pattern_bit(bits, terminal_x - 1, terminal_y, terminal_width, terminal_height);
	double right = pattern_bit(bits, terminal_x + 1, terminal_y, terminal_width, terminal_height);
	bool neighbor = has_lit_neighbor(bits, terminal_width, terminal_height, terminal_x, terminal_y);
	double intensity = shape_terminal_pixel(
		options,
		current,
		left,
		right,
		neighbor,
		local_x,
		local_y
	);

	if (options->glow > 0.0)
	{
		double glow = 0.0;
		for (int yy = -1; yy <= 1; yy++)
		{
			for (int xx = -1; xx <= 1; xx++)
			{
				if (xx == 0 && yy == 0)
				{
					continue;
				}

				glow = fmax(
					glow,
					pattern_bit(
						bits,
						terminal_x + xx,
						terminal_y + yy,
						terminal_width,
						terminal_height
					)
				);
			}
		}

		intensity = clamp_double(
			intensity + glow * clamp_double(options->glow, 0.0, 1.0) * 0.25,
			0.0,
			1.0
		);
	}

	return intensity;
}

static void write_tinted_pixel(
	const Gif320Vt320Options *options,
	float *rgba,
	int index,
	double intensity
)
{
	rgba[index] = (float)(clamp_double(options->tint_red, 0.0, 1.0) * intensity);
	rgba[index + 1] = (float)(clamp_double(options->tint_green, 0.0, 1.0) * intensity);
	rgba[index + 2] = (float)(clamp_double(options->tint_blue, 0.0, 1.0) * intensity);
	rgba[index + 3] = 1.0f;
}

void gif320_vt320_options_init(Gif320Vt320Options *options)
{
	memset(options, 0, sizeof(*options));
	options->size_mode = GIF320_SIZE_AUTO_ORIENTATION;
	options->cells_x = 80;
	options->cells_y = 24;
	options->output_scale = 2;
	options->resize_mode = GIF320_RESIZE_COVER;
	options->dither_mode = GIF320_DITHER_CHECKERBOARD;
	options->allow_glyph_reduction = true;
	options->max_glyphs = 94;
	options->red_balance = 30.0;
	options->green_balance = 40.0;
	options->blue_balance = 10.0;
	options->full_threshold = 0.50;
	options->half_threshold = 0.25;
	options->lock_red_balance = false;
	options->lock_green_balance = false;
	options->lock_blue_balance = false;
	options->lock_full_threshold = false;
	options->lock_half_threshold = false;
	options->auto_tune = true;
	options->reverse_video_tolerance = 4;
	options->manual_atlas = "";
	options->manual_cell_map = "";
	options->tint_red = 1.0;
	options->tint_green = 191.0 / 255.0;
	options->tint_blue = 0.0;
	options->second_pass = false;
	options->scanline_gap = 0.15;
	options->pixel_roundness = 0.85;
	options->roundness_aspect = 0.8;
	options->hide_single_pixel = true;
	options->glow = 0.0;
}

void gif320_vt320_resolve_cells(
	const Gif320Vt320Options *options,
	const Gif320Rect *source,
	int *cells_x,
	int *cells_y
)
{
	int requested_x = options->cells_x > 0 ? options->cells_x : 80;
	int requested_y = options->cells_y > 0 ? options->cells_y : 24;
	int resolved_x = clamp_int(requested_x, 1, 240);
	int resolved_y = clamp_int(requested_y, 1, 120);
	double source_width = fmax(source->width, 1);
	double source_height = fmax(source->height, 1);

	if (options->size_mode == GIF320_SIZE_AUTO_ORIENTATION)
	{
		if (fabs(source_height - source_width) < 0.000001)
		{
			resolved_y = clamp_int(requested_y, 1, 120);
			resolved_x = derive_columns_from_rows(
				resolved_y,
				source_width,
				source_height
			);
		}
		else if (source_height > source_width)
		{
			resolved_y = clamp_int(requested_y, 1, 120);
			resolved_x = derive_columns_from_rows(
				resolved_y,
				source_width,
				source_height
			);
		}
		else
		{
			resolved_x = clamp_int(requested_x, 1, 240);
			resolved_y = derive_rows_from_columns(
				resolved_x,
				source_width,
				source_height
			);
		}
	}
	else if (options->size_mode == GIF320_SIZE_HEIGHT_FROM_WIDTH)
	{
		resolved_y = derive_rows_from_columns(
			resolved_x,
			source_width,
			source_height
		);
	}
	else if (options->size_mode == GIF320_SIZE_WIDTH_FROM_HEIGHT)
	{
		resolved_x = derive_columns_from_rows(
			resolved_y,
			source_width,
			source_height
		);
	}

	*cells_x = clamp_int(resolved_x, 1, 240);
	*cells_y = clamp_int(resolved_y, 1, 120);
}

void gif320_vt320_resolve_preview_size(
	const Gif320Vt320Options *options,
	const Gif320Rect *source,
	int *width,
	int *height
)
{
	int cells_x;
	int cells_y;
	int scale = clamp_int(options->output_scale, 1, 12);

	gif320_vt320_resolve_cells(options, source, &cells_x, &cells_y);
	*width = cells_x * GIF320_CELL_PIXEL_WIDTH * scale;
	*height = (int)floor(
		cells_y * GIF320_CELL_PIXEL_WIDTH * scale / GIF320_DISPLAY_CELL_ASPECT
			+ 0.5
	);
}

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
)
{
	int cells_x;
	int cells_y;
	int terminal_width;
	int terminal_height;
	double *intensity;
	unsigned char *bits;
	unsigned char *reduced_bits;
	float sampled[4];

	if (width <= 0 || height <= 0 || full_width <= 0 || full_height <= 0)
	{
		return false;
	}

	gif320_vt320_resolve_cells(options, source, &cells_x, &cells_y);
	terminal_width = cells_x * GIF320_CELL_PIXEL_WIDTH;
	terminal_height = cells_y * GIF320_CELL_PIXEL_HEIGHT;
	intensity = (double *)calloc((size_t)terminal_width * (size_t)terminal_height, sizeof(double));
	bits = (unsigned char *)calloc((size_t)terminal_width * (size_t)terminal_height, 1);
	reduced_bits = (unsigned char *)calloc((size_t)terminal_width * (size_t)terminal_height, 1);

	if (intensity == NULL || bits == NULL || reduced_bits == NULL)
	{
		free(intensity);
		free(bits);
		free(reduced_bits);
		return false;
	}

	for (int y = 0; y < terminal_height; y++)
	{
		for (int x = 0; x < terminal_width; x++)
		{
			sample_source_for_terminal_pixel(
				options,
				source,
				sample,
				sample_context,
				x,
				y,
				terminal_width,
				terminal_height,
				sampled
			);
			intensity[y * terminal_width + x] = rgba_intensity(options, sampled);
		}
	}

	smooth_or_sharpen(options, intensity, terminal_width, terminal_height);
	dither(options, intensity, bits, terminal_width, terminal_height);
	reduce_cells_to_glyph_budget(
		options,
		bits,
		reduced_bits,
		cells_x,
		cells_y,
		terminal_width
	);

	for (int y = 0; y < height; y++)
	{
		double absolute_y = output_y + y + 0.5;
		double terminal_yf = absolute_y * terminal_height / (double)full_height;
		int terminal_y = clamp_int((int)floor(terminal_yf), 0, terminal_height - 1);
		double local_y = terminal_yf - floor(terminal_yf);
		for (int x = 0; x < width; x++)
		{
			double absolute_x = output_x + x + 0.5;
			double terminal_xf = absolute_x * terminal_width / (double)full_width;
			int terminal_x = clamp_int((int)floor(terminal_xf), 0, terminal_width - 1);
			double local_x = terminal_xf - floor(terminal_xf);
			double output_intensity = options->second_pass
				? shaped_bit_intensity(
					options,
					reduced_bits,
					terminal_width,
					terminal_height,
					terminal_x,
					terminal_y,
					local_x,
					local_y
				)
				: pattern_bit(
					reduced_bits,
					terminal_x,
					terminal_y,
					terminal_width,
					terminal_height
				);

			write_tinted_pixel(options, rgba, (y * width + x) * 4, output_intensity);
		}
	}

	free(intensity);
	free(bits);
	free(reduced_bits);
	return true;
}

bool gif320_vt320_render_preview(
	const Gif320Vt320Options *options,
	const Gif320Rect *source,
	Gif320SampleFunc sample,
	void *sample_context,
	float *rgba,
	int width,
	int height
)
{
	return gif320_vt320_render_preview_region(
		options,
		source,
		sample,
		sample_context,
		rgba,
		0,
		0,
		width,
		height,
		width,
		height
	);
}

void gif320_vt320_resolve_second_pass_cells(
	const Gif320Rect *source,
	int configured_cells_x,
	int configured_cells_y,
	int *cells_x,
	int *cells_y
)
{
	double source_width = fmax(source->width, 1);
	double source_height = fmax(source->height, 1);
	int resolved_x = configured_cells_x;
	int resolved_y = configured_cells_y;

	if (resolved_x > 0 && resolved_y > 0)
	{
		*cells_x = clamp_int(resolved_x, 1, 4096);
		*cells_y = clamp_int(resolved_y, 1, 4096);
		return;
	}

	if (resolved_x > 0)
	{
		resolved_y = derive_rows_from_columns(
			resolved_x,
			source_width,
			source_height
		);
	}
	else if (resolved_y > 0)
	{
		resolved_x = derive_columns_from_rows(
			resolved_y,
			source_width,
			source_height
		);
	}
	else if (fabs(source_height - source_width) < 0.000001)
	{
		resolved_y = 24;
		resolved_x = derive_columns_from_rows(
			resolved_y,
			source_width,
			source_height
		);
	}
	else if (source_height > source_width)
	{
		resolved_y = 24;
		resolved_x = derive_columns_from_rows(
			resolved_y,
			source_width,
			source_height
		);
	}
	else
	{
		resolved_x = 80;
		resolved_y = derive_rows_from_columns(
			resolved_x,
			source_width,
			source_height
		);
	}

	*cells_x = clamp_int(resolved_x, 1, 4096);
	*cells_y = clamp_int(resolved_y, 1, 4096);
}

static double grid_intensity(
	const double *grid,
	int terminal_width,
	int terminal_height,
	int terminal_x,
	int terminal_y
)
{
	if (terminal_x < 0
		|| terminal_y < 0
		|| terminal_x >= terminal_width
		|| terminal_y >= terminal_height)
	{
		return 0.0;
	}

	return grid[terminal_y * terminal_width + terminal_x];
}

static bool grid_has_neighbor(
	const double *grid,
	int terminal_width,
	int terminal_height,
	int terminal_x,
	int terminal_y
)
{
	for (int yy = -1; yy <= 1; yy++)
	{
		for (int xx = -1; xx <= 1; xx++)
		{
			if (xx == 0 && yy == 0)
			{
				continue;
			}

			if (grid_intensity(
				grid,
				terminal_width,
				terminal_height,
				terminal_x + xx,
				terminal_y + yy
			) > 0.00001)
			{
				return true;
			}
		}
	}

	return false;
}

static double shaped_grid_intensity(
	const Gif320Vt320Options *options,
	const double *grid,
	int terminal_width,
	int terminal_height,
	int terminal_x,
	int terminal_y,
	double local_x,
	double local_y
)
{
	double current = grid_intensity(grid, terminal_width, terminal_height, terminal_x, terminal_y);
	double left = grid_intensity(grid, terminal_width, terminal_height, terminal_x - 1, terminal_y);
	double right = grid_intensity(grid, terminal_width, terminal_height, terminal_x + 1, terminal_y);
	bool neighbor = grid_has_neighbor(grid, terminal_width, terminal_height, terminal_x, terminal_y);
	double intensity = shape_terminal_pixel(
		options,
		current,
		left,
		right,
		neighbor,
		local_x,
		local_y
	);

	if (options->glow > 0.0)
	{
		double glow = 0.0;
		for (int yy = -1; yy <= 1; yy++)
		{
			for (int xx = -1; xx <= 1; xx++)
			{
				if (xx == 0 && yy == 0)
				{
					continue;
				}

				glow = fmax(
					glow,
					grid_intensity(
						grid,
						terminal_width,
						terminal_height,
						terminal_x + xx,
						terminal_y + yy
					)
				);
			}
		}

		intensity = clamp_double(
			intensity + glow * clamp_double(options->glow, 0.0, 1.0) * 0.25,
			0.0,
			1.0
		);
	}

	return intensity;
}

static void sample_second_pass_grid(
	const Gif320Vt320Options *options,
	const Gif320Rect *source,
	int terminal_width,
	int terminal_height,
	Gif320SampleFunc sample,
	void *sample_context,
	double *grid
)
{
	float sampled[4];

	for (int y = 0; y < terminal_height; y++)
	{
		double sample_y = source->y + (y + 0.5) * source->height / terminal_height;
		for (int x = 0; x < terminal_width; x++)
		{
			double sample_x = source->x + (x + 0.5) * source->width / terminal_width;
			sampled[0] = 0.0f;
			sampled[1] = 0.0f;
			sampled[2] = 0.0f;
			sampled[3] = 1.0f;
			sample(sample_context, sample_x, sample_y, sampled);
			grid[y * terminal_width + x] = rgba_intensity(options, sampled);
		}
	}
}

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
)
{
	double *grid;

	if (source->width <= 0 || source->height <= 0 || width <= 0 || height <= 0)
	{
		return false;
	}

	gif320_vt320_resolve_second_pass_cells(
		source,
		terminal_width,
		terminal_height,
		&terminal_width,
		&terminal_height
	);

	grid = (double *)calloc(
		(size_t)terminal_width * (size_t)terminal_height,
		sizeof(double)
	);
	if (grid == NULL)
	{
		return false;
	}

	sample_second_pass_grid(
		options,
		source,
		terminal_width,
		terminal_height,
		sample,
		sample_context,
		grid
	);

	for (int y = 0; y < height; y++)
	{
		double absolute_y = output_y + y + 0.5;
		double terminal_yf = (absolute_y - source->y) * terminal_height / source->height;
		int terminal_y = clamp_int((int)floor(terminal_yf), 0, terminal_height - 1);
		double local_y = terminal_yf - floor(terminal_yf);
		for (int x = 0; x < width; x++)
		{
			double absolute_x = output_x + x + 0.5;
			double terminal_xf = (absolute_x - source->x) * terminal_width / source->width;
			int terminal_x = clamp_int((int)floor(terminal_xf), 0, terminal_width - 1);
			double local_x = terminal_xf - floor(terminal_xf);
			double output_intensity = shaped_grid_intensity(
				options,
				grid,
				terminal_width,
				terminal_height,
				terminal_x,
				terminal_y,
				local_x,
				local_y
			);

			write_tinted_pixel(options, rgba, (y * width + x) * 4, output_intensity);
		}
	}

	free(grid);
	return true;
}
