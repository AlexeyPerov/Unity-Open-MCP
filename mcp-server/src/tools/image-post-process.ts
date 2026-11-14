// Crop / downscale / save-to options shared by unity_senses_screenshot and
// unity_senses_capture_inline — the single-capture counterpart of
// capture_series' region / scale. The bridge (ImagePostProcess.cs) applies
// region first, then scale, then max_edge.

export const IMAGE_REGION_PROP = {
  type: "array",
  minItems: 4,
  maxItems: 4,
  items: { type: "number" },
  description:
    "Crop the captured image to [x, y, w, h] in image pixels, bottom-left origin — for 'composed' at native size " +
    "that is the game screen, the space of ui_snapshot rects, so a node's rect can be passed as is. Clipped to the image; " +
    "a rect entirely outside it is invalid_arguments.",
} as const;

export const IMAGE_SCALE_PROP = {
  type: "number",
  minimum: 0.05,
  maximum: 1,
  description: "Downscale the (cropped) image by this factor (1 = captured size, the default).",
} as const;

export const IMAGE_MAX_EDGE_PROP = {
  type: "integer",
  minimum: 16,
  maximum: 8192,
  description: "Downscale so the longest edge is at most this many pixels (applied after scale; never upscales).",
} as const;

export const IMAGE_SAVE_TO_PROP = {
  type: "string",
  minLength: 1,
  description:
    "Also copy the final PNG here: a .png path, or a directory (trailing slash, existing, or no extension) that keeps " +
    "the file name. Relative paths resolve against the project root, ~/ against the home directory; a target inside " +
    "the project's Assets/ or Packages/ is refused however the path is written (the image would be imported). " +
    "The response's savedTo is the full path.",
} as const;
