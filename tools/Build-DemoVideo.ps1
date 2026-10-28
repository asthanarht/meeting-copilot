param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\demo-video'),
    [switch]$RenderOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Speech
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$captures = Join-Path $OutputDirectory 'captures'
$work = Join-Path $OutputDirectory 'production'
[System.IO.Directory]::CreateDirectory($work) | Out-Null
$culture = [System.Globalization.CultureInfo]::InvariantCulture

$scenes = @(
    @{
        Title = "Stay in the`nconversation."
        Detail = 'Useful contributions while the meeting is happening.'
        Note = 'Actual app UI. Prewritten sample suggestions.'
        Images = @('question'); Crop = @(0,0,834,520)
        Lines = @('Meet Meeting Copilot, a private desktop assistant for live discussions.',
                  'It helps you contribute useful questions and recommendations, while the meeting is still happening.')
    },
    @{
        Title = "Prepare.`nThen listen."
        Detail = 'Choose meeting playback. Add an optional brief. Confirm consent.'
        Note = 'No real audio or meeting data used in this demo.'
        Images = @('setup'); Crop = @(16,165,730,660)
        Lines = @('Choose your meeting playback device, and optionally include your microphone.',
                  'Add a short brief, confirm participant consent, then start the live overlay.')
    },
    @{
        Title = "A question.`nWith context."
        Detail = 'Read the reason, the evidence and the uncertainty.'
        Note = 'Synthetic example, not a live model response.'
        Images = @('question','question-why'); Crop = @(12,10,810,510)
        Lines = @('When finalized speech reveals a useful gap, the overlay can suggest a grounded question.',
                  'Open the explanation to review its evidence and uncertainty.')
    },
    @{
        Title = "Go beyond`nclarification."
        Detail = 'Recommendations explain an action, its benefit and its trade-off.'
        Note = 'Sample recommendation: backoff with jitter.'
        Images = @('recommendation'); Crop = @(12,100,810,405)
        Lines = @('It can also propose an approach, not just ask another question.',
                  'Here, a sample recommendation suggests backoff with jitter to spread retries during recovery.')
    },
    @{
        Title = "Your words.`nYour decision."
        Detail = 'Copy a suggestion. Browse history. Dismiss what no longer helps.'
        Note = 'Nothing is automatically spoken or posted to Teams.'
        Images = @('copy','history'); Crop = @(8,90,818,670)
        Lines = @('Copy a suggestion, mark it already covered, or dismiss it.',
                  'Browse earlier suggestions without posting anything to Teams. You decide what to say.')
    },
    @{
        Title = "Keep the`nthread."
        Detail = 'Continuous analysis. Full session history in memory until Stop.'
        Note = 'Individual requests are bounded. Recall is not perfect.'
        Images = @('context'); Crop = @(8,185,818,590)
        Lines = @('Analysis continues throughout longer meetings, with recent speech and relevant earlier context.',
                  'Session history stays in memory until Stop, rather than waiting until the meeting ends.')
    },
    @{
        Title = "Choose your`nAI provider."
        Detail = 'Foundry, Gemini, Claude and Grok. Separate encrypted credentials.'
        Note = 'Enter a supported model ID and your own API key.'
        Images = @('foundry','gemini','claude','grok'); Crop = @(16,215,730,480)
        Lines = @('Keep Foundry, or select Gemini, Claude, or Grok.',
                  'Each provider has its own encrypted credentials. Existing speech and teleprompter features stay independent.')
    },
    @{
        Title = "Cloud.`nOr local."
        Detail = 'OpenAI-compatible endpoints, adjustable limits and a connection test.'
        Note = 'Local model inference still uses Azure Speech for audio.'
        Images = @('local','connection-test'); Crop = @(16,215,730,480)
        Lines = @('Open AI compatible endpoints support cloud models and local servers.',
                  'Test with synthetic text before a meeting. Azure Speech still handles audio.')
    },
    @{
        Title = "Prepare your`npresentation."
        Detail = 'Save scripts. Open the reader. Keep one app screen at a time.'
        Note = 'Scripts are saved locally with Windows account encryption.'
        Images = @('library','reader'); Crop = $null
        Lines = @('For presentations, save your script in the built-in library.',
                  'Open the reader without starting meeting capture. Your saved script stays separate from meeting context.')
    },
    @{
        Title = "A focused`nteleprompter."
        Detail = 'Run. Stop. Scroll manually. Return to your script library.'
        Note = 'Actual offline scrolling. Run for eight seconds, then stop.'
        Images = @('minimal'); Crop = $null
        Lines = @('The minimal overlay keeps your script above your slides.',
                  'Run starts offline timed scrolling. Stop holds your place. You can also scroll manually.')
    },
    @{
        Title = "Follow your`nvoice."
        Detail = 'Matched words guide the reading position. Silence holds it.'
        Note = 'Simulated recognition. Real use needs Azure Speech permission.'
        Images = @('voice-follow'); Crop = @(8,100,728,680)
        Lines = @('Voice following highlights matched words and holds position when you stop.',
                  'Microphone audio goes to Azure Speech with your permission. Script matching stays local.')
    },
    @{
        Title = "Stay in`ncontrol."
        Detail = 'Review AI advice. Verify screen sharing. Speak with confidence.'
        Note = 'Capture exclusion depends on the sharing tool. Verify Teams.'
        Images = @('capture-privacy'); Crop = @(8,325,818,420)
        Lines = @('Capture exclusion depends on your sharing tool. Check another participant view.',
                  'Review every suggestion. Meeting Copilot helps you contribute; you remain in control.')
    }
)

