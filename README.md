# cortico-world-sisterdrive

> 此乃直播测试验证之后的world，和其他 Persona / 非直播场景的兼容性有待测试。

Owner: `src/definition.ts`, `plugin/`.

Cortico World for *Teach my little sister how to drive* (Steam, Unity Mono build). A BepInEx 5 plugin inside the game hooks the game's own methods with Harmony and serves them over `127.0.0.1` TCP; the World renders them as events and feeds the bot's voice to the game's voice-input entry.

## Install into the game

1. Extract BepInEx 5.4.23.x (win x64) into the game directory, next to `teach_sister_to_drive.exe`.
2. `pnpm build:plugin` (needs a .NET 8 SDK; `GameDir` in `plugin/SisterDrivePlugin.csproj` points at the install and supplies the game assemblies to compile against).
3. Copy `dist/plugin/CorticoSisterDrive.dll` to `<game>/BepInEx/plugins/`.
4. Start the game once; `BepInEx/LogOutput.log` shows `listening on 127.0.0.1:47631`. The port lives in `BepInEx/config/ai.cortico.sisterdrive.cfg` and must match `worlds.sisterdrive.port`.

Removing `winhttp.dll`, `doorstop_config.ini` and `BepInEx/` restores the unmodified game.

A Steam update can overwrite the patched install. Copy the patched game directory outside the Steam library, add `steam_appid.txt` containing `3950270` next to the exe (Steamworks needs it outside Steam's own launch), and set `worlds.sisterdrive.gameExe` to that copy's exe. The World page's 游戏 panel (`pnpm build` produces its `dist/console.js`) starts that exe with Steam running and shows whether the game runs and the plugin is connected.

## Game setup

The game needs a microphone selected on first start. Pick a device that stays silent during the stream: anything the game hears through its own microphone is sent to the sister model as well (reported as `sisterdrive.micInput`).

## What the World does

- `sisterdrive_say(text)` synthesizes `text` with the configured TTS and reference voice and hands the wav to `AIManager.SendAudio2LLM`, the same entry the game's push-to-talk uses. Transcription, the sister model, her actions and her voice all stay the game's own.
- Events: `turn` (what the game heard, the sister's line, the raw model reply, the model latency, the situation text sent with it), `line`, `mission`, `guide`, `situation`, `popup`, `fine`, `damage`, `illegalAction`, `emotion`, `stageEnd`, `scene`, `micInput`, `rateLimited`, `turnFailed`. Frame fields: [docs/protocol.md](docs/protocol.md).
- The plugin replaces `DefaultAIManager.ParseLlmReply` (S-lane stages): the original cuts each field one character before the next label, so replies without a separator between fields produced no action. Replies with separators parse to the same values.
- `sisterdrive_look` returns a screenshot and the current state; `sisterdrive_stage` restarts, advances, returns to the menu or continues.
