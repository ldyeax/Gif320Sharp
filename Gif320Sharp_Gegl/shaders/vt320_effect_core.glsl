// Reference GLSL core for the VT320 second-pass look.
// The GEGL module currently uses a CPU implementation of this math.

vec2 vt320_terminal_pixel_delta(vec2 terminal_pixel_count)
{
    return 1.0 / max(terminal_pixel_count, vec2(1.0));
}

vec2 vt320_terminal_pixel_coord(vec2 uv, vec2 terminal_pixel_count)
{
    return min(floor(clamp(uv, vec2(0.0), vec2(1.0)) * terminal_pixel_count),
               terminal_pixel_count - vec2(1.0));
}

vec2 vt320_terminal_pixel_min(vec2 coord, vec2 terminal_pixel_count)
{
    return coord * vt320_terminal_pixel_delta(terminal_pixel_count);
}

vec2 vt320_terminal_pixel_center(vec2 coord, vec2 terminal_pixel_count)
{
    return (coord + vec2(0.5)) * vt320_terminal_pixel_delta(terminal_pixel_count);
}

vec2 vt320_terminal_pixel_local(vec2 uv, vec2 coord, vec2 terminal_pixel_count)
{
    return clamp(uv * terminal_pixel_count - coord, vec2(0.0), vec2(1.0));
}

float vt320_scanline_intensity(float intensity, float local_y, float scanline_gap)
{
    float gap = clamp(scanline_gap, 0.0, 1.0);
    float scanline_mask = smoothstep(0.0, max(0.00001, 1.0 - gap),
                                     abs(0.5 - local_y) * 2.0);
    return intensity * (1.0 - scanline_mask * gap);
}

float vt320_round_pixel(float intensity,
                        vec2 uv,
                        vec2 coord,
                        vec2 terminal_pixel_count,
                        float pixel_roundness,
                        float roundness_aspect,
                        float left_lit,
                        float right_lit,
                        float connected_lit,
                        bool hide_single_pixel)
{
    if (pixel_roundness <= 0.001 || intensity <= 0.00001) {
        return intensity;
    }

    vec2 delta = vt320_terminal_pixel_delta(terminal_pixel_count);
    vec2 center = vt320_terminal_pixel_center(coord, terminal_pixel_count);
    vec2 minp = vt320_terminal_pixel_min(coord, terminal_pixel_count);
    float aspect = max(roundness_aspect, 0.00001);

    vec2 center_delta = uv - center;
    center_delta.y /= aspect;

    vec2 vertical_delta = vec2(0.0, center_delta.y);
    float vertical_dist = length(vertical_delta / delta);

    vec2 left_delta = uv - vec2(minp.x, center.y);
    left_delta.y /= aspect;
    float left_dist = length(left_delta / delta);

    vec2 right_delta = uv - vec2(minp.x + delta.x, center.y);
    right_delta.y /= aspect;
    float right_dist = length(right_delta / delta);

    float edge_dist = vertical_dist;
    if (left_lit <= 0.00001 && right_lit > 0.00001) {
        edge_dist = right_dist;
    } else if (left_lit > 0.00001 && right_lit <= 0.00001) {
        edge_dist = left_dist;
    } else if (left_lit <= 0.00001 && right_lit <= 0.00001) {
        if (hide_single_pixel && connected_lit <= 0.00001) {
            return 0.0;
        }

        vec2 dot_delta = center_delta;
        dot_delta.x *= 2.0;
        edge_dist = length(dot_delta / delta);
    }

    return intensity * mix(1.0, clamp(1.0 - edge_dist, 0.0, 1.0),
                           clamp(pixel_roundness, 0.0, 1.0));
}
