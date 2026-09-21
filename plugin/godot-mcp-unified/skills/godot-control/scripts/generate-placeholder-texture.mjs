#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import zlib from "node:zlib";

import { boundedNumber, parseArgs, resolveProjectOutput, writeResult } from "./placeholder-common.mjs";

const NAMED = {
  transparent: [0, 0, 0, 0],
  black: [0, 0, 0, 255],
  white: [255, 255, 255, 255],
  red: [255, 64, 64, 255],
  green: [64, 220, 96, 255],
  blue: [71, 140, 191, 255],
  yellow: [255, 220, 64, 255],
  cyan: [64, 220, 255, 255],
  magenta: [255, 64, 220, 255],
  gray: [128, 128, 128, 255],
  grey: [128, 128, 128, 255],
};

const FONT = {
  A: ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
  B: ["11110", "10001", "10001", "11110", "10001", "10001", "11110"],
  C: ["01111", "10000", "10000", "10000", "10000", "10000", "01111"],
  D: ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
  E: ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
  F: ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
  G: ["01111", "10000", "10000", "10111", "10001", "10001", "01111"],
  H: ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
  I: ["11111", "00100", "00100", "00100", "00100", "00100", "11111"],
  J: ["00111", "00010", "00010", "00010", "10010", "10010", "01100"],
  K: ["10001", "10010", "10100", "11000", "10100", "10010", "10001"],
  L: ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
  M: ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
  N: ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
  O: ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
  P: ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
  Q: ["01110", "10001", "10001", "10001", "10101", "10010", "01101"],
  R: ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
  S: ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
  T: ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
  U: ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
  V: ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
  W: ["10001", "10001", "10001", "10101", "10101", "10101", "01010"],
  X: ["10001", "10001", "01010", "00100", "01010", "10001", "10001"],
  Y: ["10001", "10001", "01010", "00100", "00100", "00100", "00100"],
  Z: ["11111", "00001", "00010", "00100", "01000", "10000", "11111"],
  0: ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
  1: ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
  2: ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
  3: ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
  4: ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
  5: ["11111", "10000", "10000", "11110", "00001", "00001", "11110"],
  6: ["01110", "10000", "10000", "11110", "10001", "10001", "01110"],
  7: ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
  8: ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
  9: ["01110", "10001", "10001", "01111", "00001", "00001", "01110"],
  "-": ["00000", "00000", "00000", "11111", "00000", "00000", "00000"],
  _: ["00000", "00000", "00000", "00000", "00000", "00000", "11111"],
  "?": ["01110", "10001", "00001", "00010", "00100", "00000", "00100"],
  " ": ["00000", "00000", "00000", "00000", "00000", "00000", "00000"],
};

function parseColor(value, fallback) {
  if (value === undefined) return fallback;
  const text = String(value).trim().toLowerCase();
  if (NAMED[text]) return [...NAMED[text]];
  if (text.startsWith("#")) {
    const hex = text.slice(1);
    if (hex.length === 3 || hex.length === 4) {
      return [...hex].map((c) => Number.parseInt(c + c, 16)).concat(hex.length === 3 ? [255] : []);
    }
    if (hex.length === 6 || hex.length === 8) {
      const values = [];
      for (let i = 0; i < hex.length; i += 2) values.push(Number.parseInt(hex.slice(i, i + 2), 16));
      if (values.length === 3) values.push(255);
      if (values.every(Number.isFinite)) return values;
    }
  }
  const parts = text.split(",").map(Number);
  if ((parts.length === 3 || parts.length === 4) && parts.every(Number.isFinite)) {
    const scale = parts.some((part) => part > 1) ? 1 : 255;
    const values = parts.map((part) => Math.round(Math.max(0, Math.min(255, part * scale))));
    if (values.length === 3) values.push(255);
    return values;
  }
  throw new Error(`unsupported color: ${value}`);
}

