/**
 * 教妹妹开车 World 的面板 bundle：「游戏」面板显示配置的 exe 与进程、插件连接状态，
 * 并从这份 exe 启动游戏。浏览器侧只 import type 框架，数据面走 ctx.invoke。
 */
import type { ConsoleClientBundle, ConsolePanelContext } from 'cortico/web/shared/client-panel.ts';

interface GameStatus {
  exe: string;
  exists: boolean;
  running: boolean;
  connected: boolean;
}

const POLL_MS = 3000;

function mountGame(ctx: ConsolePanelContext): void {
  const { ui } = ctx;
  const card = ui.sheet({ title: '游戏', desc: '从「游戏程序」配置的 exe 启动；指向 Steam 库外的备份，Steam 更新不会覆盖插件。' });
  const pills = ui.rowbar();
  const path = ui.msgline('');
  const msg = ui.msgline('');
  const launch = ui.button('启动游戏', { variant: 'primary', onClick: () => { void start(); } });
  const bar = ui.actions();
  bar.append(launch);
  card.body.append(pills, path, bar, msg);
  ctx.root.append(card.el);

  const render = (s: GameStatus): void => {
    pills.replaceChildren(
      ui.pill(s.running ? '游戏运行中' : '游戏未运行', s.running ? 'on' : 'off'),
      ui.pill(s.connected ? '插件已连接' : '插件未连接', s.connected ? 'on' : 'off'),
    );
    path.textContent = s.exists ? s.exe : `找不到 ${s.exe}`;
    launch.disabled = !s.exists || s.running;
  };

  const refresh = async (): Promise<void> => {
    try {
      render(await ctx.invoke<GameStatus>('status'));
    } catch (err) {
      if (!ctx.signal.aborted) msg.textContent = err instanceof Error ? err.message : String(err);
    }
  };

  const start = async (): Promise<void> => {
    launch.disabled = true;
    msg.textContent = '';
    try {
      await ctx.invoke('launch');
      ui.toast('已启动游戏', 'ok');
    } catch (err) {
      if (!ctx.signal.aborted) msg.textContent = err instanceof Error ? err.message : String(err);
    }
    await refresh();
  };

  void refresh();
  ctx.interval(() => { void refresh(); }, POLL_MS);
}

const bundle: ConsoleClientBundle = {
  panels: {
    game: { mount: mountGame },
  },
};

export default bundle;
