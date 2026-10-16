import { describeTools, type WorldDefinition } from 'cortico/world.ts';
import { SISTERDRIVE_DEFAULTS, type SisterDriveConfigSection } from './config.ts';
import { SISTERDRIVE_TOOL_DECLS, SisterDriveWorld } from './world.ts';

export const SISTERDRIVE: WorldDefinition<SisterDriveConfigSection> = {
  id: 'sisterdrive',
  label: '教妹妹开车',
  defaults: () => structuredClone(SISTERDRIVE_DEFAULTS),
  create: (ctx) => new SisterDriveWorld({ cfg: ctx.cfg, timezone: ctx.timezone }),
  describe: {
    tools: describeTools(SISTERDRIVE_TOOL_DECLS, {
      sisterdrive_say: {
        zh: '把 bot 对妹妹说的话合成为 bot 的语音，当作麦克风输入送进游戏。',
        en: "Speaks to the sister: the bot's words are synthesized in its voice and fed to the game as microphone input.",
      },
      sisterdrive_look: {
        zh: '截一张游戏画面，附场景、车速、妹妹情绪、罚款、碰撞次数和游戏此刻给妹妹模型的情境。',
        en: "Takes a screenshot of the game with the scene, speed, the sister's mood, fines, collision count and the situation the game would give the sister model now.",
      },
      sisterdrive_stage: {
        zh: '重开本关、进下一关、回主菜单，或在主菜单继续存档的关卡。',
        en: 'Restarts the stage, loads the next one, goes back to the main menu, or continues the saved stage from the main menu.',
      },
    }),
    events: [
      {
        type: 'sisterdrive.connection',
        description: { zh: '游戏插件连上（附当前状态）或断开。', en: 'The game plugin connected (with the current state) or disconnected.' },
        trigger: 'flush',
      },
      {
        type: 'sisterdrive.view',
        description: {
          zh: '关卡中定时截的实时画面与车况，随下一次唤醒送达；bot 太久没对妹妹说话时单独唤醒一次。',
          en: "A periodic live screenshot with the car's state during a stage, delivered with the next wake-up; it wakes the bot once when the bot has not spoken to the sister for a while.",
        },
        tags: ['snapshot'],
      },
      {
        type: 'sisterdrive.scene',
        description: { zh: '载入了新场景。', en: 'A new scene loaded.' },
        trigger: 'flush',
      },
      {
        type: 'sisterdrive.turn',
        description: {
          zh: '妹妹对一句语音的回应：游戏听成什么、她说的话、游戏模型的回复与用时。',
          en: "The sister's response to a voice line: what the game heard, what she said, and the game model's reply and latency.",
        },
        trigger: 'flush',
      },
      {
        type: 'sisterdrive.turnFailed',
        description: { zh: '游戏模型请求失败。', en: "The game model's request failed." },
        trigger: 'flush',
      },
      {
        type: 'sisterdrive.rateLimited',
        description: { zh: '游戏的语音请求到了每分钟 30 次上限，这句被游戏丢弃。', en: 'The game hit its limit of 30 voice requests a minute and dropped this line.' },
        trigger: 'flush',
      },
      {
        type: 'sisterdrive.micInput',
        description: { zh: '游戏麦克风收到一段不是 sisterdrive_say 送来的语音。', en: 'The game microphone picked up speech that sisterdrive_say did not send.' },
        trigger: 'flush',
      },
      {
        type: 'sisterdrive.plan',
        description: { zh: '妹妹的驾驶计划开始、走到某一步、走完或被打断。', en: "The sister's driving plan started, reached a step, finished or was interrupted." },
      },
      {
        type: 'sisterdrive.line',
        description: { zh: '妹妹在回应之外说的一句话。', en: 'A line the sister said outside a response.' },
        trigger: 'debounce',
      },
      {
        type: 'sisterdrive.emotion',
        description: { zh: '妹妹的情绪变了。', en: "The sister's mood changed." },
        trigger: 'debounce',
      },
      {
        type: 'sisterdrive.mission',
        description: { zh: '游戏给出的任务。', en: 'A mission the game set.' },
        trigger: 'flush',
      },
      {
        type: 'sisterdrive.brief',
        description: { zh: '本关游戏给妹妹模型的说明节选：目标与动作刻度。', en: "An excerpt of this stage's instructions to the sister model: the goal and action scale." },
        trigger: 'flush',
      },
      {
        type: 'sisterdrive.guide',
        description: { zh: '画面上的提示。', en: 'An on-screen hint.' },
        trigger: 'flush',
      },
      {
        type: 'sisterdrive.situation',
        description: { zh: '车进入情境区，附游戏给妹妹模型的情境描述。', en: "The car entered a situation zone, with the game's situation text for the sister model." },
        trigger: 'flush',
      },
      {
        type: 'sisterdrive.popup',
        description: { zh: '游戏弹窗。', en: 'A game popup.' },
        trigger: 'flush',
      },
      {
        type: 'sisterdrive.fine',
        description: { zh: '罚款增加，附累计。', en: 'A fine was added, with the running total.' },
        trigger: 'debounce',
      },
      {
        type: 'sisterdrive.damage',
        description: { zh: '碰撞计数加一，附本关累计。', en: "The collision count went up, with this stage's total." },
        trigger: 'flush',
      },
      {
        type: 'sisterdrive.collision',
        description: { zh: '车撞上锥桶或障碍物；3 秒内连着撞只有第一下唤醒。', en: 'The car hit a cone or an obstacle; within 3 seconds only the first hit wakes the bot.' },
      },
      {
        type: 'sisterdrive.mine',
        description: { zh: '车压到地雷；3 秒内连着压只有第一下唤醒。', en: 'The car hit a mine; within 3 seconds only the first hit wakes the bot.' },
      },
      {
        type: 'sisterdrive.illegalAction',
        description: { zh: '违规倒计时开始。', en: 'A violation countdown started.' },
        trigger: 'preempt',
      },
      {
        type: 'sisterdrive.stageEnd',
        description: { zh: '关卡结束：通过，或失败及原因。', en: 'The stage ended: cleared, or failed with the cause.' },
        trigger: 'flush',
      },
    ],
  },
};
