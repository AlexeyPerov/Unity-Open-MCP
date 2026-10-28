// Playtest session report — PNG thumbnails without an image dependency.
//
// The bridge writes full-size frames to disk; the session report needs small
// previews of them. Decoding happens here, in the server, so a report can be
// written after the Editor is gone (an MCP server restart, a crashed Editor)
// and the bridge contract does not change. Only `node:zlib` is used: PNG is
// zlib-compressed scanlines with a per-row filter, so a reader for the
// non-interlaced 8/16-bit formats Unity's EncodeToPNG produces is short.
// Anything else (interlaced, < 8-bit gray) returns null and the report links
// the full frame without a thumbnail.
import { deflateSync, inflateSync } from "node:zlib";

export interface RgbaImage {
  width: number;
  height: number;
  /** width * height * 4 bytes, row-major, top row first. */
  data: Uint8Array;
}

const SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
/** Refuse images whose raw RGBA would exceed this (a corrupt header, a 16K frame). */
const MAX_PIXELS = 64 * 1024 * 1024;

let crcTable: Uint32Array | null = null;
function crc32(bytes: Uint8Array): number {
  if (!crcTable) {
    crcTable = new Uint32Array(256);
    for (let n = 0; n < 256; n++) {
      let c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      crcTable[n] = c >>> 0;
    }
  }
  let crc = 0xffffffff;
  for (let i = 0; i < bytes.length; i++) crc = crcTable[(crc ^ bytes[i]) & 0xff] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

/** Read the width / height from a PNG header without decoding it. */
export function pngSize(bytes: Uint8Array): { width: number; height: number } | null {
  if (bytes.length < 24 || !Buffer.from(bytes.subarray(0, 8)).equals(SIGNATURE)) return null;
  const view = Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (view.toString("latin1", 12, 16) !== "IHDR") return null;
  return { width: view.readUInt32BE(16), height: view.readUInt32BE(20) };
}

/** Decode a PNG into 8-bit RGBA, or null for a format this reader does not cover. */
export function decodePng(bytes: Uint8Array): RgbaImage | null {
  const buf = Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (buf.length < 8 || !buf.subarray(0, 8).equals(SIGNATURE)) return null;
  let offset = 8;
  let width = 0, height = 0, depth = 0, colorType = -1, interlace = 0;
  let palette: Buffer | null = null;
  let transparency: Buffer | null = null;
  const idat: Buffer[] = [];
  while (offset + 12 <= buf.length) {
    const length = buf.readUInt32BE(offset);
    const type = buf.toString("latin1", offset + 4, offset + 8);
    const start = offset + 8;
    if (start + length + 4 > buf.length) return null;
    const chunk = buf.subarray(start, start + length);
    if (type === "IHDR") {
      width = chunk.readUInt32BE(0);
      height = chunk.readUInt32BE(4);
      depth = chunk[8];
      colorType = chunk[9];
      interlace = chunk[12];
    } else if (type === "PLTE") palette = chunk;
    else if (type === "tRNS") transparency = chunk;
    else if (type === "IDAT") idat.push(chunk);
    else if (type === "IEND") break;
    offset = start + length + 4;
  }
  if (!width || !height || width * height > MAX_PIXELS || interlace !== 0) return null;
  const channels = colorType === 0 ? 1 : colorType === 2 ? 3 : colorType === 3 ? 1 : colorType === 4 ? 2 : colorType === 6 ? 4 : 0;
  if (!channels) return null;
  if (colorType === 3 ? depth !== 8 || !palette : depth !== 8 && depth !== 16) return null;
  const bytesPerSample = depth / 8;
  const bpp = channels * bytesPerSample;
  const stride = width * bpp;
  let raw: Buffer;
  try { raw = inflateSync(Buffer.concat(idat)); } catch { return null; }
  if (raw.length < (stride + 1) * height) return null;

  const out = new Uint8Array(width * height * 4);
  let prev = new Uint8Array(stride);
  let row = new Uint8Array(stride);
  for (let y = 0; y < height; y++) {
    const base = y * (stride + 1);
    const filter = raw[base];
    for (let x = 0; x < stride; x++) {
      const value = raw[base + 1 + x];
      const left = x >= bpp ? row[x - bpp] : 0;
      const up = prev[x];
      const upLeft = x >= bpp ? prev[x - bpp] : 0;
      let predictor: number;
      switch (filter) {
        case 0: predictor = 0; break;
        case 1: predictor = left; break;
        case 2: predictor = up; break;
        case 3: predictor = (left + up) >> 1; break;
        case 4: {
          const p = left + up - upLeft;
          const pa = Math.abs(p - left), pb = Math.abs(p - up), pc = Math.abs(p - upLeft);
          predictor = pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
          break;
        }
        default: return null;
      }
      row[x] = (value + predictor) & 0xff;
    }
    for (let x = 0; x < width; x++) {
      const s = x * bpp, d = (y * width + x) * 4;
      // 16-bit samples keep their high byte.
      const sample = (i: number) => row[s + i * bytesPerSample];
      switch (colorType) {
        case 0: out[d] = out[d + 1] = out[d + 2] = sample(0); out[d + 3] = 255; break;
        case 2: out[d] = sample(0); out[d + 1] = sample(1); out[d + 2] = sample(2); out[d + 3] = 255; break;
        case 3: {
          const index = row[s];
          out[d] = palette![index * 3] ?? 0;
          out[d + 1] = palette![index * 3 + 1] ?? 0;
          out[d + 2] = palette![index * 3 + 2] ?? 0;
          out[d + 3] = transparency && index < transparency.length ? transparency[index] : 255;
          break;
        }
        case 4: out[d] = out[d + 1] = out[d + 2] = sample(0); out[d + 3] = sample(1); break;
        default: out[d] = sample(0); out[d + 1] = sample(1); out[d + 2] = sample(2); out[d + 3] = sample(3);
      }
    }
    const swap = prev; prev = row; row = swap;
  }
  return { width, height, data: out };
}

/** Box-filter downscale so the image fits maxWidth × maxHeight (never upscales). */
export function downscale(image: RgbaImage, maxWidth: number, maxHeight: number): RgbaImage {
  const ratio = Math.min(1, maxWidth / image.width, maxHeight / image.height);
  const width = Math.max(1, Math.round(image.width * ratio));
  const height = Math.max(1, Math.round(image.height * ratio));
  if (width === image.width && height === image.height) return image;
  const out = new Uint8Array(width * height * 4);
  for (let ty = 0; ty < height; ty++) {
    const y0 = Math.floor((ty * image.height) / height);
    const y1 = Math.max(y0 + 1, Math.floor(((ty + 1) * image.height) / height));
    for (let tx = 0; tx < width; tx++) {
      const x0 = Math.floor((tx * image.width) / width);
      const x1 = Math.max(x0 + 1, Math.floor(((tx + 1) * image.width) / width));
      let r = 0, g = 0, b = 0, a = 0;
      for (let y = y0; y < y1; y++) {
        let i = (y * image.width + x0) * 4;
        for (let x = x0; x < x1; x++, i += 4) {
          r += image.data[i]; g += image.data[i + 1]; b += image.data[i + 2]; a += image.data[i + 3];
        }
      }
      const n = (y1 - y0) * (x1 - x0);
      const d = (ty * width + tx) * 4;
      out[d] = Math.round(r / n); out[d + 1] = Math.round(g / n); out[d + 2] = Math.round(b / n); out[d + 3] = Math.round(a / n);
    }
  }
  return { width, height, data: out };
}

function chunk(type: string, data: Uint8Array): Buffer {
  const head = Buffer.alloc(8);
  head.writeUInt32BE(data.length, 0);
  head.write(type, 4, "latin1");
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), data])), 0);
  return Buffer.concat([head, data, crc]);
}

