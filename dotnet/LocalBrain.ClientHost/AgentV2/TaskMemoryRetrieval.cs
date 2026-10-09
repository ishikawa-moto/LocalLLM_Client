using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalBrain.ClientHost.AgentV2;

// Derived ranking only. All IDs, source authority and evidence still come from the canonical Host.
internal static class TaskMemoryRetrieval
{
    internal sealed record Result(TaskMemorySelection.Candidate[] Candidates,long SourceFrontier,int ScannedEvents);
    private sealed record Source(ulong Matches,string Excerpt);
    internal const string Algorithm="canonical-fulltext-excerpt-v2";
    public static string Normalize(string query) {
        if(query.Length>8192)throw new InvalidDataException("Retrieval query exceeds Host bound");
        return Regex.Replace(query.Normalize(NormalizationForm.FormKC).ToLowerInvariant(),@"\s+"," ").Trim();
    }
    public static TaskMemorySelection.Candidate[] Select(string query,IReadOnlyList<TaskMemorySelection.Candidate> candidates,int limit=16,IReadOnlyDictionary<string,string>? evidence=null) {
        if(limit is <1 or >32 || candidates.Count>4096)throw new InvalidDataException("Retrieval limits invalid");
        var terms=Regex.Matches(Normalize(query),@"[\p{L}\p{N}_]+")
            .Select(m=>m.Value).Where(s=>s.Length>=2).Distinct(StringComparer.Ordinal).Take(64).ToArray();
        Source Scan(string text) {
            var normalized=text.Normalize(NormalizationForm.FormKC);ulong matches=0;var first=-1;
            for(var i=0;i<terms.Length;i++) {
                var pos=normalized.IndexOf(terms[i],StringComparison.OrdinalIgnoreCase);
                if(pos>=0){matches|=1UL<<i;if(first<0 || pos<first)first=pos;}
            }
            var start=first<0?0:Math.Max(0,first-80);
            if(start>0 && char.IsLowSurrogate(normalized[start]))start--;
            var count=Math.Min(460,normalized.Length-start);
            if(count>0 && char.IsHighSurrogate(normalized[start+count-1]))count--;
            return new(matches,"Untrusted immutable source excerpt:\n"+normalized.Substring(start,count));
        }
        // Each unique verified blob is searched once. Shared references reuse its mask/excerpt.
        var sources=evidence?.ToDictionary(e=>e.Key,e=>Scan(e.Value),StringComparer.Ordinal);
        var metadata=candidates.ToDictionary(c=>c.EventId,c=>Scan(c.EventType+" "+c.Preview),StringComparer.Ordinal);
        int Priority(TaskMemorySelection.Candidate c)=>c.EventType switch {
            "validation_result"=>4,"side_effect_applied"=>3,"human_approval_bound"=>2,"host_invocation"=>1,_=>0
        };
        int Matches(TaskMemorySelection.Candidate c) {
            var matches=metadata[c.EventId].Matches;
            if(sources is not null)foreach(var hash in c.EvidenceRefs)matches|=sources[hash].Matches;
            return BitOperations.PopCount(matches);
        }
        string Preview(TaskMemorySelection.Candidate c) {
            var best=metadata[c.EventId];
            if(sources is not null)foreach(var hash in c.EvidenceRefs) {
                var source=sources[hash];if(BitOperations.PopCount(source.Matches)>BitOperations.PopCount(best.Matches))best=source;
            }
            return best.Excerpt;
        }
        // Rank verified full source text; only the model excerpt is bounded. Neither grants truth.
        return candidates.OrderByDescending(Matches).ThenByDescending(Priority)
            .ThenByDescending(c=>c.Sequence).ThenBy(c=>c.EventId,StringComparer.Ordinal).Take(limit)
            .OrderBy(c=>c.Sequence).ThenBy(c=>c.EventId,StringComparer.Ordinal)
            .Select(c=>c with {Preview=Preview(c)}).ToArray();
    }
}