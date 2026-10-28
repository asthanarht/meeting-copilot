# Live meeting copilot - desktop design

**Status:** Design reference for the implemented C# / WPF desktop interface in `src`.
See `README.md` for supported behavior, startup instructions and remaining limitations.
Live Azure recognition and model quality still require validation with approved credentials.

**Surface:** Movable, resizable desktop overlay above Microsoft Teams.

**Initial audience:** People participating in technical design discussions.

**Platform:** Windows / WPF.

## Design thesis

A quiet cue card near your camera, not a transcript or a scrolling script. The assistant follows the live discussion and privately surfaces a recommendation, a question, a potential gap, or an alternative while it is still useful. You decide whether and how to speak.

Success means noticing a useful contribution with a brief glance, without losing track of the speaker. Generating more suggestions is not success.

## Main overlay

Place the overlay near the top center by default, just below the camera. Let the user move it away from shared content or faces and remember its position per display.

Illustrative content, not an actual meeting:

```text
 +---------------------------------------------------------+
 | :: Meeting copilot       Listening     Pause   Minimize |
 |---------------------------------------------------------|
 | QUESTION TO CONSIDER                                    |
 |                                                         |
 | If a payment request is retried,                         |
 | what prevents a duplicate charge?                       |
 |                                                         |
 | Based on the retry flow being discussed.                 |
 |                                                         |
 | Why this?                  Dismiss       Already covered |
 +---------------------------------------------------------+
```

The question is the focal point. Controls remain quiet but readable. "Listening" indicates
an active session; detailed per-source audio status appears in the Speech tab. It does not
promise that every speaker has been recognized.

**Implemented dimensions:** Focus view starts at 660 x 560 logical pixels; context expands
to 860 high, subject to the work area. Minimum focused size is 520 x 500. Showing history
increases the minimum height to preserve usable question space. Long questions scroll,
not truncate. Controls wrap on narrower windows.

**Visual direction:** An opaque slate workspace for glancing at a question alongside moving
meeting video. Off-white text, a restrained blue action color, system Segoe UI, clear
separators and a small, consistent control radius. No glowing borders, decorative
waveform or continuous typing effect. Windows high-contrast mode is supported; a separate
light theme is not implemented.

### Shared interface system

`Themes/Controls.xaml` owns the color roles and native control templates across setup,
the overlay and slide review. Surface `#141B24`, raised surface `#1D2733`, foreground
`#F0F4FA`, secondary text `#B7C3D4` and accent `#A9C7FF` are the main roles.
Body text is 14, support text 12-13, section headings 20 and the question 27.
Controls use 36-38 pixel minimum heights, 5-6 pixel radii and visible keyboard focus.
Measured primary, secondary, selected and button text pairs meet 4.5:1 contrast.
High-contrast mode replaces semantic brushes with Windows system colors.

Setup has Meeting, Connections and Privacy tabs; consent, errors and Start stay anchored
below the scrollable form. It defaults to the frequent task (preparing a meeting), not
the infrequent task of editing credentials. Connection validation reveals Connections.
The question remains above a separate, scrollable detail area. Speech, AI context and
Display are mutually exclusive tabs, rather than a wall of diagnostic text.

Meeting AI preserves existing Foundry/legacy choices and adds OpenAI-compatible,
Gemini, Claude and Grok connections. Each external provider owns a separate encrypted
profile: destination, model, key and limits. Switching providers does not copy credentials;
editing the URL clears that profile's key. The destination stays visible, and generic
protocol knobs plus request limits are disclosed under an expander rather than mixed
into the required fields. Native providers hide irrelevant compatibility controls.

Test model connection has explicit pending, cancel, success and failure states. It
uses synthetic text, not audio, meeting briefs or scripts, and warns about billing.
Editing, switching providers or closing cancels the request; late results cannot
validate a different configuration. Live capture does its own synthetic preflight.
Unsupported slide-image actions are visibly disabled on the new text-only adapters;
existing vision-capable Foundry connections retain slide capture.

## Shared window chrome and icons

