# Meeting Copilot user guide

Commands and local paths in this guide are relative to the repository root.

A C# / WPF desktop overlay for **live English meetings**, not post-meeting summaries.
It captures a selected Windows playback device and optionally a microphone, sends
16 kHz mono audio to **Azure Speech continuous streaming** (recommended, new-setup default)
or MAI-Transcribe-2 short uploads, and sends finalized utterances with rolling
meeting context to **your selected model through the Foundry Responses API with an API key**
(new-setup default) or Microsoft Entra authentication. Legacy Azure OpenAI Chat Completions
is also available. **Google Gemini, Anthropic Claude, xAI Grok and OpenAI-compatible
cloud/local servers** can now be selected without a Foundry resource. Saved connection choices are preserved. One grounded recommendation,
question, potential gap, or alternative appears in a movable, resizable, always-on-top window.

**No speech recognizer provides 100% accuracy.** This app cannot promise complete voice
detection, perfect transcription, complete context recall, or correct design advice.
Interim recognition is shown in diagnostics but never treated as finalized evidence.
This version transcribes English; it does **not** translate other languages.

## Run on Windows

Prerequisites:

- Windows 11, .NET 10 SDK (or .NET 10 Desktop Runtime for a framework-dependent publish).
- Microsoft Visual C++ 2015-2022 Redistributable for the target architecture, required by
  the native Azure Speech SDK.
- An approved Azure Speech resource and key. Streaming can use the resource's HTTPS
  endpoint directly, without a region; an optional region override preserves regional
  connections. MAI requires a resource endpoint in a region supporting the preview model.
- An approved model provider connection (see below), or a Foundry OpenAI v1 endpoint, deployed model, and either a resource API key
  with key authentication enabled or an Azure identity authorized to invoke it.
  Entra mode uses `DefaultAzureCredential`; API-key mode does not request an Entra token
  or require Azure CLI, Visual Studio or VS Code setup.
  Use the **deployment name**, not just a model family name.
  No cloud resources are provisioned by this app.

```powershell
dotnet run --project .\src\MeetingCopilot.Desktop
```

Publish a framework-dependent **Windows ARM64** build to
`artifacts\multi-provider-win-arm64\MeetingCopilot.Desktop.exe` using the build commands below.
Build outputs are not committed to this repository.
Preview screenshots are in `artifacts\screenshots`.

### Two-minute demo video

`artifacts\demo-video\Meeting-Copilot-Demo.mp4` is a 120-second, 1080p narrated
walkthrough with burned-in English captions. The matching `.en.srt` is available
alongside it. It uses actual WPF screens, prewritten sample suggestions, simulated
voice recognition and actual offline teleprompter motion, not a real meeting recording.
Narration is synthesized locally with the Windows Microsoft Zira voice.

To regenerate on Windows with FFmpeg/FFprobe and that voice installed:

```powershell
$env:MEETINGCOPILOT_DEMO_CAPTURE_DIR = Join-Path (Get-Location) 'artifacts\demo-video\captures'
dotnet test .\tests\MeetingCopilot.Tests -c Release --filter 'FullyQualifiedName~WpfSetupAndPreviewRenderAndControlsWorkWithoutCredentials'
Remove-Item Env:\MEETINGCOPILOT_DEMO_CAPTURE_DIR
powershell.exe -NoProfile -File .\tools\Build-DemoVideo.ps1
```

The opt-in capture uses temporary synthetic settings/scripts and window rendering,
not desktop recording, user credentials, microphone capture or provider calls.

The setup screen accepts credentials in password fields and saves API keys encrypted
with Windows DPAPI for the current Windows account. On first launch it can
also prefill from `AZURE_SPEECH_KEY`, `AZURE_SPEECH_ENDPOINT`, `AZURE_SPEECH_REGION`, `AZURE_OPENAI_ENDPOINT`,
`AZURE_OPENAI_DEPLOYMENT`, and `AZURE_OPENAI_API_KEY`. Do not place secrets in source
files or paste them into shared shells. `AZURE_TENANT_ID` can prefill the optional
resource tenant ID. Once saved, local configuration takes precedence over environment
defaults, including keys deliberately cleared in the UI. Bearer tokens are not saved
by the application.

## Saved configuration

Configuration is stored in:

```text
%LOCALAPPDATA%\MeetingCopilot\settings.json
```

Connection-field edits are saved automatically after a short delay, on **Start live
overlay**, and before closing setup. **Save configuration** saves immediately without
starting audio or requiring cloud connectivity. The app saves provider selections,
endpoints, deployment, resource tenant ID, recognition locale, selected device IDs,
microphone and capture-exclusion preferences, and encrypted Speech and OpenAI keys
(both Responses and legacy modes). OpenAI-compatible, Gemini, Claude and Grok each have
a separate profile containing their base URL, model ID, encrypted key and request limits.
Switching provider never copies an Azure key or another provider's key. Changing an
external profile's URL clears its key; enter the key intended for the new destination.
Existing version-1 Azure settings and their DPAPI encryption remain supported.

Recordings, transcripts, meeting briefs, vocabulary hints, and meeting consent are
**not** persisted. Consent is not restored on restart and must be confirmed again.
A missing saved audio device requires an explicit replacement selection rather
than silently switching capture to another device.

The JSON keeps non-secret settings readable; key fields contain DPAPI ciphertext.
DPAPI protects keys at rest for the Windows account, not against code running as that
same user. Do not share or commit this file, and do not expect encrypted keys to work
when copied to another Windows account or machine.

Writes replace the prior configuration only after the new encrypted file is written.
Read/decryption/save failures are shown in setup. An unreadable configuration is not
automatically overwritten; use **Forget saved settings** to reset it. That button removes
the config file and clears key fields. Further configuration edits or starting live
mode will save again. An environment variable can still supply a key on a later fresh
launch; remove it separately if you want to forget that source too.

