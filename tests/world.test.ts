import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { mkdtempSync, rmSync } from 'node:fs';
import { createServer as createHttpServer, type Server as HttpServer } from 'node:http';
import { createServer, type Server, type Socket } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { ToolCallContext } from 'cortico/core/types.ts';
import { dryMountWorld, fakeWorldContext } from 'cortico/extensions/dry-mount.ts';
import { SISTERDRIVE } from '../src/definition.ts';
import { PROTOCOL } from '../src/bridge.ts';
import { spokenText } from '../src/world.ts';
import { FakeHost } from './helpers/fake-host.ts';

/** 1 秒、16 kHz 单声道 PCM16 的静音 wav。 */
function silentWav(): Buffer {
  const data = Buffer.alloc(32000);
  const h = Buffer.alloc(44);
  h.write('RIFF', 0); h.writeUInt32LE(36 + data.length, 4); h.write('WAVE', 8);
  h.write('fmt ', 12); h.writeUInt32LE(16, 16); h.writeUInt16LE(1, 20); h.writeUInt16LE(1, 22);
  h.writeUInt32LE(16000, 24); h.writeUInt32LE(32000, 28); h.writeUInt16LE(2, 32); h.writeUInt16LE(16, 34);
  h.write('data', 36); h.writeUInt32LE(data.length, 40);
  return Buffer.concat([h, data]);
}

/** 游戏内插件的替身：真 TCP，帧格式按 docs/protocol.md。 */
class FakePlugin {
  server: Server;
  socket: Socket | null = null;
  received: Array<Record<string, unknown>> = [];
  constructor() {
    this.server = createServer((socket) => {
      this.socket = socket;
      socket.setEncoding('utf8');
      let buf = '';
      socket.on('data', (chunk: string) => {
        buf += chunk;
        for (let i = buf.indexOf('\n'); i !== -1; i = buf.indexOf('\n')) {
          const frame = JSON.parse(buf.slice(0, i));
          buf = buf.slice(i + 1);
          this.received.push(frame);
          if (frame.kind === 'say') this.send({ kind: 'reply', id: frame.id, ok: true, previousTurnInFlight: false });
        }
      });
      this.send({ kind: 'hello', protocol: PROTOCOL, plugin: 'test', state: { scene: 'RoadTestScene', voiceInput: true, paused: false, speedUnit: 'km/h' } });
    });
  }
  send(frame: Record<string, unknown>): void {
    this.socket?.write(`${JSON.stringify(frame)}\n`);
  }
  listen(): Promise<number> {
    return new Promise((resolve) => this.server.listen(0, '127.0.0.1', () => resolve((this.server.address() as { port: number }).port)));
  }
}

async function until(cond: () => boolean): Promise<void> {
  for (let i = 0; i < 200 && !cond(); i++) await new Promise((r) => setTimeout(r, 10));
  expect(cond()).toBe(true);
}

let scratchDir: string;
let plugin: FakePlugin;
let tts: HttpServer;
let ttsBodies: Array<Record<string, unknown>>;
beforeEach(() => {
  scratchDir = mkdtempSync(join(tmpdir(), 'sisterdrive-world-'));
  plugin = new FakePlugin();
  ttsBodies = [];
  tts = createHttpServer((req, res) => {
    let body = '';
    req.on('data', (c) => (body += c));
    req.on('end', () => {
      ttsBodies.push(JSON.parse(body));
      res.writeHead(200, { 'Content-Type': 'audio/wav' }).end(silentWav());
    });
  });
});
afterEach(() => {
  plugin.server.close();
  tts.close();
  rmSync(scratchDir, { recursive: true, force: true });
});

describe('sisterdrive World', () => {
  it('干装载不报失败', async () => {
    const report = await dryMountWorld(SISTERDRIVE, { scratchDir });
    expect(report.failures).toEqual([]);
  });

  it('送进游戏的字去掉 vtuber_act 的表演标记', () => {
    expect(spokenText('【看向妹妹】<小声>慢一点，[吸气]前面红灯')).toBe('慢一点，前面红灯');
  });

  it('sisterdrive_say 把 TTS 合成的 wav 原样交给插件；插件回报的回合渲染成 turn 事件', async () => {
    const port = await plugin.listen();
    const ttsPort = await new Promise<number>((r) => tts.listen(0, '127.0.0.1', () => r((tts.address() as { port: number }).port)));
    const ctx = fakeWorldContext(SISTERDRIVE, { scratchDir });
    ctx.persist({ port, ttsUrl: `http://127.0.0.1:${ttsPort}`, attachImages: false });
    const world = SISTERDRIVE.create(ctx);
    const host = new FakeHost();
    await world.start(host);
    await until(() => host.events.some((e) => e.type === 'sisterdrive.connection'));

    const say = world.tools().find((t) => t.name === 'sisterdrive_say')!;
    const receipt = await say.handler({ text: '【指路】前面路口右转' }, {} as ToolCallContext);
    expect(typeof receipt === 'string' ? receipt : receipt.failed).not.toBe(true);
    expect(ttsBodies[0].input).toBe('前面路口右转');
    const sent = plugin.received.find((f) => f.kind === 'say')!;
    expect(Buffer.from(String(sent.wav), 'base64').equals(silentWav())).toBe(true);

    plugin.send({ kind: 'event', type: 'turn', scene: 'RoadTestScene', source: 'world', sayId: sent.id, heard: '前面路口右转', line: '好的哥哥', reply: 'actions: [{target_speed: 20}] sister: 好的哥哥', latencySec: 1.2 });
    await until(() => host.events.some((e) => e.type === 'sisterdrive.turn'));
    const turn = host.events.find((e) => e.type === 'sisterdrive.turn')!;
    expect(turn.text).toContain(`第 ${String(sent.id)} 句`);
    expect(turn.text).toContain('游戏听成：「前面路口右转」');
    expect(turn.text).toContain('妹妹说：「好的哥哥」');
    await world.stop();
  });
});