The shared window style applies native DWM caption, text and border colors from the
slate theme on Windows 11. Windows 10 uses dark native chrome where supported. Native
caption controls, resizing, Snap and the system menu are preserved rather than replaced
with custom hit-testing. High contrast resets DWM colors to system defaults; borderless
presentation and region-selection surfaces keep their existing behavior.

The application mark pairs a blue speech bubble with an M-shaped voice line. Its
editable SVG is the source for the bundled multi-resolution ICO and PNG; the executable,
window icons and setup header use those exports. Action icons share a 24-unit geometry
grid and 1.8-unit round strokes, rendered at 16px alongside unchanged text labels.
They inherit foreground, focus and disabled states from the existing button template.
Icons are supplementary, not substitutes for accessible action names.

## Presentation workspace

The teleprompter extends the same Operate-mode interface without turning the question
overlay into a script. Setup exposes a separate **Open teleprompter** entry; the live
overlay links to it from Display. Existing meeting capture, if any, remains independent.
Navigation shows one app screen at a time: setup or the meeting overlay hides when the
library opens, and the library hides when a reader opens. Reader close returns to the
library; library Back or close restores the originating screen. Existing readers are
reused. Readers have no native owner, preventing owner visibility changes from hiding
the active reader; the library explicitly coordinates their lifetime and cleanup.

The library uses a persistent left-hand script list and a titled editor, with explicit
Save, New and confirmed Delete actions. Unsaved navigation and close are guarded.
Titles, scripts and reading settings are persisted in a separate DPAPI-encrypted local
library, never in meeting context. Concurrent same-script writes are rejected with a
visible error; the draft remains available.

The always-on-top reader begins paused and uses the existing slate surface and system
font. A restrained guide near the top establishes a reading position; upcoming text
fills the remaining width. Playback and paragraph controls remain below the text.
Speed, font, spacing and width are adjustable; advanced settings collapse on Play.
The mode selector separates **Follow my voice** from **Timed / offline**. Voice following
shows the saved microphone and an unsaved, explicit Azure Speech permission before Start.
Only microphone audio goes to Speech; script matching is local and uses no LLM or script
upload. Interim matches move the current line to the guide and enlarge the current word
to 110% in an inline overlay, avoiding text reflow or an unprotected popup window.
Silence or unmatched speech holds position, with no timed motion. A clicked word anchors
the next read. Recognition latency and corrections are visible rather than hidden.
The exact microphone must be available; failures do not fall back to another device or
to timed movement.

Timed mode uses elapsed-time-based scrolling and remains offline. Both modes start paused,
stop at the end and pause on layout changes; prolonged rendering stalls pause timed mode.
Opening either window starts no capture or network request. Voice capture stops when
permission is revoked, the mode changes, the reader pauses/minimizes or either window
closes. Reader disposal completes before the library closes and saves reading settings.

Shortcuts are local to the focused reader. The reading settings are saved on close;
the reading position is intentionally transient. Both the library and reader request
their own capture exclusion; the capture limitations of the meeting overlay still apply.

### Minimal slide overlay

An additional **Open minimal overlay** action in the library serves the slide-presenting
case without replacing the full reader. The Operate-mode surface reduces to large script
text and Run/Stop, with a small textual playback state and a compact top-right close
button and top-left back arrow, both with accessible names. No title bar, connection panel,
settings or microphone controls compete with the presentation. A translucent slate
surface (`#AA141B24`) keeps white script text readable over both light and dark slides;
high contrast deliberately replaces transparency with the system window color.

The borderless window sits above slides, with a top drag grip and bottom resize grip.
Manual scroll, resize or move stops timed motion; Stop never resets. Run resumes or
restarts at the end. It reuses saved script typography and the deterministic timed
playback model, with no speech or network dependency and no change to saved reader mode.
Library help carries movement, close and privacy instructions so the overlay stays small.
The library stays hidden while reading. The overlay's back arrow stops motion and
returns to the library without closing the overlay or losing position. Show overlay
restores it, still stopped until Run, and hides the library again. X closes the overlay
and restores the library. Closing the library closes even a hidden overlay and restores
the originating setup or meeting screen. Private text remains hidden
until capture exclusion is confirmed on opening or reshowing; failure
closes the overlay and produces a visible library error rather than an unprotected fallback.

