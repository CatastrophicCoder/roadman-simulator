// The world's sky (docs/specs/world-edge-game.md, "Space backdrop"). Above the horizon: the day sky of Godot's
// ProceduralSkyMaterial with its default values (sky colours and curve, the sun disc from LIGHT0), so the town looks
// as before. Below the horizon: a procedural starfield (hashed cells, no texture), blended into the horizon colour.
// The radiance pass (AT_CUBEMAP_PASS, the source of ambient and reflected light) keeps ProceduralSkyMaterial's ground
// gradient below the horizon, so the town's lighting doesn't change.
shader_type sky;
render_mode use_debanding;

uniform vec4 sky_top_color : source_color = vec4(0.385, 0.454, 0.55, 1.0);
uniform vec4 sky_horizon_color : source_color = vec4(0.6463, 0.6558, 0.6708, 1.0);
uniform float sky_curve : hint_range(0.0, 1.0) = 0.15;
uniform float sky_energy = 1.0;
uniform vec4 ground_bottom_color : source_color = vec4(0.2, 0.169, 0.133, 1.0);
uniform vec4 ground_horizon_color : source_color = vec4(0.6463, 0.6558, 0.6708, 1.0);
uniform float ground_curve : hint_range(0.0, 1.0) = 0.02;
uniform float ground_energy = 1.0;
// Radians (ProceduralSkyMaterial's 30 degrees).
uniform float sun_angle_max = 0.5235988;
uniform float sun_curve : hint_range(0.0, 1.0) = 0.15;
uniform float exposure = 1.0;

// Starfield.
uniform vec4 space_color : source_color = vec4(0.004, 0.005, 0.012, 1.0);
// Cells per unit of the view direction; more cells, smaller and denser stars.
uniform float star_scale = 160.0;
// Fraction of cells that hold a star.
uniform float star_density = 0.12;
// Star radius as a fraction of a cell.
uniform float star_radius = 0.09;
uniform float star_energy = 2.5;
// How far below the horizon (in EYEDIR.y) the horizon colour fades into space.
uniform float horizon_blend = 0.06;

float hash13(vec3 p) {
	p = fract(p * 0.1031);
	p += dot(p, p.zyx + 31.32);
	return fract((p.x + p.y) * p.z);
}

vec3 hash33(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yxz + 33.33);
	return fract((p.xxy + p.yxx) * p.zyx);
}

// One layer of stars: a star in some cells of a 3D grid over the view direction, a soft disc around a random point.
vec3 star_layer(vec3 dir, float scale, float density, float radius) {
	vec3 p = dir * scale;
	vec3 cell = floor(p);
	vec3 local = fract(p);
	if (hash13(cell) > density) {
		return vec3(0.0);
	}
	vec3 centre = 0.2 + 0.6 * hash33(cell + 17.0);
	float d = length(local - centre);
	float glow = exp(-d * d / (radius * radius));
	float brightness = 0.35 + 0.65 * hash13(cell + 41.0);
	// A slight tint: bluish to warm white.
	vec3 tint = mix(vec3(0.75, 0.82, 1.0), vec3(1.0, 0.92, 0.8), hash13(cell + 73.0));
	return tint * brightness * glow;
}

vec3 starfield(vec3 dir) {
	vec3 stars = star_layer(dir, star_scale, star_density, star_radius);
	stars += 0.6 * star_layer(dir, star_scale * 2.3, star_density * 0.8, star_radius * 1.4);
	return space_color.rgb + stars * star_energy;
}

void sky() {
	float v_angle = acos(clamp(EYEDIR.y, -1.0, 1.0));
	float c = (1.0 - v_angle / (PI * 0.5));
	vec3 sky = mix(sky_horizon_color.rgb, sky_top_color.rgb, clamp(1.0 - pow(1.0 - c, 1.0 / sky_curve), 0.0, 1.0));
	sky *= sky_energy;

	if (LIGHT0_ENABLED) {
		float sun_angle = acos(dot(LIGHT0_DIRECTION, EYEDIR));
		if (sun_angle < LIGHT0_SIZE) {
			sky = LIGHT0_COLOR * LIGHT0_ENERGY;
		} else if (sun_angle < sun_angle_max) {
			float c2 = (sun_angle - LIGHT0_SIZE) / (sun_angle_max - LIGHT0_SIZE);
			sky = mix(LIGHT0_COLOR * LIGHT0_ENERGY, sky, clamp(1.0 - pow(1.0 - c2, 1.0 / sun_curve), 0.0, 1.0));
		}
	}

	vec3 below;
	if (AT_CUBEMAP_PASS) {
		c = (v_angle - (PI * 0.5)) / (PI * 0.5);
		below = mix(ground_horizon_color.rgb, ground_bottom_color.rgb, clamp(1.0 - pow(1.0 - c, 1.0 / ground_curve), 0.0, 1.0));
		below *= ground_energy;
	} else {
		float fade = smoothstep(0.0, horizon_blend, -EYEDIR.y);
		below = mix(sky_horizon_color.rgb * sky_energy, starfield(EYEDIR), fade);
	}

	COLOR = mix(below, sky, step(0.0, EYEDIR.y)) * exposure;
}
