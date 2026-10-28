// Placeholder building material: one wall colour, and a darker roof colour on faces that point up.
// The tile meshes have one surface per building, so walls and roofs are told apart by the world-space normal.
shader_type spatial;

uniform vec3 wall_color : source_color = vec3(0.70, 0.68, 0.64);
uniform vec3 roof_color : source_color = vec3(0.38, 0.36, 0.35);
// Faces whose world normal has a larger up component than this count as roof.
uniform float roof_min_up : hint_range(0.0, 1.0) = 0.5;
uniform float roughness_value : hint_range(0.0, 1.0) = 0.9;

void fragment() {
	float up = (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).y;
	ALBEDO = up > roof_min_up ? roof_color : wall_color;
	ROUGHNESS = roughness_value;
}