function Number([double]$value) { $value.ToString('0.000', $culture) }
function Duration([string]$path) {
    $result = & ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $path
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect audio: $path" }
    return [double]::Parse($result, $culture)
}
function SrtTime([double]$seconds) {
    $span = [TimeSpan]::FromMilliseconds([Math]::Round($seconds * 1000))
    return '{0:00}:{1:00}:{2:00},{3:000}' -f [int]$span.TotalHours,$span.Minutes,$span.Seconds,$span.Milliseconds
}
function AssTime([double]$seconds) {
    $span = [TimeSpan]::FromMilliseconds([Math]::Round($seconds * 1000))
    return '{0}:{1:00}:{2:00}.{3:00}' -f [int]$span.TotalHours,$span.Minutes,$span.Seconds,[int][Math]::Floor($span.Milliseconds/10)
}
function WriteUtf8([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}
function Font([float]$size, [bool]$bold = $false) {
    $style = if ($bold) { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }
    return New-Object System.Drawing.Font('Segoe UI', $size, $style, [System.Drawing.GraphicsUnit]::Pixel)
}
function Brush([string]$hex) { return New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($hex)) }

$fontHeader = Font 30 $true
$fontTitle = Font 70 $true
$fontDetail = Font 32
$fontNote = Font 23
$fontFooter = Font 22
$white = Brush '#F0F4FA'
$muted = Brush '#B7C3D4'
$accent = Brush '#A9C7FF'
$surface = Brush '#141B24'
$outline = New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml('#435266'), 2)
$icon = [System.Drawing.Image]::FromFile((Join-Path $root 'src\MeetingCopilot.Desktop\Assets\MeetingCopilot.png'))
$format = New-Object System.Drawing.StringFormat
$format.Trimming = [System.Drawing.StringTrimming]::Word

function Text($g, [string]$text, $font, $brush, [float]$x, [float]$y, [float]$w, [float]$h) {
    $g.DrawString($text, $font, $brush, (New-Object System.Drawing.RectangleF($x,$y,$w,$h)), $format)
}