function pointInPolygon(x, y, points) {
  let inside = false;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    const [xi, yi] = points[i];
    const [xj, yj] = points[j];
    if (yi > y !== yj > y && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

function arrowPoints(direction) {
  const up = [
    [0.5, 0.08], [0.92, 0.48], [0.67, 0.48], [0.67, 0.92], [0.33, 0.92], [0.33, 0.48], [0.08, 0.48],
  ];
  return up.map(([x, y]) => {
    if (direction === "down") return [1 - x, 1 - y];
    if (direction === "left") return [y, 1 - x];
    if (direction === "right") return [1 - y, x];
    return [x, y];
  });
}

function makeMask(shape, width, height, direction, cellSize) {
  const mask = new Uint8Array(width * height);
  const polygon = shape === "arrow" ? arrowPoints(direction) : undefined;
  for (let y = 0; y < height; y += 1) {
    for (let x = 0; x < width; x += 1) {
      const nx = (x + 0.5) / width;
      const ny = (y + 0.5) / height;
      let inside = false;
      if (shape === "solid") inside = true;
      else if (shape === "circle") inside = ((nx - 0.5) ** 2 + (ny - 0.5) ** 2) <= 0.42 ** 2;
      else if (shape === "diamond") inside = Math.abs(nx - 0.5) + Math.abs(ny - 0.5) <= 0.45;
      else if (shape === "triangle") inside = pointInPolygon(nx, ny, [[0.5, 0.08], [0.92, 0.9], [0.08, 0.9]]);
      else if (shape === "arrow") inside = pointInPolygon(nx, ny, polygon);
      else if (shape === "checkerboard") inside = (Math.floor(x / cellSize) + Math.floor(y / cellSize)) % 2 === 0;
      else if (shape === "grid") inside = x % cellSize === 0 || y % cellSize === 0;
      mask[y * width + x] = inside ? 1 : 0;
    }
  }
  return mask;
}

function setPixel(pixels, width, x, y, color) {
  const index = (y * width + x) * 4;
  pixels[index] = color[0];
  pixels[index + 1] = color[1];
  pixels[index + 2] = color[2];
  pixels[index + 3] = color[3];
}

function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit += 1) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function pngChunk(type, data) {
  const name = Buffer.from(type, "ascii");
  const chunk = Buffer.alloc(12 + data.length);
  chunk.writeUInt32BE(data.length, 0);
  name.copy(chunk, 4);
  data.copy(chunk, 8);
  chunk.writeUInt32BE(crc32(Buffer.concat([name, data])), 8 + data.length);
  return chunk;
}

function encodePng(width, height, pixels) {
  const rows = Buffer.alloc((width * 4 + 1) * height);
  for (let y = 0; y < height; y += 1) {
    const row = y * (width * 4 + 1);
    rows[row] = 0;
    pixels.copy(rows, row + 1, y * width * 4, (y + 1) * width * 4);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    pngChunk("IHDR", ihdr),
    pngChunk("IDAT", zlib.deflateSync(rows)),
    pngChunk("IEND", Buffer.alloc(0)),
  ]);
}

const args = parseArgs(process.argv.slice(2), [
  "width",
  "height",
  "shape",
  "direction",
  "cell-size",
  "outline-width",
  "fill-color",
  "outline-color",
  "background-color",
  "label-color",
  "label",
]);
const { output, resourcePath } = resolveProjectOutput(args, ".png");
const width = Math.round(boundedNumber(args.width, 64, 1, 1024, "width"));
const height = Math.round(boundedNumber(args.height, 64, 1, 1024, "height"));
const shape = args.shape ?? "solid";
if (!new Set(["solid", "circle", "triangle", "diamond", "arrow", "checkerboard", "grid"]).has(shape)) {
  throw new Error("unsupported shape");
}
const direction = args.direction ?? "up";
if (!new Set(["up", "down", "left", "right"]).has(direction)) throw new Error("unsupported direction");
const cellSize = Math.round(boundedNumber(args["cell-size"], 8, 1, 256, "cell-size"));
const outlineWidth = Math.round(boundedNumber(args["outline-width"], 1, 0, 64, "outline-width"));
const fill = parseColor(args["fill-color"], NAMED.blue);
const outline = parseColor(args["outline-color"], NAMED.black);
const background = parseColor(args["background-color"], NAMED.transparent);
const labelColor = parseColor(args["label-color"], NAMED.white);
const pixels = Buffer.alloc(width * height * 4);
const mask = makeMask(shape, width, height, direction, cellSize);

for (let y = 0; y < height; y += 1) {
  for (let x = 0; x < width; x += 1) {
    const inside = mask[y * width + x] === 1;
    let color = inside ? fill : background;
    if (inside && outlineWidth > 0 && shape !== "checkerboard" && shape !== "grid") {
      let edge = false;
      for (let oy = -outlineWidth; oy <= outlineWidth && !edge; oy += 1) {
        for (let ox = -outlineWidth; ox <= outlineWidth; ox += 1) {
          const px = x + ox;
          const py = y + oy;
          if (px < 0 || py < 0 || px >= width || py >= height || mask[py * width + px] === 0) {
            edge = true;
            break;
          }
        }
      }
      if (edge) color = outline;
    }
    setPixel(pixels, width, x, y, color);
  }
}

const label = String(args.label ?? "").toUpperCase();
if (label.length > 0) {
  const glyphWidth = 5;
  const glyphHeight = 7;
  const scale = Math.max(1, Math.floor(Math.min(width / Math.max(1, label.length * 6), height / 9)));
  const textWidth = (label.length * 6 - 1) * scale;
  const startX = Math.floor((width - textWidth) / 2);
  const startY = Math.floor((height - glyphHeight * scale) / 2);
  [...label].forEach((character, characterIndex) => {
    const glyph = FONT[character] ?? FONT["?"];
    for (let gy = 0; gy < glyphHeight; gy += 1) {
      for (let gx = 0; gx < glyphWidth; gx += 1) {
        if (glyph[gy][gx] !== "1") continue;
        for (let sy = 0; sy < scale; sy += 1) {
          for (let sx = 0; sx < scale; sx += 1) {
            const x = startX + characterIndex * 6 * scale + gx * scale + sx;
            const y = startY + gy * scale + sy;
            if (x >= 0 && y >= 0 && x < width && y < height) setPixel(pixels, width, x, y, labelColor);
          }
        }
      }
    }
  });
}

const png = encodePng(width, height, pixels);
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, png, { flag: args.replace === true ? "w" : "wx" });
writeResult(output, resourcePath, { shape, width, height, bytes: png.length });
