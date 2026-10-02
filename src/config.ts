import type { ConfigGroup } from 'cortico/core/types.ts';

/** config.json 的 worlds.sisterdrive 段，默认禁用。 */
export interface SisterDriveConfigSection {
  enabled: boolean;
  /** 游戏内插件监听的 127.0.0.1 端口，与插件配置 BepInEx/config/ai.cortico.sisterdrive.cfg 一致。 */
  port: number;
  /** 控制台「启动游戏」打开的 exe；指向 Steam 库外的备份，Steam 更新不会动它。 */
  gameExe: string;
  /** 合成送进游戏那份语音的 TTS 服务地址（OpenAI 兼容 /v1/audio/speech）。 */
  ttsUrl: string;
  /** 参考音频，单声道 PCM16 wav；留空时服务端用默认音色。 */
  voiceFile: string;
  /** 参考音频的逐字稿；填写时服务端做续写式克隆。 */
  voiceText: string;
  /** TTS 采样步数；越小越快。 */
  ttsInferenceTimesteps: number;
  /** 截图的最大宽度，像素。 */
  captureWidth: number;
  /** 场景载入、情境区、画面提示、违规倒计时、地雷爆炸、碰撞、关卡结束七类事件是否附带当时的游戏截图。 */
  attachImages: boolean;
  /** 妹妹说话、或送进游戏的话正在被听时，主视角转向妹妹。 */
  glanceAtSister: boolean;
  /** 关卡中在游戏画面右上角显示俯视小地图，范围同 topViewSpanMeters；观众和截图都看得到。 */
  minimap: boolean;
  /** 截图时另附车顶俯视图（车头朝上）。 */
  topView: boolean;
  /** 俯视图覆盖的边长，米。 */
  topViewSpanMeters: number;
  /** 关卡中每隔多少秒缓存一份最新画面，随下一次唤醒一起送达；0 关闭。 */
  liveViewSec: number;
  /** 关卡中超过这么多秒没对妹妹说话时，实时画面改为唤醒事件；每段空闲一次，0 关闭。 */
  wakeIdleSec: number;
}

export const SISTERDRIVE_DEFAULTS: SisterDriveConfigSection = {
  enabled: false,
  port: 47631,
  gameExe: 'L:\\CorticoGames\\teach sister to drive Demo\\teach_sister_to_drive.exe',
  ttsUrl: 'http://127.0.0.1:8010',
  voiceFile: '',
  voiceText: '',
  ttsInferenceTimesteps: 10,
  captureWidth: 960,
  attachImages: true,
  glanceAtSister: true,
  minimap: true,
  topView: true,
  topViewSpanMeters: 60,
  liveViewSec: 3,
  wakeIdleSec: 6,
};

export const SISTERDRIVE_CONFIG_GROUP: ConfigGroup = {
  id: 'world:sisterdrive',
  owner: 'world:sisterdrive',
  schema: {
    type: 'object',
    title: '教妹妹开车',
    properties: {
      'worlds.sisterdrive.port': {
        type: 'integer',
        minimum: 1024,
        maximum: 65535,
        title: '插件端口',
        description: '须与游戏目录 BepInEx/config/ai.cortico.sisterdrive.cfg 的 Port 一致。',
        'x-hot': false,
      },
      'worlds.sisterdrive.gameExe': {
        type: 'string',
        title: '游戏程序',
        description: '控制台「启动游戏」打开的 teach_sister_to_drive.exe。指向 Steam 库外装好插件的备份，Steam 更新不会覆盖它；Steam 客户端须在运行。',
        'x-path': { kind: 'file' },
        'x-hot': true,
      },
      'worlds.sisterdrive.ttsUrl': {
        type: 'string',
        title: 'TTS 地址',
        description: 'sisterdrive_say 用它合成送进游戏的语音。',
        'x-hot': true,
      },
      'worlds.sisterdrive.voiceFile': {
        type: 'string',
        title: '参考音频',
        description: '单声道 PCM16 wav。与 vtuber 用同一份时，游戏听到的音色与观众听到的一致。',
        'x-path': { kind: 'file' },
        'x-hot': true,
      },
      'worlds.sisterdrive.voiceText': {
        type: 'string',
        title: '参考音频逐字稿',
        'x-hot': true,
      },
      'worlds.sisterdrive.ttsInferenceTimesteps': {
        type: 'integer',
        minimum: 1,
        maximum: 50,
        title: 'TTS 采样步数',
        'x-hot': true,
      },
      'worlds.sisterdrive.captureWidth': {
        type: 'integer',
        minimum: 320,
        maximum: 1920,
        title: '截图宽度',
        'x-suffix': 'px',
        'x-hot': true,
      },
      'worlds.sisterdrive.attachImages': {
        type: 'boolean',
        title: '关键事件附截图',
        description: '场景载入、情境区、画面提示、违规倒计时、地雷爆炸、碰撞、关卡结束。',
        'x-hot': true,
      },
      'worlds.sisterdrive.glanceAtSister': {
        type: 'boolean',
        title: '说话时看向妹妹',
        'x-hot': true,
      },
      'worlds.sisterdrive.minimap': {
        type: 'boolean',
        title: '右上角小地图',
        description: '范围与俯视图相同。',
        'x-hot': true,
      },
      'worlds.sisterdrive.topView': {
        type: 'boolean',
        title: '附俯视图',
        description: '车顶正上方，车头朝上。',
        'x-hot': true,
      },
      'worlds.sisterdrive.topViewSpanMeters': {
        type: 'integer',
        minimum: 10,
        maximum: 300,
        title: '俯视图范围',
        'x-suffix': 'm',
        'x-hot': true,
      },
      'worlds.sisterdrive.liveViewSec': {
        type: 'integer',
        minimum: 0,
        maximum: 60,
        title: '实时画面间隔',
        description: '关卡中按此间隔缓存最新画面，随下一次唤醒送达；0 关闭。',
        'x-suffix': 's',
        'x-hot': true,
      },
      'worlds.sisterdrive.wakeIdleSec': {
        type: 'integer',
        minimum: 0,
        maximum: 120,
        title: '空闲唤醒',
        description: '关卡中这么久没对妹妹说话，就用实时画面唤醒一次；0 关闭。',
        'x-suffix': 's',
        'x-hot': true,
      },
    },
  },
};