function Compose([int]$index, [string]$imagePath, [string]$destination, $crop) {
    $scene = $scenes[$index]
    $canvas = New-Object System.Drawing.Bitmap(1920,1080)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.Clear([System.Drawing.ColorTranslator]::FromHtml('#0B1119'))
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.DrawImage($icon, 72,40,52,52)
    Text $g 'Meeting Copilot' $fontHeader $white 144 47 500 55
    Text $g 'Product walkthrough  |  Synthetic examples' $fontFooter $muted 1260 54 600 40
    Text $g $scene.Title $fontTitle $white 80 265 665 235
    $g.FillRectangle($accent, 84,525,66,5)
    Text $g $scene.Detail $fontDetail $muted 80 566 640 174
    Text $g $scene.Note $fontNote $accent 80 772 630 92
    $image = [System.Drawing.Image]::FromFile($imagePath)
    try {
        if ($null -eq $crop) { $crop = @(0,0,$image.Width,$image.Height) }
        $scale = [Math]::Min(1030.0/$crop[2], 738.0/$crop[3])
        $width = [int]($crop[2]*$scale)
        $height = [int]($crop[3]*$scale)
        $left = [int](810+(1030-$width)/2)
        $top = [int](145+(738-$height)/2)
        $g.FillRectangle($surface, $left-2,$top-2,$width+4,$height+4)
        $g.DrawImage($image, (New-Object System.Drawing.Rectangle($left,$top,$width,$height)),
            [float]$crop[0],[float]$crop[1],[float]$crop[2],[float]$crop[3], [System.Drawing.GraphicsUnit]::Pixel)
        $g.DrawRectangle($outline, $left-2,$top-2,$width+4,$height+4)
    }
    finally { $image.Dispose() }
    $g.DrawLine($outline,80,926,1840,926)
    Text $g ('{0:00} / 12' -f ($index+1)) $fontFooter $muted 80 1038 200 30
    Text $g 'Actual app screens  |  Local synthetic narration' $fontFooter $muted 1260 1038 600 30
    $canvas.Save($destination,[System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $canvas.Dispose()
}

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SelectVoice('Microsoft Zira Desktop')
$synth.Rate = 2
$synth.Volume = 100
$captionIndex = 0
$srt = New-Object System.Text.StringBuilder
$manifest = @()
try {
    for ($i=0; $i -lt $scenes.Count; $i++) {
        $scene = $scenes[$i]
        $folder = Join-Path $work ('scene-{0:00}' -f $i)
        [System.IO.Directory]::CreateDirectory($folder) | Out-Null
        $audio = @()
        $durations = @()
        for ($j=0; $j -lt 2; $j++) {
            $wave = Join-Path $folder "voice-$j.wav"
            if (-not $RenderOnly) {
                $synth.SetOutputToWaveFile($wave)
                $synth.Speak($scene.Lines[$j])
                $synth.SetOutputToNull()
            }
            $audio += $wave
            $durations += Duration $wave
        }
        $speed = [Math]::Max([double]1, [double](($durations[0]+$durations[1])/8.8))
        if ($speed -gt 1.22) { throw "Scene $i narration is too long. Shorten it rather than rushing the voice." }
        $start1 = 0.45
        $end1 = $start1 + $durations[0]/$speed
        $start2 = $end1 + 0.20
        $end2 = $start2 + $durations[1]/$speed
        if ($end2 -gt 9.5) { throw "Scene $i narration would be cut off." }
        $ass = @"
[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080
WrapStyle: 0
[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Segoe UI,36,&H00F0F4FA,&H00F0F4FA,&H0019110B,&H0019110B,0,0,0,0,100,100,0,0,1,1,0,2,170,170,80,1
[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"@
        foreach ($j in 0,1) {
            $start = if ($j -eq 0) { $start1 } else { $start2 }
            $end = if ($j -eq 0) { $end1 } else { $end2 }
            $line = $scene.Lines[$j]
            $ass += "`nDialogue: 0,$(AssTime $start),$(AssTime $end),Default,,0,0,0,,{\fad(100,100)}$line"
            $captionIndex++
            [void]$srt.AppendLine("$captionIndex")
            [void]$srt.AppendLine("$(SrtTime ($i*10+$start)) --> $(SrtTime ($i*10+$end))")
            [void]$srt.AppendLine($line)
            [void]$srt.AppendLine()
        }
        WriteUtf8 (Join-Path $folder 'captions.ass') $ass
        $concat = New-Object System.Text.StringBuilder
        $count = if ($i -eq 9) { 250 } else { $scene.Images.Count }
        for ($k=0; $k -lt $count; $k++) {
            if ($i -eq 9) {
                $source = Join-Path $captures ('minimal-{0:000}.png' -f $k)
            } elseif ($scene.Images[$k] -eq 'voice-follow') {
                $source = Join-Path $root 'artifacts\screenshots\voice-follow-reader.png'
            } else {
                $source = Join-Path $captures ($scene.Images[$k]+'.png')
            }
            $frame = 'frame-{0:000}.png' -f $k
            Compose $i $source (Join-Path $folder $frame) $scene.Crop
            [void]$concat.AppendLine("file '$frame'")
            [void]$concat.AppendLine("duration $(Number (10.0/$count))")
        }
        [void]$concat.AppendLine("file '$frame'")
        WriteUtf8 (Join-Path $folder 'frames.txt') $concat.ToString()
        $filter = "[1:a]atempo=$(Number $speed),adelay=450:all=1[a1];[2:a]atempo=$(Number $speed),adelay=$([int][Math]::Round($start2*1000)):all=1[a2];[a1][a2]amix=inputs=2:normalize=0,apad,atrim=0:10,afade=t=out:st=9.8:d=0.2[a];[0:v]fps=25,subtitles=captions.ass,drawbox=x=80:y=1018:w=1760:h=3:color=0x293F5F:t=fill,drawbox=x=80:y=1018:w=$([int](1760*($i+1)/12)):h=3:color=0xA9C7FF:t=fill,format=yuv420p[v]"
        Push-Location $folder
        try {
            $inputArgs = if ($i -eq 9) { @('-framerate','25','-i','frame-%03d.png') } else { @('-f','concat','-safe','0','-i','frames.txt') }
            & ffmpeg -hide_banner -loglevel error -y @inputArgs -i voice-0.wav -i voice-1.wav -filter_complex $filter -map '[v]' -map '[a]' -t 10 -r 25 -c:v libx264 -preset fast -crf 19 -pix_fmt yuv420p -c:a aac -b:a 160k -ar 48000 -ac 2 -movflags +faststart segment.mp4
            if ($LASTEXITCODE -ne 0) { throw "Encoding scene $i failed." }
        }
        finally { Pop-Location }
        $manifest += [pscustomobject]@{ Scene=$i+1; Start=$i*10; Duration=10; VoiceSpeed=$speed; VoiceEnd=$end2; Text=$scene.Lines }
        Write-Host ('Rendered scene {0}/12' -f ($i+1))
    }
    WriteUtf8 (Join-Path $OutputDirectory 'Meeting-Copilot-Demo.en.srt') $srt.ToString()
    WriteUtf8 (Join-Path $work 'production.json') ($manifest | ConvertTo-Json -Depth 5)
    $join = (0..11 | ForEach-Object { "file 'scene-{0:00}/segment.mp4'" -f $_ }) -join "`n"
    WriteUtf8 (Join-Path $work 'segments.txt') $join
    Push-Location $work
    try {
        & ffmpeg -hide_banner -loglevel error -y -f concat -safe 0 -i segments.txt -map 0:v -map 0:a -vf 'setpts=PTS-STARTPTS' -c:v libx264 -preset fast -crf 18 -pix_fmt yuv420p -af 'asetpts=PTS-STARTPTS,loudnorm=I=-16:TP=-1.5:LRA=7,apad' -c:a aac -b:a 192k -ar 48000 -ac 2 -t 120 -movflags +faststart -metadata title='Meeting Copilot - Two-minute product walkthrough' -metadata comment='Actual app UI with synthetic examples. Local synthetic English narration. Not a recording of a real meeting.' (Join-Path $OutputDirectory 'Meeting-Copilot-Demo.mp4')
        if ($LASTEXITCODE -ne 0) { throw 'Final assembly failed.' }
    }
    finally { Pop-Location }
}
finally {
    $synth.Dispose()
    $icon.Dispose()
    $format.Dispose()
    foreach ($item in @($fontHeader,$fontTitle,$fontDetail,$fontNote,$fontFooter,$white,$muted,$accent,$surface,$outline)) { $item.Dispose() }
}
Write-Host (Join-Path $OutputDirectory 'Meeting-Copilot-Demo.mp4')