## Four useful forms of contribution

| Label | Example | Required grounding |
|---|---|---|
| Recommendation to propose | Reuse the original payment's idempotency key on retries to avoid duplicate charges. | A stated duplicate-charge risk and gateway support justify the action; distinguish a proposed fix from an existing fact and explain trade-offs. |
| Question to consider | What prevents duplicate charges during retries? | The proposal includes retries; duplicate handling remains unresolved in available context. |
| Potential gap | A timeout does not tell us whether the payment succeeded. | Identify the specific uncertainty, without asserting the implementation is broken. |
| Alternative to explore | Could we reuse a payment idempotency key across retries? | Tie the approach to the discussed problem and state any relevant assumption. |

Keep one contribution prominent. At the user's request, provide an optional inline
history list rather than replacing the main cue with a continuously scrolling feed.
Previous/Next and Live controls let the user navigate every generated contribution
without a new one stealing their place. Historical entries retain timestamps, evidence
and lifecycle labels and are never presented as current advice.

### Expanded speech and context view

**Show context** reveals supporting detail below the contribution; **Focus view** returns
to the question-first surface. The Speech tab separates provisional words from the full
session's finalized utterances, with timestamps and source labels.
The **AI context** tab shows the supplied brief, slide summaries and model-generated memory,
explicitly noting that new speech may not yet have been analyzed. These read-only
panes scroll and support copying; compact mode hides them without clearing context.
They are an inspection view of the live context, not a saved or unlimited transcript.

### On-demand slide context

**Capture slide** opens a region selector, followed by a local preview and explicit
**Send slide to AI** approval. Cancel/Escape sends nothing. Only the selected rectangle
is read; no continuous capture, clipboard or disk image persistence is involved.
Images are bounded to 1600 pixels on the longest side and 6 MB; each confirmed capture
uses one image-analysis call through the configured Responses deployment.

The slide summary separates extracted visible content, interpretation and uncertainty.
It is timestamped and labeled as slide evidence, not speech or a claim of what is still
on screen. Its summary joins session history and bounded AI requests without blocking speech reasoning. The source image
is released after analysis. Pause, Stop and explicit cancellation reject late results.
The user must inspect the preview for cropping, notifications and private material.

### Browsable suggestion history

**Browse suggestions** reveals an inline list inside the same capture-protected window.
Browsing freezes selection, not live analysis. **Live (N new)** reports arrivals and
returns to current advice. Previous/Next works in compact and expanded modes.
Answered, superseded, expired, dismissed and covered questions remain navigable.
Feedback on a historical item applies to that item, never a different live cue.
All entries remain in session memory until Stop; they are not saved after the meeting.

**Copy** sits alongside the main suggestion actions in both views. It copies the
displayed contribution only, whether live or historical; it does not accidentally copy
new pending wording or the supporting transcript. Pointer/keyboard interaction with
Copy holds the current wording. Inline success/failure feedback replaces silent failure.
Copy is disabled for the empty state. Clipboard history/sync may retain explicitly
copied text after Stop; capture exclusion protects the window, not the clipboard.

## Reveal reasoning on demand

Selecting **Why this?** expands the same panel downward:

> **Discussion context:** The proposed flow retries a payment after a timeout.
>
> **Why it matters:** The first request may have completed even if its response was lost.
>
> **Uncertainty:** Duplicate handling has not been established in the available discussion; it may exist elsewhere.

Use a timestamped transcript excerpt only when actual transcript evidence is available. Otherwise label the context as a paraphrase. Do not invent quotations or numerical confidence scores.

## Live behavior

