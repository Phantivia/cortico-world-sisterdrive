import type { WorldDefinition } from 'cortico/world.ts';
import { SISTERDRIVE_DEFAULTS, type SisterDriveConfigSection } from './config.ts';
import { SisterDriveWorld } from './world.ts';

export const SISTERDRIVE: WorldDefinition<SisterDriveConfigSection> = {
  id: 'sisterdrive',
  label: '教妹妹开车',
  defaults: () => structuredClone(SISTERDRIVE_DEFAULTS),
  create: (ctx) => new SisterDriveWorld({ cfg: ctx.cfg, timezone: ctx.timezone }),
};
