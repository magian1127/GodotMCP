#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";

import { boundedNumber, parseArgs, resolveProjectOutput, writeResult } from "./placeholder-common.mjs";

const args = parseArgs(process.argv.slice(2), [
  "waveform",
  "sample-rate",
  "duration",
  "frequency",
  "end-frequency",
  "volume",
  "fade-in",
  "fade-out",
  "decay",
]);
const { output, resourcePath } = resolveProjectOutput(args, ".wav");
const waveform = args.waveform ?? "sine";
if (!new Set(["sine", "square", "triangle", "sawtooth", "noise"]).has(waveform)) {
  throw new Error("waveform must be sine, square, triangle, sawtooth, or noise");
}

const sampleRate = Math.round(boundedNumber(args["sample-rate"], 44100, 8000, 96000, "sample-rate"));
const duration = boundedNumber(args.duration, 0.3, 0.01, 5, "duration");
const frequency = boundedNumber(args.frequency, 440, 1, 20000, "frequency");
const endFrequency = boundedNumber(args["end-frequency"], frequency, 1, 20000, "end-frequency");
const volume = boundedNumber(args.volume, 0.8, 0, 1, "volume");
const fadeIn = boundedNumber(args["fade-in"], 0.003, 0, duration, "fade-in");
const fadeOut = boundedNumber(args["fade-out"], 0.003, 0, duration, "fade-out");
const decay = boundedNumber(args.decay, 0, 0, 30, "decay");
const sampleCount = Math.max(1, Math.round(sampleRate * duration));
const pcm = Buffer.alloc(sampleCount * 2);
let phase = 0;
let noiseState = 0x6d2b79f5;

for (let i = 0; i < sampleCount; i += 1) {
  const t = i / sampleRate;
  const ratio = sampleCount <= 1 ? 0 : i / (sampleCount - 1);
  const hz = frequency + (endFrequency - frequency) * ratio;
  phase += hz / sampleRate;
  const cycle = phase - Math.floor(phase);
  let sample;
  switch (waveform) {
    case "square":
      sample = cycle < 0.5 ? 1 : -1;
      break;
    case "triangle":
      sample = 1 - 4 * Math.abs(cycle - 0.5);
      break;
    case "sawtooth":
      sample = cycle * 2 - 1;
      break;
    case "noise":
      noiseState = (Math.imul(noiseState ^ (noiseState >>> 15), 1 | noiseState) + 0x6d2b79f5) | 0;
      sample = (((noiseState ^ (noiseState >>> 14)) >>> 0) / 0xffffffff) * 2 - 1;
      break;
    default:
      sample = Math.sin(cycle * Math.PI * 2);
  }
  let envelope = 1;
  if (fadeIn > 0) envelope = Math.min(envelope, t / fadeIn);
  if (fadeOut > 0) envelope = Math.min(envelope, (duration - t) / fadeOut);
  if (decay > 0) envelope *= Math.exp(-t / decay);
  const value = Math.max(-1, Math.min(1, sample * volume * Math.max(0, envelope)));
  pcm.writeInt16LE(Math.round(value * 32767), i * 2);
}

const wav = Buffer.alloc(44 + pcm.length);
wav.write("RIFF", 0);
wav.writeUInt32LE(36 + pcm.length, 4);
wav.write("WAVEfmt ", 8);
wav.writeUInt32LE(16, 16);
wav.writeUInt16LE(1, 20);
wav.writeUInt16LE(1, 22);
wav.writeUInt32LE(sampleRate, 24);
wav.writeUInt32LE(sampleRate * 2, 28);
wav.writeUInt16LE(2, 32);
wav.writeUInt16LE(16, 34);
wav.write("data", 36);
wav.writeUInt32LE(pcm.length, 40);
pcm.copy(wav, 44);
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, wav, { flag: args.replace === true ? "w" : "wx" });

writeResult(output, resourcePath, { waveform, duration, sample_rate: sampleRate, bytes: wav.length });