/** Encode RGBA as a PNG (RGB when every pixel is opaque). */
export function encodePng(image: RgbaImage): Buffer {
  let opaque = true;
  for (let i = 3; i < image.data.length; i += 4) if (image.data[i] !== 255) { opaque = false; break; }
  const channels = opaque ? 3 : 4;
  const stride = image.width * channels;
  const raw = Buffer.alloc((stride + 1) * image.height);
  for (let y = 0; y < image.height; y++) {
    const base = y * (stride + 1);
    raw[base] = 1; // Sub filter: cheap and compresses UI frames well.
    for (let x = 0; x < image.width; x++) {
      const s = (y * image.width + x) * 4;
      for (let c = 0; c < channels; c++) {
        const value = image.data[s + c];
        const left = x > 0 ? image.data[s - 4 + c] : 0;
        raw[base + 1 + x * channels + c] = (value - left) & 0xff;
      }
    }
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(image.width, 0);
  header.writeUInt32BE(image.height, 4);
  header[8] = 8;
  header[9] = opaque ? 2 : 6;
  return Buffer.concat([SIGNATURE, chunk("IHDR", header), chunk("IDAT", deflateSync(raw, { level: 9 })), chunk("IEND", new Uint8Array(0))]);
}

/** Decode, fit into the box and re-encode; null when the source cannot be decoded. */
export function thumbnail(bytes: Uint8Array, maxWidth: number, maxHeight: number): { png: Buffer; width: number; height: number; source: { width: number; height: number } } | null {
  const image = decodePng(bytes);
  if (!image) return null;
  const small = downscale(image, maxWidth, maxHeight);
  return { png: encodePng(small), width: small.width, height: small.height, source: { width: image.width, height: image.height } };
}
