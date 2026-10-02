# Plugin protocol

Owner: `plugin/Plugin.cs`, `plugin/Patches.cs`, `src/bridge.ts`.

UTF-8 JSON, one frame per line, over TCP on `127.0.0.1:<Port>`. The plugin is the server and serves one client; a new connection replaces the old one. Protocol version: `1`.

## Plugin → World

| kind | fields |
|---|---|
| `hello` | `protocol`, `plugin` (version), `game` (`Application.version`), `state` |
| `reply` | `id` of the request, `ok`, `error` when not ok, request-specific fields |
| `event` | `type`, `scene` (active scene name), `atMs` (game realtime clock), type-specific fields |

`state`: `scene`, `voiceInput` (an `AIManager` exists and accepts input), `paused`, `speedUnit`, and when present `speed` (speedometer value), `emotion` (`Smile`/`Angry`/`Anxious`), `fine`, `damageCount`, `context` (what the scene's `AIManager.GetContextInfo()` returns now), `goal` (parking TriggerZone relative to the car: `distanceM`, `bearingDeg` right-positive, `inside`, `stayedSec`/`requiredStaySec`, and for rear-in zones `headingErrorDeg`/`allowedErrorDeg`).

Event types and their source:

| type | game method | fields |
|---|---|---|
| `turn` | `ModelRouter.CommitAndReturn` + the following `AiVoiceService.RunTTS` | `source` (`world`/`mic`), `sayId`, `heard` (`UserTranscript`), `reply` (model text), `line` (spoken reply, absent when none within 0.5 s), `latencySec`, `context` |
| `turnFailed` | `ModelRouter.ApplyPlayResult` with a failed result | `source`, `error`, `latencySec` |
| `line` | `AiVoiceService.RunTTS` / `RunPreparedTTS` outside a turn | `text` |
| `micInput` | `AIManager.SendAudio2LLM` not called by the plugin | `bytes` |
| `rateLimited` | `AIManager.CheckRequestLimitReached` returned true | `source` |
| `brief` | `ModelRouter._activePromptText` changed (polled each second) | `text`: the #Goal and action-scale sections of that prompt, verbatim |
| `mission` | `MissionPanel.SetMission` | `text` |
| `guide` | `RoadTestMissionTexts.ShowGuide`, `TutorialUIManager.ShowGuideUI` | `key`, `text` (localized) |
| `situation` | `RoadTestSituationPrompt` / `ParkingSituationPrompt` `OnTriggerEnter` when the player enters | `text` (`prompt2make`) |
| `popup` | `MessagePopup.Show` | `text` |
| `fine` | `ScoreStatus.AddFine` | `amount`, `total` |
| `damage` | `ScoreStatus.AddDamageCount` | `count` |
| `illegalAction` | `IllegalActionTimeoutUI.ShowIllegalActionTimeoutUI` when it was hidden | `timeLeftSec` |
| `mine` | `SLaneMineSceneController.OnMineHit` when the hit count rises | `hits`, `maxHits`, `state` |
| `emotion` | `EmotionStatus.SetEmotion` with a different value | `from`, `to` |
| `stageEnd` | `UIManager.ShowGameOverUI` / `ShowMissionCompleteUI` when neither panel is shown | `result` (`fail`/`clear`), `state`; on fail `cause` (class that called ShowGameOverUI) and `reason` (its `gameOverReason`, when it has one) |
| `scene` | `SceneManager.sceneLoaded` (single mode) | `state` |

## World → Plugin

| kind | fields | reply |
|---|---|---|
| `config` | `id`, `glance` (turn the gameplay camera left toward the sister while she speaks or a sent line plays), `minimap`, `minimapSpanM` (top-right on-screen top view) | — |
| `say` | `id`, `wav` (base64 wav), `text` (logged only), `glanceSec` (how long the camera looks at the sister) | `previousTurnInFlight`; errors `noVoiceInput`, `inputDisabled`, `paused` |
| `look` | `id`, `maxWidth`, `top`, `topWidth`, `topHeightM`, `topSpanM` | `jpeg` (base64), `width`, `height`, `state`; with `top`: `topJpeg` (orthographic, car heading up), `topSpanM` |
| `stage` | `id`, `action`: `restart`/`next`/`menu`/`continue`, and `newgame` (operator only: New Game from the tutorial) | errors `notInStage`, `noSceneLoader`, `notInMainMenu` |

Every `say` outside pause also counts as one release of the push-to-talk key (Space, axis `Recording`) for the stage scripts that wait for it (`TutorialController`, `CameraOverviewController`, `ParkingSceneGameManager`); `AIManager` still reads only the real key. A `say` that is accepted produces a `turn` (or `turnFailed`/`rateLimited`) whose `sayId` equals the request `id`.
