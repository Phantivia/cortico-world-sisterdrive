/**
 * 教妹妹开车 World。游戏内 BepInEx 插件把游戏方法调用转成事件帧，World 渲染后投递；
 * sisterdrive_say 合成语音并交给游戏原有的语音入口（AIManager.SendAudio2LLM），
 * 之后的识别、妹妹模型、动作执行全部走游戏自己的流程。
 */
import { fileURLToPath } from 'node:url';
import type { BlobInput, ToolCallContext, ToolDef, ToolOutcome, TriggerMode, World, WorldConsoleDecl, WorldHost } from 'cortico/core/types.ts';
import { nowIso } from 'cortico/core/util.ts';
import { SISTERDRIVE_CONFIG_GROUP, type SisterDriveConfigSection } from './config.ts';
import { GameBridge, PROTOCOL, type GameFrame, type GameState } from './bridge.ts';
import { SpeechClient } from './tts.ts';

const ENV_PROMPT_FILE = fileURLToPath(new URL('./ENV_PROMPT.md', import.meta.url));
const TAG = '[教妹妹开车]';
// 插件在游戏主线程处理请求；游戏主线程卡顿期间请求会排队。
const REQUEST_TIMEOUT_MS = 10_000;
const RAW_REPLY_MAX_CHARS = 400;

/** cfg.attachImages 打开时附截图的事件。 */
const IMAGE_EVENTS = new Set(['scene', 'situation', 'guide', 'illegalAction', 'mine', 'stageEnd']);

const EMOTION: Record<string, string> = { Smile: '平静', Angry: '生气', Anxious: '紧张' };

const TRIGGER: Record<string, TriggerMode> = {
  turn: 'flush', line: 'debounce', mission: 'flush', guide: 'flush', situation: 'flush', popup: 'flush',
  fine: 'debounce', damage: 'flush', illegalAction: 'preempt', mine: 'preempt', emotion: 'debounce', stageEnd: 'flush',
  scene: 'flush', micInput: 'flush', rateLimited: 'flush', turnFailed: 'flush',
};

const SAY_REFUSAL: Record<string, string> = {
  noVoiceInput: '当前场景没有语音输入（菜单、加载或过场），对讲键松开照常发出',
  inputDisabled: '游戏此刻关闭了语音输入（脚本演出中或关卡已结束），对讲键松开照常发出，等这个键的开场画面会继续',
  paused: '游戏处于暂停',
};

/** vtuber_act 的表演标记：【】动作、<>行内、[]发声；送进游戏的只保留说出口的字。 */
export function spokenText(script: string): string {
  return script.replace(/【[^】]*】|<[^>]*>|\[[^\]]*\]/g, '').replace(/\s+/g, ' ').trim();
}

export function renderState(s: GameState | undefined): string {
  if (!s) return '';
  const parts = [`场景 ${s.scene}`];
  if (s.speed !== undefined) parts.push(`车速 ${s.speed} ${s.speedUnit}`);
  if (s.emotion) parts.push(`妹妹情绪 ${EMOTION[s.emotion] ?? s.emotion}`);
  if (s.fine !== undefined) parts.push(`罚款累计 ${s.fine}`);
  if (s.damageCount !== undefined) parts.push(`碰撞 ${s.damageCount} 次`);
  parts.push(s.paused ? '已暂停' : s.voiceInput ? '可接收语音' : '不接收语音');
  return parts.join(' · ');
}

export function renderEvent(f: GameFrame): string | null {
  const str = (k: string): string => (typeof f[k] === 'string' ? (f[k] as string) : '');
  switch (f.type) {
    case 'turn': {
      const head = f.source === 'world' ? `${TAG} 妹妹对 sisterdrive_say 第 ${String(f.sayId)} 句的回应` : `${TAG} 游戏麦克风那句语音的回应`;
      const lines = [head, `游戏听成：「${str('heard') || '（无转写）'}」`];
      if (str('line')) lines.push(`妹妹说：「${str('line')}」`);
      const raw = str('reply');
      if (raw) lines.push(`游戏模型原始回复：${raw.length > RAW_REPLY_MAX_CHARS ? `${raw.slice(0, RAW_REPLY_MAX_CHARS)}…` : raw}`);
      lines.push(`游戏模型用时 ${Number(f.latencySec ?? 0).toFixed(1)} 秒`);
      if (str('context')) lines.push(`游戏随语音附给妹妹模型的情境：${str('context')}`);
      return lines.join('\n');
    }
    case 'line': return `${TAG} 妹妹说：「${str('text')}」`;
    case 'mission': return `${TAG} 任务：${str('text')}`;
    case 'guide': return `${TAG} 画面提示：${str('text') || str('key')}`;
    case 'situation': return `${TAG} 进入情境区，游戏给妹妹模型的情境描述：${str('text')}`;
    case 'popup': return `${TAG} 弹窗：${str('text')}`;
    case 'fine': return `${TAG} 罚款 +${String(f.amount)}，累计 ${String(f.total)}`;
    case 'damage': return `${TAG} 碰撞，本关累计 ${String(f.count)} 次`;
    case 'mine': return `${TAG} 车压到地雷，爆炸了\n${renderState(f.state)}`;
    case 'illegalAction': return `${TAG} 违规倒计时开始：${Number(f.timeLeftSec ?? 0).toFixed(1)} 秒`;
    case 'emotion': return `${TAG} 妹妹情绪 ${EMOTION[str('from')] ?? str('from')} → ${EMOTION[str('to')] ?? str('to')}`;
    case 'stageEnd': return `${TAG} 关卡结束：${f.result === 'clear' ? '通过' : '失败'}\n${renderState(f.state)}`;
    case 'scene': return `${TAG} 场景载入：${str('scene')}\n${renderState(f.state)}`;
    case 'micInput': return `${TAG} 游戏麦克风收到一段语音（不是 sisterdrive_say 送的），游戏照常交给妹妹的模型。`;
    case 'rateLimited': return `${TAG} 游戏的语音请求达到每分钟 30 次上限，这句语音被游戏丢弃。`;
    case 'turnFailed': return `${TAG} 游戏模型请求失败：${str('error') || '未知错误'}`;
    default: return null;
  }
}

