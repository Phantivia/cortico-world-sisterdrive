/** TCP 客户端：连游戏内插件（docs/protocol.md），断线后按固定间隔重连，直到 close()。 */
import { createConnection, type Socket } from 'node:net';
import { EventEmitter } from 'node:events';

export const PROTOCOL = 1;
const RECONNECT_MS = 2000;

/** 插件对状态的报告；字段缺失表示该场景没有对应对象。 */
export interface GameState {
  scene: string;
  voiceInput: boolean;
  paused: boolean;
  speedUnit: string;
  speed?: number;
  emotion?: string;
  fine?: number;
  damageCount?: number;
  /** 当前关卡的 AIManager 会随语音一起发给游戏模型的情境文本。 */
  context?: string;
  /** 停车目标：相对车头的方位（右为正）与距离，及游戏通关判定用到的量。 */
  goal?: {
    distanceM: number;
    bearingDeg: number;
    inside: boolean;
    stayedSec: number;
    requiredStaySec: number;
    rearIn?: boolean;
    headingErrorDeg?: number;
    allowedErrorDeg?: number;
  };
  contextError?: string;
}

export interface GameFrame {
  kind: 'hello' | 'reply' | 'event';
  id?: number;
  ok?: boolean;
  error?: string;
  type?: string;
  scene?: string;
  state?: GameState;
  [key: string]: unknown;
}

/** 事件：`hello`(frame)、`event`(frame)、`disconnect`(error)。 */
export class GameBridge extends EventEmitter {
  private socket: Socket | null = null;
  private nextId = 1;
  private pending = new Map<number, { resolve: (f: GameFrame) => void; reject: (e: Error) => void; timer: NodeJS.Timeout }>();
  private closed = true;
  private retry: NodeJS.Timeout | null = null;
  hello: GameFrame | null = null;

  get connected(): boolean {
    return this.hello !== null;
  }

  open(port: number): void {
    this.closed = false;
    this.dial(port);
  }

  close(): void {
    this.closed = true;
    if (this.retry) clearTimeout(this.retry);
    this.retry = null;
    this.drop(new Error('bridge closed'));
  }

  request(payload: Record<string, unknown>, timeoutMs: number): Promise<GameFrame> {
    const socket = this.socket;
    if (!socket || !this.hello) return Promise.reject(new Error('游戏未连接'));
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`游戏 ${timeoutMs / 1000} 秒内没有处理 ${String(payload.kind)} 请求（游戏主线程未响应）`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      socket.write(`${JSON.stringify({ ...payload, id })}\n`);
    });
  }

  private dial(port: number): void {
    if (this.closed) return;
    const socket = createConnection({ host: '127.0.0.1', port });
    this.socket = socket;
    socket.setNoDelay(true);
    socket.setEncoding('utf8');
    let buffer = '';
    socket.on('data', (chunk: string) => {
      buffer += chunk;
      for (let end = buffer.indexOf('\n'); end !== -1; end = buffer.indexOf('\n')) {
        const line = buffer.slice(0, end);
        buffer = buffer.slice(end + 1);
        if (line) this.receive(JSON.parse(line) as GameFrame);
      }
    });
    socket.on('error', () => {});
    socket.on('close', () => {
      if (this.socket !== socket) return;
      const wasConnected = this.hello !== null;
      this.drop(new Error('游戏连接已断开'));
      if (wasConnected) this.emit('disconnect', new Error('游戏连接已断开'));
      if (!this.closed) this.retry = setTimeout(() => this.dial(port), RECONNECT_MS);
    });
  }

  private drop(error: Error): void {
    const socket = this.socket;
    this.socket = null;
    this.hello = null;
    for (const p of this.pending.values()) {
      clearTimeout(p.timer);
      p.reject(error);
    }
    this.pending.clear();
    socket?.destroy();
  }

  private receive(frame: GameFrame): void {
    if (frame.kind === 'hello') {
      this.hello = frame;
      this.emit('hello', frame);
    } else if (frame.kind === 'reply' && frame.id !== undefined) {
      const p = this.pending.get(frame.id);
      if (!p) return;
      this.pending.delete(frame.id);
      clearTimeout(p.timer);
      p.resolve(frame);
    } else if (frame.kind === 'event') {
      this.emit('event', frame);
    }
  }
}
