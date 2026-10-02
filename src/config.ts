import type { ConfigGroup } from 'cortico/core/types.ts';

/** config.json 的 worlds.sisterdrive 段，默认禁用。 */
export interface SisterDriveConfigSection {
  enabled: boolean;
  /** 游戏内插件监听的 127.0.0.1 端口，与插件配置 BepInEx/config/ai.cortico.sisterdrive.cfg 一致。 */
  port: number;
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
  /** 场景载入、情境区、画面提示、违规倒计时、地雷爆炸、关卡结束六类事件是否附带当时的游戏截图。 */
  attachImages: boolean;
}

export const SISTERDRIVE_DEFAULTS: SisterDriveConfigSection = {
  enabled: false,
  port: 47631,
  ttsUrl: 'http://127.0.0.1:8010',
  voiceFile: '',
  voiceText: '',
  ttsInferenceTimesteps: 10,
  captureWidth: 960,
  attachImages: true,
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
        description: '场景载入、情境区、画面提示、违规倒计时、地雷爆炸、关卡结束。',
        'x-hot': true,
      },
    },
  },
};