1. Select the same **playback device as Teams**. This captures all sounds on that
   device, not a Teams-only stream. Close unrelated audio.
2. Optionally enable your microphone. Use headphones to prevent playback being picked
   up again by the microphone. Only near-identical textual echoes are suppressed;
   this is not acoustic echo cancellation or named-participant diarization.
3. Choose MAI-Transcribe-2 or Azure streaming and optionally add technical vocabulary
   hints. MAI uses `locales: ["en"]`; accent/locale selection applies only to streaming.
4. Optionally supply a meeting goal or design excerpt, then confirm you have the required consent
   and workplace permission. A local confirmation is not a substitute for participant consent.
5. Start the live overlay. It opens in **Focus view** with the current question, history
   controls and live analysis status. **Show context** reveals **Speech**, **AI context**
   and **Display** tabs without stopping listening. Nothing is sent to Teams chat or spoken aloud.

**Try a sample** exercises the UI without audio, cloud calls, or
credentials. Its suggestions are explicitly prewritten, not AI outputs.

## Desktop interface

### Choose a meeting AI provider

In **Connections > Meeting AI**, keep the current Foundry connection or select:

| Connection | Default API base URL | API and authentication |
|---|---|---|
| OpenAI-compatible | `https://api.openai.com/v1` | Chat Completions, Bearer API key |
| Google Gemini | `https://generativelanguage.googleapis.com/v1beta` | Native `generateContent`, `x-goog-api-key`, JSON-schema output |
| Anthropic Claude | `https://api.anthropic.com/v1` | Native Messages, `x-api-key`, API version `2023-06-01`, forced structured output tool |
| xAI Grok | `https://api.x.ai/v1` | Responses, Bearer API key, JSON-schema output, `store: false` |

Enter an **exact model ID available to your account**, not a model display name. Gemini
expects the bare model ID without `models/`. The Claude tool is an output schema only:
the application executes no tool or external action. Native adapters require HTTPS.
Endpoint/model compatibility is checked explicitly; there is no automatic protocol,
model or provider fallback. Provider error bodies are not displayed or logged.

For local Ollama use the OpenAI-compatible option with `http://localhost:11434/v1`;
LM Studio commonly uses `http://localhost:1234/v1`. Start the server and load a model
separately. Select **No key - local loopback only** where appropriate. HTTP and no-key
mode are restricted to `localhost` or loopback IP addresses; remote endpoints require
HTTPS and an API key. Redirects are not followed, so keys are not redirected elsewhere.

Under **Model compatibility and limits**, generic servers offer JSON object, strict
JSON schema, or prompt-only JSON, and `max_completion_tokens` or `max_tokens`.
Many local servers require `max_tokens`; some require prompt-only output. Every result
is still checked locally for the meeting schema, evidence IDs and length limits.
Not every model supports these protocols or follows structured instructions reliably.
This is extensible provider support, not a guarantee that every model will work.

All four new profiles offer 2,200/4,096/8,192 output tokens, 45/60/90-second deadlines,
and 16,000/24,000/48,000 speech/slide-text characters per request. Instructions,
brief and AI summaries are additional input; this setting is not the model's total
token-context window. The default deadline is 45 seconds. Slow/reasoning models may
need more output tokens or time but can be less useful for live suggestions.
Full meeting history remains retained in memory until Stop at every budget.

**Test model connection** sends only a synthetic payment discussion and checks a
nonempty memory plus the normal grounded-result schema. It opens no audio devices,
does not send the meeting brief or scripts, and may be billable. Edits, provider
changes, Cancel, and closing setup cancel the test and invalidate stale success.
The new providers also perform synthetic preflight before live capture or resume.
Tests in this repository use fake HTTP responses; live Gemini, Claude, Grok and
arbitrary compatible-model access/quality have not been verified with real credentials.

**Capability boundary:** the new adapters support text context, live recommendations,
questions, memory, history and clipboard actions. Slide-image analysis is currently
enabled only for the existing Foundry Responses adapter with an image-capable model.
Other connections visibly disable that action rather than uploading an unsupported image.
Azure Speech remains the separate transcription provider even with local model inference.
Saved scripts and the offline teleprompter do not require any model provider.

Setup separates **Meeting** (audio and optional brief), **Connections** (Speech/model
configuration, Save configuration and Forget saved settings) and **Privacy** (data
handling and capture exclusion). Consent and Start stay visible while the form scrolls.
Validation switches to the relevant tab rather than leaving a connection error hidden.

The native WPF controls share a slate palette, restrained blue primary action, consistent
type and spacing, visible keyboard focus, and selected/disabled/hover states. Standard
tab navigation and F4/Escape dropdown behavior remain available. Windows high-contrast
colors are applied to surfaces, controls and selections, including changes while running.
No animations or decorative effects compete with the meeting.

Window title bars share the slate palette on Windows 11, retaining native dragging,
resize, Snap, system menus and minimize/maximize/close controls. Windows 10 uses the
supported dark-frame setting rather than custom caption colors. High contrast returns
caption colors to Windows and updates the app's surfaces and action icons. The minimal
prompter remains borderless and translucent; it does not gain a title bar.

The Meeting Copilot conversation/M icon is included in the executable, window captions,
taskbar and setup header. Key actions use a shared set of line icons alongside their
text labels. The editable source is `src\MeetingCopilot.Desktop\Assets\MeetingCopilot.svg`;
regenerate its 16-256px Windows ICO and PNG with
`powershell.exe -NoProfile -STA -File .\tools\Build-AppIcon.ps1`. No network or design
service is needed. Generated assets are included, so ordinary builds need no icon tooling.

The overlay starts focused. **Show context** expands it; **Focus view** hides supporting
details again. **Why this suggestion?** (or **Why this recommendation?**) explains the contribution in the expanded
view. History remains available in either view. **Listening** means a session is active;
the Speech tab provides detailed per-source audio status, not a guarantee of transcription.

