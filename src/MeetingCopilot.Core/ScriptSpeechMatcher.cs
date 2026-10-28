using System.Text;
using System.Text.RegularExpressions;

namespace MeetingCopilot.Core;

public sealed record ScriptWord(string Text, string Normalized, int Start, int Length);
public sealed record ScriptMatch(int WordIndex, bool Matched, bool AtEnd);

public sealed class ScriptSpeechMatcher
{
    private static readonly Regex WordsPattern = new(@"[\p{L}\p{M}\p{Nd}]+(?:['\u2019][\p{L}\p{M}\p{Nd}]+)*",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private readonly string[] words;
    private int segmentAnchor = -1;
    private string? lastFinal;
    public int CurrentWord { get; private set; } = -1;
    public int WordCount => words.Length;
    public bool AtEnd => words.Length > 0 && CurrentWord == words.Length - 1;

    public ScriptSpeechMatcher(IEnumerable<string> scriptWords) =>
        words = scriptWords.Select(Normalize).ToArray();

    public static IReadOnlyList<ScriptWord> Tokenize(string text) => WordsPattern.Matches(text)
        .Select(m => new ScriptWord(m.Value, Normalize(m.Value), m.Index, m.Length)).ToArray();

    private static string Normalize(string word) =>
        word.Normalize(NormalizationForm.FormKC).Replace("'", "").Replace("\u2019", "").ToLowerInvariant();

    public void Reset(int nextWord)
    {
        if (nextWord < 0 || nextWord > words.Length) throw new ArgumentOutOfRangeException(nameof(nextWord));
        CurrentWord = segmentAnchor = nextWord - 1;
        lastFinal = null;
    }

    public ScriptMatch Observe(string text, bool final)
    {
        // Each partial replaces the current recognition hypothesis; it is not appended.
        var heard = Tokenize(text).Select(w => w.Normalized).Take(96).ToArray();
        string normalized = string.Join(' ', heard);
        if (heard.Length == 0) return new(CurrentWord, false, AtEnd);
        if (final && normalized == lastFinal && CurrentWord == segmentAnchor) return new(CurrentWord, false, AtEnd);
        int next = segmentAnchor + 1;
        int bestIndex = -1;
        double bestScore = double.NegativeInfinity;
        int required = heard.Length == 1 ? 1 : Math.Max(2, (int)Math.Ceiling(heard.Length * 0.65));
        // Stay near the reading position. A distant repeated phrase must not jump the script.
        for (int start = Math.Max(0, next - 4); start < Math.Min(words.Length, next + 17); start++)
        {
            if (heard.Length == 1)
            {
                if (start != next && start != CurrentWord) continue;
                if (words[start] == heard[0] && (bestIndex < 0 || start == CurrentWord)) bestIndex = start;
                continue;
            }
            int count = Math.Min(words.Length - start, heard.Length + 6);
            var scores = new Alignment[heard.Length + 1, count + 1];
            for (int i = 1; i <= heard.Length; i++) scores[i, 0] = new(-i, 0, -1);
            for (int j = 1; j <= count; j++) scores[0, j] = new(-j * 1.5, 0, -1);
            for (int i = 1; i <= heard.Length; i++)
            {
                for (int j = 1; j <= count; j++)
                {
                    var skipSpeech = scores[i - 1, j] with { Score = scores[i - 1, j].Score - 1 };
                    var skipScript = scores[i, j - 1] with { Score = scores[i, j - 1].Score - 1.5 };
                    var best = skipSpeech.Score >= skipScript.Score ? skipSpeech : skipScript;
                    if (heard[i - 1] == words[start + j - 1])
                    {
                        var previous = scores[i - 1, j - 1];
                        var match = new Alignment(previous.Score + 3, previous.Matches + 1, i - 1);
                        if (match.Score >= best.Score) best = match;
                    }
                    scores[i, j] = best;
                    if (i != heard.Length || heard[i - 1] != words[start + j - 1] ||
                        best.LastSpeech != i - 1 || best.Matches < required) continue;
                    int index = start + j - 1;
                    if (index < CurrentWord || index > CurrentWord + heard.Length + 16) continue;
                    double score = best.Score - Math.Abs(start - next) * 0.5;
                    if (score > bestScore || score == bestScore && index < bestIndex)
                    {
                        bestScore = score;
                        bestIndex = index;
                    }
                }
            }
        }
        bool matched = bestIndex >= CurrentWord && bestIndex >= 0;
        if (matched) CurrentWord = bestIndex;
        if (final)
        {
            segmentAnchor = CurrentWord;
            lastFinal = normalized;
        }
        return new(CurrentWord, matched, AtEnd);
    }

    private readonly record struct Alignment(double Score, int Matches, int LastSpeech);
}