1. **Start:** Choose and verify the permitted meeting-audio source. Explain what audio is processed, where processing occurs, and the retention policy. Require explicit start after the relevant permissions and consent requirements are addressed. A local checkbox alone does not establish participant consent.
2. **Listen:** With no worthwhile suggestion, stay in a compact strip: `Listening | No suggestion right now | Pause | Expand`. Do not occupy a large empty panel.
3. **Suggest:** Surface a concise, complete thought at an appropriate conversational moment. Never steal keyboard focus, play a sound by default, or move the window unexpectedly.
4. **Read:** Keep the wording stable while the user interacts with the suggestion. Do not stream word-by-word rewrites. If a better suggestion arrives, defer replacement while the current one is in use.
5. **Resolve:** If the discussion answers the question or moves beyond it, mark the suggestion as addressed or no longer current. Collapse it when the user is no longer interacting; never leave obsolete advice looking current.
6. **Control:** Dismiss hides that suggestion. Already covered also tells the assistant not to repeat the same point without new evidence. Pause stops audio capture and analysis, cancels pending suggestion delivery, and visibly marks the overlay paused.
7. **End:** Explicit stop ends capture and clears transient context according to the disclosed retention policy. No post-meeting summary is required.

**Suggest now** explicitly requests the strongest grounded contribution from current
context even without new speech. Automatic analysis and manual requests both prefer a
concrete recommendation when the known risk/goal and constraints support it. Missing
essential information still warrants clarification. The model explains benefits and
trade-offs without treating its proposed solution as an existing meeting fact. Adopted
actions retire the cue, and a materially better recommendation may replace a stale
question. Silence remains valid; advice is not manufactured to fill space.

The analysis indicator distinguishes waiting, queued speech, an active model request,
completed analysis and failure. It includes the checked revision, request latency and
the model's brief explanation when no suggestion is selected. This explanation is an
outcome, not hidden model reasoning. No diagnostic transcript is persisted.

The implementation treats persistent actionable ambiguity as a reason to ask a grounded
clarification; a meeting brief is not required. Ongoing speech no longer discards every
completed model result. Instead, cues based on a recent snapshot are explicitly provisional
while the next analysis confirms or retracts them. Expiry is checked between requests
(45 seconds normally, 15 seconds continuously provisional), not by a hard display timer.
Interaction with the speech/context panes does not defer suggestions; interaction with
the suggestion, explanation, Copy and dismissal controls keeps wording stable. Freshness status
and retractions remain immediate.
The question stays above the independently scrolling details, including while inspecting
context. Compact mode hides details but keeps the question and controls visible. A first
question is not deferred over an empty placeholder; deferred replacement wording has an
explicit **Show new suggestion** action.

## Window and accessibility behavior

- Drag using the header; resize using edges. Provide keyboard-accessible positioning and size presets as alternatives.
- Compact mode keeps the cue and controls while hiding speech/context details; it does not stop listening.
- Use readable adjustable text, visible focus indicators, labeled controls, reduced-motion support, and status text rather than color alone.
- Do not announce every automatic suggestion to a screen reader by default. Allow the user to enable announcements and read the current cue on demand.
- Restore the overlay inside the visible desktop bounds after display changes. Avoid covering Teams meeting controls by default.
- No click-through mode in the first version: it makes controls difficult to discover and can send accidental clicks to Teams.

## Important states

| State | What the user sees |
|---|---|
| Not started | Start listening; audio and processing disclosure available. |
| Connecting | Connecting to audio; never a premature Listening indicator. |
| Listening, no useful cue | Compact listening strip; silence is a valid result. |
| Suggestion ready | One grounded prompt with optional reasoning. |
| Paused | Paused - audio capture stopped; Resume remains visible. |
| Audio unavailable | Meeting audio unavailable; explain how to reconnect. |
| Analysis delayed or offline | Suggestions delayed/unavailable; do not present stale cues as live. |
| Question addressed | Addressed in discussion; retire the cue without replacing text under interaction. |
| Stopped | Capture ended; retention behavior follows the disclosed policy. |

## Privacy and screen sharing

"Private" means the assistant does not speak or post suggestions to the meeting. It does not automatically mean invisible in a screen share.

