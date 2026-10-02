# Plugin protocol

Owner: `plugin/Plugin.cs`, `plugin/Patches.cs`, `src/bridge.ts`.

UTF-8 JSON, one frame per line, over TCP on `127.0.0.1:<Port>`. The plugin is the server and serves one client; a new connection replaces the old one. Protocol version: `1`.

## Plugin → World

| kind | fields |
|---|---|
| `hello` | `protocol`, `plugin` (version), `game` (`Application.version`), `state` |
| `reply` | `id` of the request, `ok`, `error` when not ok, request-specific fields |
| `event` | `type`, `scene` (active scene name), `atMs` (game realtime clock), type-specific fields |

`state`: `scene`, `voiceInput` (an `AIManager` exists and accepts input), `paused`, `speedUnit`, and when present `speed` (speedometer value), `emotion` (`Smile`/`Angry`/`Anxious`), `fine`, `damageCount`, `context` (what the scene's `AIManager.GetContextInfo()` returns now).

Event types and their source:

| type | game method | fields |
|---|---|---|
| `turn` | `ModelRouter.CommitAndReturn` + the following `AiVoiceService.RunTTS` | `source` (`world`/`mic`), `sayId`, `heard` (`UserTranscript`), `reply` (model text), `line` (spoken reply, absent when none within 0.5 s), `latencySec`, `context` |
| `turnFailed` | `ModelRouter.ApplyPlayResult` with a failed result | `source`, `error`, `latencySec` |
| `line` | `AiVoiceService.RunTTS` / `RunPreparedTTS` outside a turn | `text` |
| `micInput` | `AIManager.SendAudio2LLM` not called by the plugin | `bytes` |
| `rateLimited` | `AIManager.CheckRequestLimitReached` returned true | `source` |
| `mission` | `MissionPanel.SetMission` | `text` |
| `guide` | `RoadTestMissionTexts.ShowGuide`, `TutorialUIManager.ShowGuideUI` | `key`, `text` (localized) |
| `situation` | `RoadTestSituationPrompt` / `ParkingSituationPrompt` `OnTriggerEnter` when the player enters | `text` (`prompt2make`) |
| `popup` | `MessagePopup.Show` | `text` |
| `fine` | `ScoreStatus.AddFine` | `amount`, `total` |
| `damage` | `ScoreStatus.AddDamageCount` | `count` |
| `illegalAction` | `IllegalActionTimeoutUI.ShowIllegalActionTimeoutUI` when it was hidden | `timeLeftSec` |
| `emotion` | `EmotionStatus.SetEmotion` with a different value | `from`, `to` |
| `stageEnd` | `UIManager.ShowGameOverUI` / `ShowMissionCompleteUI` when neither panel is shown | `result` (`fail`/`clear`), `state` |
| `scene` | `SceneManager.sceneLoaded` (single mode) | `state` |

## World → Plugin

| kind | fields | reply |
|---|---|---|
| `say` | `id`, `wav` (base64 wav), `text` (logged only) | `previousTurnInFlight`; errors `noVoiceInput`, `inputDisabled`, `paused` |
| `look` | `id`, `maxWidth` | `jpeg` (base64), `width`, `height`, `state` |
| `stage` | `id`, `action`: `restart`/`next`/`menu`/`continue` | errors `notInStage`, `noSceneLoader`, `notInMainMenu` |

A `say` that is accepted produces a `turn` (or `turnFailed`/`rateLimited`) whose `sayId` equals the request `id`.
