/** 合成送进游戏的语音：POST {ttsUrl}/v1/audio/speech，返回整段 wav。 */
import { readFileSync } from 'node:fs';
import type { SisterDriveConfigSection } from './config.ts';

const TIMEOUT_MS = 30_000;

export interface Speech {
  wav: Buffer;
  durationMs: number;
}

/** PCM wav 的时长；只读 fmt 与 data 块头。 */
export function wavInfo(wav: Buffer): { format: number; channels: number; sampleRate: number; bits: number; durationMs: number } {
  if (wav.toString('ascii', 0, 4) !== 'RIFF' || wav.toString('ascii', 8, 12) !== 'WAVE') throw new Error('不是 wav 文件');
  let format = 0, channels = 0, sampleRate = 0, bits = 0, dataBytes = 0;
  for (let off = 12; off + 8 <= wav.length; ) {
    const id = wav.toString('ascii', off, off + 4);
    const size = wav.readUInt32LE(off + 4);
    if (id === 'fmt ') {
      format = wav.readUInt16LE(off + 8);
      channels = wav.readUInt16LE(off + 10);
      sampleRate = wav.readUInt32LE(off + 12);
      bits = wav.readUInt16LE(off + 22);
    } else if (id === 'data') {
      dataBytes = Math.min(size, wav.length - off - 8);
      break;
    }
    off += 8 + size + (size % 2);
  }
  const bytesPerSec = sampleRate * channels * (bits / 8);
  return { format, channels, sampleRate, bits, durationMs: bytesPerSec ? Math.round((dataBytes / bytesPerSec) * 1000) : 0 };
}

export class SpeechClient {
  private voice: { path: string; b64: string } | null = null;

  constructor(private readonly cfg: SisterDriveConfigSection) {}

  async synth(text: string, signal?: AbortSignal): Promise<Speech> {
    const body: Record<string, unknown> = {
      model: 'voxcpm2',
      input: text,
      voice: 'default',
      response_format: 'wav',
      inference_timesteps: this.cfg.ttsInferenceTimesteps,
    };
    const ref = this.referenceAudio();
    if (ref) {
      body.reference_audio = ref;
      if (this.cfg.voiceText) body.prompt_text = this.cfg.voiceText;
    }
    const timeout = AbortSignal.timeout(TIMEOUT_MS);
    const res = await fetch(`${this.cfg.ttsUrl.replace(/\/$/, '')}/v1/audio/speech`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      signal: signal ? AbortSignal.any([signal, timeout]) : timeout,
    });
    if (!res.ok) throw new Error(`TTS ${res.status}: ${(await res.text().catch(() => '')).slice(0, 200)}`);
    const wav = Buffer.from(await res.arrayBuffer());
    return { wav, durationMs: wavInfo(wav).durationMs };
  }

  /** 服务端只认单声道 PCM16 的参考音频，其他格式在这里拒绝并说明。 */
  private referenceAudio(): string | null {
    const path = this.cfg.voiceFile;
    if (!path) return null;
    if (this.voice?.path === path) return this.voice.b64;
    const bytes = readFileSync(path);
    const info = wavInfo(bytes);
    if (info.format !== 1 || info.bits !== 16 || info.channels !== 1) {
      throw new Error(`参考音频须为单声道 PCM16 wav，${path} 是 format=${info.format} ${info.bits}bit ${info.channels}ch`);
    }
    this.voice = { path, b64: bytes.toString('base64') };
    return this.voice.b64;
  }
}
