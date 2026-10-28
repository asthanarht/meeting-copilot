using System.Text.RegularExpressions;

namespace MeetingCopilot.Core;

internal static class MeetingContextSelection
{
    private const int MaxItems = 80;
    private static readonly HashSet<string> CommonWords = new(
        "about after again also because before being could discussion during from have into meeting more other should some that their them then there these they this those through what when where which while will with would".Split(' '),
        StringComparer.OrdinalIgnoreCase);

    internal static AnalysisRequest Create(long analyzedRevision, string brief, string memory,
        IReadOnlyList<Utterance> all, IReadOnlyList<MeetingMemory> memories, string[] dismissed,
        Cue? current, bool requested, int maxCharacters = 24_000)
    {
        var chosen = new HashSet<long>();
        int characters = 0;
        bool Include(Utterance item)
        {
            if (chosen.Contains(item.Id)) return true;
            if (chosen.Count >= MaxItems || characters + item.Text.Length > maxCharacters) return false;
            chosen.Add(item.Id);
            characters += item.Text.Length;
            return true;
        }

        // Always include current speech, even when catching up after a slow request or outage.
        foreach (var item in all.TakeLast(8).Reverse())
        {
            if (characters + item.Text.Length > 8_000 && chosen.Count > 0) break;
            Include(item);
        }

        // Advance only over a contiguous batch. An outage must never silently skip speech.
        int through = (int)analyzedRevision;
        while (through < all.Count && chosen.Count < 48)
        {
            var item = all[through];
            if (!chosen.Contains(item.Id) && characters + item.Text.Length > maxCharacters) break;
            if (!Include(item)) break;
            through++;
        }

        if (current is not null)
            foreach (var item in all.Where(u => current.EvidenceIds.Contains(u.Id))) Include(item);

        var query = Terms(string.Join(" ", all.TakeLast(12).Select(u => u.Text)));
        var newestTerms = Terms(all[^1].Text);
        int Relevance(string text)
        {
            var terms = Terms(text);
            return terms.Count(newestTerms.Contains) * 4 + terms.Count(query.Contains);
        }
        // Retrieve original evidence, including neighboring remarks, not just AI paraphrases.
        var earlier = all.Take((int)analyzedRevision).Select((item, index) => new
        {
            Item = item, Index = index, Score = Relevance(item.Text)
        }).Where(x => x.Score > 0 && !chosen.Contains(x.Item.Id))
          .OrderByDescending(x => x.Score).ThenByDescending(x => x.Index).Take(4);

        foreach (var item in all.TakeLast(12).Reverse()) Include(item);
        foreach (var match in earlier)
            for (int i = Math.Max(0, match.Index - 1); i <= Math.Min(through - 1, match.Index + 1); i++)
                Include(all[i]);
        foreach (var slide in all.Where(u => u.Kind == "slide").Reverse().Take(3)) Include(slide);
        if (requested)
            foreach (var item in all.Reverse()) Include(item);

        var prior = memories.Where(m => m.Text != memory)
            .Select(m => new { Memory = m, Score = Relevance(m.Text) })
            .Where(m => m.Score > 0).OrderByDescending(m => m.Score)
            .ThenByDescending(m => m.Memory.ThroughRevision).Take(2);
        string historical = "";
        foreach (var entry in prior)
        {
            string text = $"Earlier AI summary through item {entry.Memory.ThroughRevision} (may be superseded):\n{entry.Memory.Text}\n";
            if (historical.Length + text.Length <= 12_400) historical += text;
        }

        return new(through, brief, memory, all.Where(u => chosen.Contains(u.Id)).ToArray(), dismissed,
            current is not null && current.EvidenceIds.All(chosen.Contains) ? current : null, requested, historical);
    }

    private static HashSet<string> Terms(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{4,}")
            .Select(m => m.Value).Where(word => !CommonWords.Contains(word)).ToHashSet();
}