## Voice-follow and offline teleprompter

Choose **Open teleprompter** above the setup tabs, or in the live overlay under
**Show context > Display**. Opening the library or reader does not start speech capture,
model preflight, Azure calls or a meeting session. Offline mode needs no keys. If a
meeting session is already running, it continues independently; opening the reader
does not pause existing audio capture.

Only one app screen is shown at a time: opening the library hides setup or the meeting
overlay, and opening a reader hides the library. Closing a reader returns to the library;
the library's **Back** button or window close returns to the screen you came from.
Reopening the teleprompter focuses the current screen rather than creating another library.

In the script library, add a title and paste presentation text. Use blank lines between
paragraphs for paragraph navigation. **Save script** persists the draft; **Save and open
reader** saves first and opens the always-on-top reader near the camera, initially paused.
Use **New script**, the saved-script list, and **Delete script** to manage presentations.
Leaving an unsaved draft asks whether to save, discard or cancel. Reload reads changes
made by another app instance. Deletion requires confirmation.

**Follow my voice** is the default for scripts without a saved mode. In setup, select
your microphone and save the Azure Speech connection; the meeting's microphone checkbox
does not need to be enabled. In the reader, verify the microphone name, grant the
separate Azure Speech permission, then select **Start following**. Opening a reader
never grants permission automatically; permission is not saved.

Interim recognition is matched against nearby script words locally. The current word
is highlighted at 110% of the script font size without reflowing the surrounding text.
The reading line advances only when recognized words match; silence or off-script
speech holds position. There is **no speed timer in voice mode**, no LLM call, and no
script upload or vocabulary-hint upload. Only the selected microphone's audio is sent
to Azure Speech (billable). An existing meeting session may bill a separate audio stream.
Recognition has latency, can revise partial words, and is not speaker identification:
other speech entering the microphone can match. Technical names, numbers and repeated
phrases may need correction. Click a word to select a new starting point, then resume.
Already accepted progress does not rewind on a revised hypothesis.

**Timed / offline** moves text at a fixed, adjustable speed (5-120 logical pixels per
second) without microphone or network use. It is never a silent fallback after a
voice error. A reading guide stays near the top, with upcoming text below.
Pause preserves position; Restart returns to the beginning and stays paused. At the
end the reader stops without looping. Voice capture stops on pause, permission
revocation, mode change, minimization and close. Resizing, changing text layout or
manually scrolling pauses either mode. A rendering stall longer than one second
pauses timed playback rather than skipping unread content. Switching to another
application does not pause the reader.

**Reading settings** adjusts text size (20-60), line spacing (1.2-2.0), and reading width
(320-960 logical pixels, constrained by the window). Layout adjustments preserve the
approximate place within the current paragraph. Play closes settings to leave room for
the script. Timed speed changes apply without restarting. On reader close, the mode and settings are
saved with the script; save failures leave a visible, retryable draft in the library.
The reading position is not persisted. Script editing is disabled while its reader is open.

| Shortcut (reader focused) | Action |
|---|---|
| Ctrl+Space | Play / pause |
| Space in the reading area | Play / pause (focused buttons retain their usual Space action) |
| Escape | Pause |
| Ctrl+Up / Ctrl+Down | Faster / slower in timed mode only |
| Page Up / Page Down | Previous / next paragraph, paused |
| Up / Down | Move one line, paused |
| Home / End | Beginning / end, paused |
| Ctrl+Home | Restart, paused |

These are window-local shortcuts, not system-wide hotkeys. Once you switch to Teams or
slides, click the reader before using them. Mouse-wheel scrolling pauses playback.

Scripts are deliberately persistent, unlike meeting context. The separate library is
`%LOCALAPPDATA%\MeetingCopilot\scripts.json`: titles, text and reading settings are encrypted
with Windows DPAPI for the current account. It supports up to 100 scripts, 120 characters
per title and 100,000 characters per script, within a 16 MB encrypted-file limit.
Writes are atomic; concurrent changes to the same script are rejected, not silently
overwritten. Different-script saves merge. Corrupt or undecryptable libraries are not
overwritten. Keep a copy of a conflicting draft before reloading. **Forget saved settings**
does not delete scripts; **Delete script** removes the selected presentation.
DPAPI does not protect against other programs running as your Windows user and is not
a portable backup or secure-erasure guarantee.

### Minimal overlay for slides

Choose a script in the library and select **Open minimal overlay**. It saves your
draft first, then switches to a borderless, translucent, always-on-top reader over
PowerPoint or other applications. This is an **additional option**: the full voice-follow
and timed reader is unchanged. The minimal overlay always stays offline, even for a
script whose full reader uses voice following. It opens no microphone, uses no Azure
connection or LLM, and needs no credentials.

The main controls are **Run**, which scrolls at the script's saved timed speed, and **Stop**, which
preserves the current position. It starts stopped. Mouse-wheel or scrollbar navigation
stops automatic scrolling so you can choose your own position; Run resumes from there.
At the end scrolling stops, and Run restarts from the beginning. A script with nothing
to scroll has Run disabled. Change speed, font size or spacing in the full reader and
close it to save those settings before reopening the minimal overlay.

Drag the top grip to move the overlay, or the bottom-right corner to resize. Dragging,
resizing, minimizing and long rendering stalls stop scrolling. Switching to your slides
does not. While the overlay has focus: **Escape** stops, **Space** toggles Run/Stop,
**Up/Down** and **Page Up/Page Down** scroll manually, **Home/End** move to the beginning/end,
and **Alt+F4** closes it. The small **X** at the top right also closes the prompter.
Focused buttons retain their normal Space behavior.