export interface SisterDriveWorldOptions {
  cfg: SisterDriveConfigSection;
  timezone: string;
}

export class SisterDriveWorld implements World {
  readonly id = 'sisterdrive';
  private readonly bridge = new GameBridge();
  private readonly speech: SpeechClient;
  private host: WorldHost | null = null;
  private lamp: 'offline' | 'loading' | 'online' | 'error' = 'offline';
  private detail = '';
  private scene = '';

  constructor(private readonly opts: SisterDriveWorldOptions) {
    this.speech = new SpeechClient(opts.cfg);
    this.bridge.on('hello', (f: GameFrame) => void this.onHello(f));
    this.bridge.on('event', (f: GameFrame) => void this.onGameEvent(f));
    this.bridge.on('disconnect', () => {
      this.lamp = 'loading';
      this.detail = '等待游戏';
      void this.push('connection', `${TAG} 游戏连接已断开，插件可连时自动重连。`, 'flush', true);
    });
  }

  envPromptVars(): Record<string, string> {
    return {};
  }

  tools(): ToolDef[] {
    return [this.sayTool(), this.lookTool(), this.stageTool()];
  }

  console(): WorldConsoleDecl {
    return {
      lamps: [{ label: '游戏插件', state: this.lamp, hint: this.detail || undefined }],
      badges: [{ label: '场景', value: this.scene || '—' }],
      config: [SISTERDRIVE_CONFIG_GROUP],
      promptDocs: [{
        key: 'worlds.sisterdrive.envPrompt',
        title: '教妹妹开车 · 环境提示词',
        description: '对妹妹说话的方式、事件与工具的含义。',
        path: ENV_PROMPT_FILE,
        role: 'envPrompt',
      }],
    };
  }

  async start(host: WorldHost): Promise<void> {
    this.host = host;
    this.lamp = 'loading';
    this.detail = '等待游戏';
    this.bridge.open(this.opts.cfg.port);
  }

  async stop(): Promise<void> {
    this.bridge.close();
    this.host = null;
    this.lamp = 'offline';
  }

  // ---- 游戏 → 事件 ------------------------------------------------------------------

  private async onHello(f: GameFrame): Promise<void> {
    if (f.protocol !== PROTOCOL) {
      this.lamp = 'error';
      this.detail = `插件协议 ${String(f.protocol)}，World 需要 ${PROTOCOL}`;
      this.bridge.close();
      return;
    }
    this.lamp = 'online';
    this.detail = `插件 ${String(f.plugin)}`;
    this.scene = f.state?.scene ?? '';
    await this.push('connection', `${TAG} 游戏已连接。\n${renderState(f.state)}`, 'flush', true);
  }

  private async onGameEvent(f: GameFrame): Promise<void> {
    if (f.scene) this.scene = f.scene;
    const text = renderEvent(f);
    if (!text || !f.type) return;
    const blobs = this.opts.cfg.attachImages && IMAGE_EVENTS.has(f.type) ? await this.capture() : undefined;
    await this.push(f.type, text, TRIGGER[f.type] ?? 'debounce', false, blobs);
  }

  private async capture(): Promise<BlobInput[] | undefined> {
    if (!this.host?.modelFacts.accepts('image/jpeg')) return undefined;
    try {
      const rep = await this.bridge.request({ kind: 'look', maxWidth: this.opts.cfg.captureWidth }, REQUEST_TIMEOUT_MS);
      if (!rep.ok || typeof rep.jpeg !== 'string') return undefined;
      return [{ bytes: Buffer.from(rep.jpeg, 'base64'), mime: 'image/jpeg', name: 'sisterdrive.jpg', fallbackText: '游戏画面' }];
    } catch (err) {
      this.host?.log.warn('sisterdrive.capture', String(err));
      return undefined;
    }
  }