The implementation requests Windows `WDA_EXCLUDEFROMCAPTURE` by default and confirms
the flag with native readback before reporting it as on. Setup and **Display settings**
expose a persisted opt-out, and a visible status reports failures. Exclusion applies to
both overlay sizes, not the setup window. Native on/off behavior is verified; Teams
remote sharing still needs verification. Compatible capture tools omit the window, but
not every capture method respects this setting. Do not promise invisibility; recommend
sharing a specific application window when unsure. Cameras can still see the display.

Hiding the overlay and stopping listening are separate actions and must remain clearly distinguished. Do not claim automatic screen-share detection until its reliability has been established.

## First-version boundaries

Include the live overlay, optional pre-meeting design-document context, the four contribution types, evidence disclosure, clipboard copy, dismissal, pause/resume, explicit stop, and recoverable failure states.

Exclude autonomous speech, Teams chat posting, full transcript feeds, post-meeting dashboards, rolling teleprompter text, and automatic acceptance of design advice.

The initial implementation uses WPF, WASAPI capture, Azure Speech continuous streaming, and a user-configured
model deployment via the Foundry Responses API with an encrypted, saved resource
API key or Microsoft Entra authentication. Responses verifies synthetic inference
before opening audio devices. MAI short uploads and API-key Chat Completions
remain explicit alternatives.
Native Gemini and Claude, Grok Responses and generic OpenAI-compatible text inference
are also available through the same grounded-result contract. Provider selection
does not change Azure Speech recognition or the offline teleprompter.
Live use still requires valid resource access and consented audio; see the user guide
for authentication troubleshooting and privacy/latency limitations.

## Proposed listening and reasoning pipeline

**Implemented selection:** Azure Speech continuous recognition is now recommended and
the new-setup default after MAI short uploads exceeded the live latency limit. Streaming
publishes partial words and final utterances over a persistent connection; the
resource endpoint can be used without knowing its region. MAI-Transcribe-2 remains
selectable for short in-memory WAV uploads. See `README.md` for latency,
segmentation, preview availability and accuracy limitations.

Audio capture -> streaming speech recognition -> rolling discussion context -> language-model analysis -> relevance and staleness checks -> overlay.

Display provisional transcripts, but analyze finalized speech to avoid claims based on
recognition revisions. Retain full session speech, slide summaries and distinct AI summary
versions in memory until Stop. Bound each model request rather than deleting old meeting
history. Every request includes latest live speech, even when an ordered historical batch
is being processed after a delay. Relevant earlier utterances and summaries are selected
locally by keyword overlap; this is not a guarantee of exact or semantic recall.

Analysis remains continuous during the meeting: one in-flight request, a default 45-second
deadline (external profiles optionally allow 60/90 seconds) and an eight-second retry delay. The timing area updates independently every
second with speech age, last successful analysis age/duration, active elapsed time,
retry/error state and preserved summary count. Reading a historical cue must not rewind
these live indicators. Stop does not trigger deferred question generation.

Speech recognition does not itself detect design flaws. The selected language
model generates evidence-grounded recommendations, questions and alternatives. Actual live latency,
suggestion usefulness and cost still require representative evaluation.
No text-to-speech model is needed for the private text-overlay version.

**Alternative to evaluate:** OpenAI `gpt-live-transcribe`, currently documented for incremental live transcription through the Realtime API. Benchmark it against Azure Speech on consented representative meeting audio; do not assume either is more accurate for the user's accents, terminology, or languages.

Neither service automatically gains access to Teams audio. The desktop app must obtain a permitted audio source. Other participants' playback and the user's microphone may require separate capture paths, with echo and duplication handling.

References:
- [Azure Speech speech-to-text overview](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/speech-to-text)
- [OpenAI realtime transcription](https://developers.openai.com/api/docs/guides/realtime-transcription)

## What the prototype must prove

The user can read a cue without losing the discussion; cues arrive while relevant; already-answered questions are suppressed; source evidence supports suggestions; and pause reliably stops capture and analysis. Measure end-to-end delay and distraction with consented live trials rather than claiming human-level meeting judgment.