The library is hidden while the overlay is visible. The small **back arrow** at the top
left hides the overlay and returns to the library without closing it or losing position.
**Show overlay** (or **Return to minimal overlay**) restores the same overlay and hides
the library again. Hiding stops scrolling; showing it again stays stopped until you select
Run. This does not change transparency or disable capture exclusion. **X** closes the
overlay and returns to the library. Closing the library closes even a hidden overlay
and returns to setup or the meeting screen.
Editing and the full reader are disabled while the minimal overlay is open. Its position
and window geometry are not saved, and it does not change the saved voice/timed mode.

Capture exclusion is **always required** for this overlay, independently of the app's
capture preference. Script text remains hidden until Windows confirms exclusion for
the window, including when restored through Show overlay; if that fails, it closes and
the library shows an error. This is not a
universal invisibility guarantee: verify the remote view in your sharing app before
presenting. Prefer sharing the PowerPoint window rather than your whole desktop.
High-contrast mode uses an opaque system-color surface for readability.

**Will Teams attendees see it?** The overlay uses Windows `WDA_EXCLUDEFROMCAPTURE`,
which requests that supported capture APIs omit the window. A local synthetic screen
capture verified exclusion, but that does not verify Teams' remote attendee view.
Microsoft documents [capture exclusion as a limited OS feature, not a security guarantee](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity).
For slides, prefer sharing only the PowerPoint window rather than the whole desktop.
[PowerPoint Live](https://support.microsoft.com/en-us/teams/meetings/share-slides-in-microsoft-teams-meetings-with-powerpoint-live)
presents the deck instead of your desktop; it can upload the selected file, so check
your organization's file-sharing policy. Before relying on desktop sharing, use a
dummy script in a private test meeting and check from a second device or with another
participant. Your own screen or sharing outline is not proof of the remote result.

The library and reader each request capture exclusion according to the app preference.
The reader can turn its own exclusion on/off under Reading settings for that window only.
Verify your sharing application's remote view; not every capture method respects it.
The setup window remains unprotected. Scripts are not automatically inserted into the
meeting brief, transcript, question history or AI context.

## Speech and AI context

The **Speech** tab shows provisional words separately from the finalized speech
retained for analysis, including time and source labels. Provisional results are
replaced as recognition changes and cleared when that source finalizes; they are
never sent to the question model. Both playback and microphone provisional text can
be shown at once.

The **AI context** tab shows your supplied meeting brief, captured slide summaries and the latest
model-generated memory alongside the retained speech count. This is the context
available for analysis, not a claim that every new utterance has already been processed.
The memory may be incomplete or wrong. All accepted finalized speech, slide summaries
and distinct successful AI summaries remain in session memory until Stop or app exit.
Near-identical cross-source echoes are suppressed. There is no 80-item retention cap
or one-hour cutoff; memory use grows with the session. AI requests, not stored session
history, are bounded to keep live analysis practical. Earlier-context selection is
keyword-based and can miss paraphrases; preservation does not guarantee perfect model recall.

The read-only fields support scrolling, text selection and copying. Compact mode
hides them; expanding restores current content. Pause clears provisional words but
keeps finalized speech and context in memory until Stop. The larger default window
is constrained to the work area and remains resizable. These panes do not add any
transcript or context persistence and can be visible during desktop screen sharing.

## Capture a slide on demand

In a live session, choose **Capture slide**, drag around only the slide, and review the
local image before selecting **Send slide to AI**. Escape cancels region selection;
**Cancel** discards the preview without sending it. No continuous screen monitoring,
automatic slide changes, clipboard access, full-desktop screenshot buffer, or image
file storage is used. The overlay temporarily hides for selection and then returns.
The preview requests the same capture-exclusion setting as the overlay.

The capture reads only the selected rectangle and scales its longest side to at most
1600 pixels (PNG, maximum 6 MB). Check the preview carefully: a screen region can also
include another window, notification or private chat. Protected/occluded content or
unusual multi-monitor scaling can produce an incomplete image; cancel or select again.
This is a snapshot, not an attached deck or a guarantee that the slide stays current.

Slide reading requires a **Responses** connection and an image-capable deployment.
The currently configured deployment has passed a synthetic image-reading check. Legacy
Chat Completions mode leaves capture disabled with an explanation. Each approved capture
uses one additional image-analysis request, followed by normal text reasoning; these can
be billed. The image is not repeatedly attached to speech analysis.

The model separates visible content from diagram interpretation and uncertainty.
Timestamped, AI-extracted summaries appear under **Captured slide context**, not under
recognized speech. They enter the same bounded evidence context as speech and can be
cited by generated questions. Older slide summaries remain in session memory but are
selectively included in AI requests. Extraction
can be wrong; the original pixels are released after the request rather than retained
for later comparison. Speech analysis runs independently while one slide is being read.
**Cancel slide analysis**, Pause or Stop cancels the request and rejects late results;
a request already sent may still be billed.

## Browse and copy suggestions from this meeting

**Browse suggestions** opens an inline, scrollable list with timestamps and lifecycle
labels. **Previous** and **Next** step through it; **Live** returns to current advice.
The list lives inside the capture-protected overlay, not in a separate popup.
All generated recommendation/question/gap/alternative entries remain available for this session,
including suggestions that were deferred before display. Freshness updates do not
duplicate the same active suggestion. Each entry retains its original evidence excerpts.

While browsing, new suggestions do not move your selection. **Live (N new)** indicates
arrivals since browsing began. Historical entries are explicitly marked as not live
advice; answered, expired, superseded, dismissed and covered entries are retained.
The app does not re-evaluate every archived entry on every new utterance, so an old
entry is not a claim that a question remains unanswered. Dismissing or marking an
older entry covered does not clear a different live suggestion.

History navigation is available in both overlay sizes and while paused. History is
held only in memory until **Stop**; it is neither saved nor restored on restart. Unlike
the bounded context sent to the model, question history grows with the meeting.

**Copy** copies only the displayed contribution text to the Windows clipboard, in both
Focus and expanded views. It also works on a selected history entry, including while
listening is paused. Pending/new suggestions cannot silently replace the wording while
the Copy button has pointer or keyboard focus. Empty placeholders cannot be copied.
Success and clipboard-unavailable errors appear in the overlay; failed copies can be
retried. It does not copy the whole transcript, supporting reason or evidence.

Copying is explicit and is an exception to in-memory-only history: Windows clipboard
history, cloud clipboard sync or other applications may retain the copied text after
Stop. Capture exclusion does **not** protect clipboard contents. The app does not clear
your system clipboard when it stops, and never posts the copied text into Teams.

## Live behavior

- Azure Speech streaming is recommended for live meetings. It maintains a continuous
  connection and publishes provisional text while speech is arriving. Only finalized
  speech utterances and approved AI-extracted slide summaries feed the question model. Existing saved provider selections are respected;
  switching to streaming is explicit, not an automatic fallback during capture.
  For the configured resource, saved-key synthetic testing verified partial text after
  1.29 seconds, a 30-second continuous session, and a grounded model question from the
  recognized speech in 6.57 seconds. These are sample measurements, not latency guarantees.
- MAI uses independent short WAV uploads for each enabled audio source (billable).
  Audio is held only in memory. It seeks a 700 ms quiet boundary after at least two
  seconds, with a hard six-second segment cap. Segments containing only exact digital
  zero are not uploaded. All nonzero segments, including quiet speech and background
  noise, are preserved; the boundary heuristic is not a guarantee of speech detection.
  If loopback stops delivering packets during silence, trailing audio is flushed after
  800 ms. Pausing deliberately discards buffered/in-flight audio instead of sending it.
- Each source permits two concurrent MAI requests and at most two queued segments
  (up to four concurrent requests when both playback and microphone are enabled).
  Completed text is published in capture order, even when requests finish out of order.
  A later completed result holds its slot until earlier text arrives, bounding memory.
  The overlay shows queue wait and last-transcript latency measured from segment completion,
  not from the beginning of speech. Concurrent requests can increase rate-limit pressure.
  Full queues, requests over 15 seconds, and transcripts delayed over 15 seconds
  stop the session with a visible error rather than silently losing meeting context.
  If this continues, use the explicitly selectable Azure streaming provider; the app
  does not silently switch models or claim MAI can always sustain live audio.
- This is **near-real-time chunked transcription**, not streaming partial words.
  Expect segment duration plus network/model latency before text reaches the reasoning
  model. Hard boundaries may split words or sentences. Accuracy and latency must be
  measured with representative consented audio; the app does not guarantee either.
- There is no automatic model fallback.
  It uses two independent streams when microphone capture is enabled (billable).
  Continuous recognition uses a 650 ms silence segmentation target and bounded long
  speech segments (10-second maximum requested). When WASAPI loopback stops producing
  packets, the app flushes the trailing conversion buffer after 200 ms and feeds silence
  on a 100 ms timer. This lets the recognizer finalize during pauses without waiting for
  the next person to speak. Microphone packets already containing silence are retained.
  Actual transcript latency varies with speech, network and service.
- Before capture starts or resumes, Foundry Responses and the new external modes send a small synthetic discussion
  through the configured deployment to verify authentication, resource access and JSON
  compatibility. No meeting brief or audio is sent during this check; it is billable.
  Failed access checks leave audio devices unopened. Foundry startup has a 30-second
  budget; external modes use their configured request deadline. Authorization failures during a meeting interrupt
  the session instead of being retried indefinitely.
- Model checks every two seconds when new finalized speech exists; one request at a
  time, a default 45-second request timeout, and visible errors with an eight-second retry delay.
  External profiles can select 60 or 90 seconds instead. The shared model HTTP client
  relies on explicit operation cancellation so it does not cut off those deadlines;
  Foundry preflight and slide reading keep their independent 30-second and 12-second deadlines.
  Timing updates every second independently of suggestion display: latest final speech,
  last successful analysis and its request duration, current elapsed time, retry countdown
  and last analysis error. Historical suggestions do not rewind these live indicators.
- All accepted speech/slide context and distinct successful summaries are retained
  in memory until Stop, not discarded when 80 items accumulate. They are not saved to
  disk or uploaded after the meeting. Pause preserves them; Stop/app exit releases them.
  Analysis runs continuously, not only at the end of a meeting.
- Each model request has at most 80 speech/slide items and, by default, 24,000 text characters
  (16,000 or 48,000 can be selected for external profiles).
  It reserves space for the latest speech (up to eight items / 8,000 characters), even
  while catching up. Remaining space processes an ordered batch of older unprocessed
  speech, followed by recent overlap, active-cue evidence, relevant earlier remarks with
  neighbors, and recent slide summaries where space permits. A backlog is consumed over
  successive requests without discarding items. Suggestions should concern the newest
  speech, not the historical catch-up topic. **Suggest now** also fills unused space with
  more recent history; it does not send an unbounded hour-long prompt.
- The current summary targets under 3,000 characters with a 6,000-character per-summary
  limit. All distinct successful summary versions are retained; up to two relevant
  earlier versions can accompany the latest one. This is not a lifetime memory cap.
  Earlier-context retrieval is local keyword matching, not guaranteed semantic recall.
  The model is instructed to prioritize later corrections over older notes.
  Failed analyses never advance summary coverage or overwrite the last good memory.
- Model suggestions must cite IDs from supplied finalized utterances. This validates
  evidence references, not the truth or quality of the model's reasoning.
- Responses requests use a strict JSON schema for the result, including field types,
  suggestion length limits and required outcome explanations. Local validation still
  rejects unknown evidence IDs and contradictory cue status. The legacy Chat Completions
  option retains JSON-object mode with the same instructions and local validation.
- A brief is optional. Persistent actionable ambiguity can produce a grounded clarification
  about a goal, metric, target, owner, scope or dependency, instead of only a catalogue of
  fragments. Greetings, isolated unfinished sentences and noise can still produce no cue.
- On every analysis the model considers concrete recommendations, not just clarifications.
  When the stated goal, risk and constraints support an action, it prefers a speakable
  proposal explaining what to do and the benefit. **Why this recommendation?** shows
  the mechanism/trade-off, assumptions and evidence. General engineering knowledge can
  inform the proposal, but supporting IDs must cite the actual meeting problem or
  constraint; the proposed solution need not have already been said.
- Missing essential facts still call for focused clarification, not an invented fix.
  Known constraints, accepted solutions and dismissed topics remain respected. A
  competing approach without a grounded reason to prefer it is an alternative, not a
  claim of an objectively best design. No contribution is forced just to fill space.
  New evidence may replace an earlier question with a materially stronger recommendation;
  an adopted/implemented recommendation is treated as addressed.
- Presentations and demos also prompt useful participation about practical behavior,
  trade-offs, boundaries, recovery or implications. Unknown behavior is not evidence
  that a feature is absent or broken.
- **Suggest now** explicitly requests the strongest grounded contribution from the retained
  context, including when no new speech has arrived. The request is queued if analysis is
  already in progress, and does not force advice from noise or repeat resolved points.
  The existing JSON field `questionRequested` is retained for compatibility but no longer
  restricts the result to a question. Both Responses and legacy Chat Completions share
  the policy and accept the `recommendation` kind.
- The analysis indicator distinguishes waiting for finalized speech, queued speech, an
  in-flight request, a completed analysis and a failed request. Completed checks show the
  context revision and elapsed time. When no suggestion is selected, the model supplies a
  short outcome explanation (`noSuggestionReason`, at most 300 characters), not hidden
  reasoning. Older responses without that optional field explicitly show "No explanation
  returned." These diagnostics remain in memory, not a transcript log.
- If new finalized speech arrives during analysis, a grounded result can appear explicitly
  labeled **Provisional** while newer speech is checked next. New speech also marks an
  existing cue provisional. The next result can confirm, replace or retract it; references
  to evidence no longer retained are rejected. This avoids starving suggestions during
  continuous discussion, but provisional advice may already have been answered: review it.
- New cue wording waits only while the pointer or keyboard focus is on the question,
  its explanation, Copy or dismissal controls. Reading speech/context does not block delivery.
  Same-cue status changes, errors and retractions are immediate. Between model requests,
  the engine expires cues at 45 seconds, or after 15 seconds continuously provisional.
  An in-flight request can delay that expiry check; these are not hard UI deadlines.
- The current question and its controls stay above the separately scrolling context.
  Compact mode hides details, not the question. A first question is never held just because
  the pointer is on the empty placeholder. When replacement wording is deferred, **Show
  new suggestion** displays it immediately without having to move the pointer away.
- Dismiss / Already covered suppress repeat wording and pass feedback to the model.
- Pause stops capture and analysis, cancels in-flight requests, and retains context in
  memory. Resume reconnects. Stop disposes the session and drops transient context.
- Device changes/disconnection stop the session with an explicit error; restart from
  setup to choose another device. No silent device switching or hidden recording.

## Privacy and limitations

The app does not write recordings, slide images, question history, transcripts, model responses or plaintext credentials
to disk. API keys are persisted only as Windows DPAPI ciphertext in local configuration.
Audio goes to Azure Speech; user-approved slide images, finalized text, the brief and bounded context go to the
selected AI endpoint (Foundry, another provider or a local server), subject to its image capability. Provider retention, region, billing, access control
and organizational policy must be reviewed separately. This is not an offline app.
Foundry and Grok Responses requests set `store: false` and do not use server-side conversation IDs.
This disables response storage for retrieval, not all provider retention or monitoring.
Azure credential tools may maintain their own sign-in caches outside this application.

The overlay requests Windows **capture exclusion** by default, including when loading
older settings. Change **Hide overlay from screen capture** in setup or the overlay's
**Display** tab; the preference is saved for the next launch. This applies to the
overlay in expanded and compact modes and to the slide preview, not to the setup window.

The app sets `WDA_EXCLUDEFROMCAPTURE` and reads the native window affinity back before
showing **Capture exclusion on**. Failures are displayed explicitly; they never imply
the window is protected. Windows 10 version 2004 or newer is required. Native acceptance
and toggle behavior are verified on this machine, but **Teams remote sharing has not
been verified**. Check another participant's view before relying on it. Only compatible
capture mechanisms omit the window; cameras, hardware and other methods can still see
it. Share a specific application window when unsure. There is no automatic screen-share
detection, and hiding the overlay does not stop listening.

Speech from recordings or meeting participants is untrusted model input. The model has
no executable tools, code execution or outbound actions; Claude's named output tool
only carries structured data. Suggestions are advisory; review them before speaking.
The Azure-specific connections support public Azure resource domains, not sovereign-cloud
domains. External profiles support user-configured HTTPS provider/gateway URLs; generic
compatible connections additionally permit HTTP loopback servers. Review each destination
before entering credentials or sending workplace information.

Light-theme switching, remembered per-display placement, and guaranteed cross-stream
speaker identification are not implemented in this first version. Windows high-contrast
colors and adjustable suggestion text are supported.

## Recommended live listening: Azure Speech streaming

Select **Azure Speech (live streaming - recommended)**. Enter your Speech endpoint,
such as `https://your-resource.cognitiveservices.azure.com/`, and Speech key. Leave **Region
override** blank to connect directly through that resource. No resource provisioning,
model deployment, new key or interactive sign-in is needed when the existing resource
supports streaming. A nonblank region takes precedence over the endpoint for users
with an existing regional Speech connection.

The app uses the official Speech SDK with `SpeechConfig.FromEndpoint` and a `wss://`
version of the validated HTTPS resource root. The SDK supplies the Speech protocol
path. The endpoint field is visible for both streaming and MAI. Public Azure resource
domains are validated before a key is passed to the SDK.

Opt-in synthetic-audio checks can validate an approved resource without opening microphone
or playback devices. Real meeting accuracy, accents, echo and long-session reliability
still need evaluation.

Reference: Microsoft's [endpoint-based Speech SDK configuration](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/speech-services-private-link#usage-with-the-speech-sdk)
also applies to custom-domain resources without private endpoints.

## MAI-Transcribe-2 configuration

Enter your resource endpoint, for example
`https://your-resource.cognitiveservices.azure.com/`, or supply it through the environment.
Never share keys in chat, source files or screenshots. Keys entered into the app are
saved only as ciphertext in its local configuration.

The request matches the supplied REST sample:

- `POST /speechtotext/transcriptions:transcribe?api-version=2025-10-15`
- `Ocp-Apim-Subscription-Key` header; redirects disabled.
- Multipart `audio` (PCM16 mono WAV) and `definition`.
- `locales: ["en"]`, `diarization.enabled: true`, and the configured phrase list.
- `enhancedMode.enabled: true`, `model: "mai-transcribe-2"`,
  `modelOptions: { "timestamps": "word", "transcribeStyle": "verbatim" }`.

Final text is read once from `combinedPhrases`; `phrases`/word metadata are not appended
again. Although diarization and timestamps are requested, the UI deliberately does not
assign people stable identities from per-upload speaker IDs. Evidence timestamps show
when final text was received, not exact word timestamps.

MAI-Transcribe-2 is documented as public preview without a production SLA and does
**not** translate languages. This implementation follows the agreed English-to-English
flow. Meeting analysis uses the independently selected AI provider; no hosted Foundry
agent is required.

References: [MAI-Transcribe](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/mai-transcribe)
and [fast transcription](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/fast-transcription-create).

## Foundry Responses: API key or Microsoft Entra

Configure your own resource:

- Endpoint: `https://your-resource.services.ai.azure.com/openai/v1`
- Deployment: your exact deployed model name
- API: Responses (`POST /openai/v1/responses`)
- Default for new setup: **Responses API - API key (no sign-in)** using `ApiKeyCredential`.
- Alternative: **Responses API - Microsoft Entra (no API key)** using `DefaultAzureCredential`
  with `BearerTokenPolicy` and token scope `https://ai.azure.com/.default`.

For the API-key path, select **Responses API - API key (no sign-in)**, enter your endpoint
and deployment, and enter the key from that Foundry resource in **Azure OpenAI
API key**. It is saved encrypted automatically. The tenant field is hidden and ignored;
no Azure sign-in is attempted. An existing saved Entra selection is not silently changed.
Do not select the legacy Chat Completions option for the `/openai/v1` URL.
Rotate any key exposed outside its intended storage. A key from an unrelated Speech
resource is not interchangeable with the model resource's key.

The implementation uses the official OpenAI .NET SDK and Azure.Identity. In the
installed SDK version, `ResponsesClientOptions` configures the endpoint and
`CreateResponseOptions.Model` selects the deployment; this is the current equivalent
of the supplied C# sample's model-in-constructor overload. It uses async requests,
the existing meeting instructions and validated JSON output, not the sample's weather
question. No tools, current-location access, or named Foundry agent are configured.

For Entra mode, sign in with an authorized Azure CLI or Visual Studio identity. When needed, enter the
resource's tenant GUID in setup and sign in to that same tenant:

```powershell
az login --tenant <resource-tenant-id>
```

The app does not launch interactive sign-in or change your global Azure account.
The credential chain caches/refreshes tokens through the SDK. Successful token acquisition
alone does not prove resource authorization, so both Responses modes now verify a
synthetic inference before audio devices are opened.

Correct tenant sign-in and resource permissions are required for Entra inference.
API-key mode avoids that tenant-token dependency, but requires a valid resource key
with local authentication enabled. Synthetic checks do not establish real-meeting
quality or latency; evaluate with approved resources and consented audio.

**Fixed HTTP 400:** Azure's JSON-output validation requires the word `JSON` in
`input`, not only in the separate `instructions` field. Every Responses request now
includes that explicit input instruction; both authentication modes have regression coverage.

Responses use `store: false`, a 2,200-output-token budget, strict JSON-schema format, and no
SDK retries; the live engine handles timeouts and retry timing. Incomplete, refused,
empty or ungrounded results are rejected. Authentication/service errors are sanitized
so tokens, identity details, and service-echoed meeting text are not shown.

To use the previous Chat Completions implementation, select **Chat Completions - API key**,
enter a resource root URL without `/openai/v1`, and supply the deployment and key.
Switching modes does not silently replace your endpoint.

## Build and verify

```powershell
dotnet build .\MeetingCopilot.slnx
dotnet test .\tests\MeetingCopilot.Tests --verbosity minimal
dotnet publish .\src\MeetingCopilot.Desktop -c Release -r win-arm64 --self-contained false -o .\artifacts\multi-provider-win-arm64
# For an Intel/AMD machine, use -r win-x64 instead.
```

Tests cover streaming resampling, grounded model-response validation, bounded context,
echo suppression, cancellation, stale results, dismissal races and HTTP payloads.
Session integration tests also exercise finalized-speech delivery, partial-startup rollback,
stopping both inputs before cloud cleanup, startup cancellation and repeat disposal. They
also validate MAI multipart definitions, WAV headers, response validation, endpoint
restrictions, lossless PCM segmentation, digital-zero suppression, bounded concurrent
uploads, ordered results, sustained synthetic throughput and canceled uploads. They
also exercise the official Responses SDK through a fake HTTP transport, the exact
Entra scope and payload, refusal/incomplete responses, authentication before capture,
tenant mismatch guidance, resource preflight before capture, late authorization failure,
cancellation, and API-key authentication without Entra token acquisition. They
verify DPAPI round trips, absence of plaintext keys, corrupt/tampered configuration
handling, atomic-write failure, UI reload, save-on-close and forgetting settings using
isolated synthetic test configurations. Teleprompter checks cover encrypted scripts,
timed playback, cumulative speech matching, repeated phrases, off-script holds, word
highlighting, mode changes, unsaved microphone permission, late recognition events,
canceled startup and deferred microphone cleanup before window close. They
do not prove real meeting accuracy. Live end-to-end verification requires approved
credentials and a consented meeting; sample preview is not a substitute.

An opt-in live model check sends only synthetic meeting text and may incur inference
charges. It is skipped by default. It uses `AZURE_OPENAI_API_KEY` if already supplied
securely in the process environment; otherwise it uses Entra. It does not read your
saved application keys. Endpoint/deployment environment overrides are also supported:

```powershell
$env:MEETINGCOPILOT_LIVE_FOUNDRY_TEST = '1'
dotnet test .\tests\MeetingCopilot.Tests --filter FullyQualifiedName~LiveFoundryTests
Remove-Item Env:\MEETINGCOPILOT_LIVE_FOUNDRY_TEST
```

To explicitly validate the application's saved configuration instead, including its
DPAPI-protected key, use the separate opt-in below. It decrypts keys only in memory,
does not change settings or open audio devices, and makes billable synthetic requests.
It covers preflight, question/retraction, clarification without a brief, automatic
presentation questions through the running session, and explained silence for chatter.
Diagnostics redact saved keys and report service errors and timings. Model analyses
use a stricter 12-second performance-probe deadline (live analysis permits 45 seconds); the presentation check also requires
automatic delivery within 15 seconds of injecting finalized text. This does not
measure actual microphone/playback recognition latency or real-meeting quality.

```powershell
$env:MEETINGCOPILOT_LIVE_SAVED_SETTINGS_TEST = '1'
dotnet test .\tests\MeetingCopilot.Tests --filter FullyQualifiedName~LiveFoundryTests.SavedConfiguration --logger "console;verbosity=detailed"
Remove-Item Env:\MEETINGCOPILOT_LIVE_SAVED_SETTINGS_TEST
```

The recommendation check sends invented payment-design context through the saved
model connection. It verifies both automatic and explicitly requested recommendations,
grounded evidence, replacement of an answered question and retirement after adoption.
It opens no audio devices and sends no saved scripts or real meeting text. These
billable synthetic checks do not establish recommendation quality in real meetings.

```powershell
$env:MEETINGCOPILOT_LIVE_RECOMMENDATION_TEST = '1'
dotnet test .\tests\MeetingCopilot.Tests --filter FullyQualifiedName~SavedConfigurationProposesAnActionInsteadOfOnlyClarifying --logger "console;verbosity=detailed"
Remove-Item Env:\MEETINGCOPILOT_LIVE_RECOMMENDATION_TEST
```

The live streaming check uses the saved Speech and model keys, generates speech in
memory with Windows SAPI, and sends PCM at real-time speed through the same streaming
configuration and conversion/idle-silence code used by the app. It checks provisional
text before audio ends, finalized speech, a connection lasting beyond 15 seconds, and
a grounded question from the recognized audio. It makes billable Speech/model requests
but never opens microphone/playback devices or writes audio files.

```powershell
$env:MEETINGCOPILOT_LIVE_STREAMING_TEST = '1'
dotnet test .\tests\MeetingCopilot.Tests --filter FullyQualifiedName~SavedSpeechResourceStreamsSyntheticAudioAndFeedsTheQuestionModel --logger "console;verbosity=detailed"
Remove-Item Env:\MEETINGCOPILOT_LIVE_STREAMING_TEST
```

By default that check does not change settings. Explicitly setting
`MEETINGCOPILOT_ENABLE_VALIDATED_STREAMING=1` also saves the streaming provider after
success, preserving all other saved fields and refusing to overwrite changes made
while validation was running. Remove that environment variable afterward.

A separate voice-follow check uses only the saved Speech connection and an invented
script synthesized locally. It verifies provisional alignment before audio ends,
matching through the end of the script and holding position during silence. It opens
no microphone, reads no saved presentation scripts, and invokes no LLM. This is
billable Azure Speech usage and does not establish real-speaker accuracy.

```powershell
$env:MEETINGCOPILOT_LIVE_VOICE_FOLLOW_TEST = '1'
dotnet test .\tests\MeetingCopilot.Tests --filter FullyQualifiedName~SavedSpeechResourceFollowsSyntheticScriptWithoutAMicrophoneOrLlm --logger "console;verbosity=detailed"
Remove-Item Env:\MEETINGCOPILOT_LIVE_VOICE_FOLLOW_TEST
```

The opt-in slide check renders an invented slide locally (never captures the desktop),
sends it through the saved image-capable deployment, and checks image extraction and
a question grounded in the extracted slide evidence:

```powershell
$env:MEETINGCOPILOT_LIVE_SLIDE_TEST = '1'
dotnet test .\tests\MeetingCopilot.Tests --filter FullyQualifiedName~SavedDeploymentReadsASyntheticSlide --logger "console;verbosity=detailed"
Remove-Item Env:\MEETINGCOPILOT_LIVE_SLIDE_TEST
```

Architecture: `MeetingCopilot.Core` contains the model contract and conversation engine;
`MeetingCopilot.Desktop` contains WPF, WASAPI capture and Azure Speech integration.
See [the design reference](../meeting-overlay-design.md) for the implemented UX.