  private async push(noun: string, text: string, trigger: TriggerMode, internal = false, blobs?: BlobInput[]): Promise<void> {
    try {
      await this.host?.pushEvent({
        type: `sisterdrive.${noun}`,
        source: this.id,
        senderKey: this.id,
        ts: nowIso(this.opts.timezone),
        text,
        ...(internal ? { origin: 'internal' as const } : {}),
        ...(blobs ? { blobs } : {}),
      }, { trigger });
    } catch (err) {
      this.host?.log.warn('sisterdrive.event', String(err));
    }
  }

  // ---- 工具 -------------------------------------------------------------------------

  private requireGame(): ToolOutcome | null {
    if (this.bridge.connected) return null;
    return { text: '未执行：游戏未连接（游戏没开，或插件没装）。', failed: true };
  }

  private sayTool(): ToolDef {
    return {
      name: 'sisterdrive_say',
      description: 'Speak to the sister in the game: the text is synthesized in your voice and fed to the game as microphone input. Returns once the game accepted the audio; her reaction arrives later as a sisterdrive.turn event.',
      tags: ['act'],
      parameters: {
        type: 'object',
        additionalProperties: false,
        properties: {
          text: { type: 'string', description: 'The exact words addressed to the sister, as spoken in vtuber_act. Performance markers are removed before synthesis.' },
        },
        required: ['text'],
      },
      handler: async (args, ctx) => this.say(String(args.text ?? ''), ctx),
    };
  }

  private async say(raw: string, ctx: ToolCallContext): Promise<ToolOutcome> {
    const missing = this.requireGame();
    if (missing) return missing;
    const text = spokenText(raw);
    if (!text) return { text: '未执行：去掉表演标记后没有要说的字。', failed: true };
    const t0 = Date.now();
    let speech;
    try {
      speech = await this.speech.synth(text, ctx.signal);
    } catch (err) {
      return { text: `未执行：语音合成失败（${err instanceof Error ? err.message : String(err)}）。`, failed: true };
    }
    const synthMs = Date.now() - t0;
    let rep;
    try {
      rep = await this.bridge.request({ kind: 'say', text, wav: speech.wav.toString('base64') }, REQUEST_TIMEOUT_MS);
    } catch (err) {
      return { text: `结果未确认：${err instanceof Error ? err.message : String(err)}。音频已发出，游戏恢复后可能仍会处理「${text}」，是否处理以 sisterdrive.turn 为准。`, failed: true };
    }
    if (!rep.ok) {
      return { text: `未执行：${SAY_REFUSAL[rep.error ?? ''] ?? rep.error}。「${text}」没有送进游戏。`, failed: true };
    }
    const lines = [`已送进游戏（第 ${String(rep.id)} 句）：「${text}」，语音 ${(speech.durationMs / 1000).toFixed(1)} 秒，合成用时 ${(synthMs / 1000).toFixed(1)} 秒。`];
    if (rep.previousTurnInFlight) lines.push('送达时游戏还在处理上一句。');
    return { text: lines.join('\n') };
  }

  private lookTool(): ToolDef {
    return {
      name: 'sisterdrive_look',
      description: 'Current game screen plus scene, speed, sister emotion, fines, collisions, and the situation text the game would send to the sister model right now.',
      tags: ['read', 'snapshot'],
      parameters: { type: 'object', additionalProperties: false, properties: {} },
      handler: async () => {
        const missing = this.requireGame();
        if (missing) return missing;
        const rep = await this.bridge.request({ kind: 'look', maxWidth: this.opts.cfg.captureWidth }, REQUEST_TIMEOUT_MS);
        if (!rep.ok) return { text: `未取得画面：${rep.error}`, failed: true };
        const lines = [`${TAG} ${renderState(rep.state)}`];
        if (rep.state?.context) lines.push(`妹妹模型此刻会收到的情境：${rep.state.context}`);
        return {
          text: lines.join('\n'),
          blobs: [{ bytes: Buffer.from(String(rep.jpeg), 'base64'), mime: 'image/jpeg', name: 'sisterdrive.jpg', fallbackText: `游戏画面 ${String(rep.width)}×${String(rep.height)}` }],
        };
      },
    };
  }

  private stageTool(): ToolDef {
    return {
      name: 'sisterdrive_stage',
      description: 'Stage flow: restart = replay the current stage; next = load the next stage; menu = back to the main menu; continue = from the main menu, resume at the saved stage.',
      tags: ['act'],
      barrierAfter: true,
      parameters: {
        type: 'object',
        additionalProperties: false,
        properties: { action: { type: 'string', enum: ['restart', 'next', 'menu', 'continue'] } },
        required: ['action'],
      },
      handler: async (args) => {
        const missing = this.requireGame();
        if (missing) return missing;
        const rep = await this.bridge.request({ kind: 'stage', action: args.action }, REQUEST_TIMEOUT_MS);
        if (!rep.ok) return { text: `未执行：${rep.error}`, failed: true };
        return `已执行 ${String(args.action)}；新场景载入后会收到 sisterdrive.scene 事件。`;
      },
    };
  }
}
