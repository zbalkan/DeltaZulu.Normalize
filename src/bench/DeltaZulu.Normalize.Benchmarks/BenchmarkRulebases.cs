using System.Text;

namespace DeltaZulu.Normalize.Benchmarks;

/// <summary>
/// Rulebase and message corpora for the benchmark scenarios. Everything is
/// generated in code so the benchmarks have no file dependencies and the
/// shape of each corpus (trie-heavy, backtrack-heavy, structured) is explicit.
/// </summary>
public static class BenchmarkRulebases
{
    /// <summary>
    /// Backtrack-heavy: rules share a greedy %word% %word% prefix and differ
    /// only in a literal tail, so the walker must re-try many sibling edges
    /// (and re-parse the words) before finding the winning path. Messages
    /// match the last tail to force maximal sibling exploration.
    /// </summary>
    public static string BacktrackHeavy(out string[] matchingMessages, out string[] nonMatchingMessages)
    {
        var sb = new StringBuilder();
        string[] tails = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa", "lambda", "omega"];
        foreach (var tail in tails)
        {
            sb.AppendLine($"rule=:%user:word% %host:word% {tail} %val:number%");
            sb.AppendLine($"rule=:%user:word% %host:word% {tail}-ext %val:number% %detail:char-to:,%, done");
        }
        /* a low-priority catch-all keeps NoMatch honest without matching everything */
        sb.AppendLine("rule=:BEGIN %head:word% %mid:char-to:,%, %tail:rest%");
        matchingMessages =
        [
            "alice web01 omega 42",
            "bob db02 omega-ext 7 some detail here, done",
            "carol app03 kappa 123456",
        ];
        nonMatchingMessages =
        [
            "alice web01 unknownword 42",
            "alice web01 omega notanumber",
            "no separators here at all",
        ];
        return sb.ToString();
    }

    /// <summary>Structured motifs: json, cef, name-value-list and repeat.</summary>
    public static string Structured(out string[] matchingMessages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("rule=:app: %payload:json%");
        sb.AppendLine("rule=:%cef:cef%");
        sb.AppendLine("rule=:kv: %fields:name-value-list%");
        sb.AppendLine("rule=:list: %{\"name\":\"nums\", \"type\":\"repeat\", \"parser\":{\"name\":\".\", \"type\":\"number\"}, \"while\":{\"type\":\"literal\", \"text\":\", \"}}%");
        matchingMessages =
        [
            """app: {"user":"alice","action":"login","meta":{"ip":"10.0.0.1","ok":true},"count":3}""",
            "CEF:0|Vendor|Product|1.0|100|Something happened|5|src=10.0.0.1 dst=10.0.0.2 msg=hello world spt=1234",
            "kv: user=alice host=web01 action=login result=success elapsed=42",
            "list: 1, 2, 3, 4, 5, 6, 7, 8, 9, 10",
        ];
        return sb.ToString();
    }

    /// <summary>
    /// Trie-heavy: many rules whose literal heads share prefixes, so the PDAG
    /// is dominated by literal edges. Exercises literal dispatch and the
    /// no-backtracking fast path.
    /// </summary>
    public static string TrieHeavy(int ruleCount, out string[] matchingMessages, out string[] nonMatchingMessages)
    {
        var sb = new StringBuilder();
        var match = new List<string>();
        string[] services = ["sshd", "systemd", "kernel", "nginx", "postfix", "cron", "sudo", "dhclient"];
        string[] actions = ["accepted", "rejected", "opened", "closed", "started"];
        for (var i = 0; i < ruleCount; i++)
        {
            var svc = services[i % services.Length];
            var act = actions[(i / services.Length) % actions.Length];
            var variant = i / (services.Length * actions.Length);
            sb.AppendLine($"rule=:{svc}[%pid:number%]: {act} v{variant} from %ip:ipv4% port %port:number%");
            if (i % 7 == 0)
            {
                match.Add($"{svc}[{1000 + i}]: {act} v{variant} from 192.168.{i % 256}.{(i * 3) % 256} port {2000 + i}");
            }
        }
        matchingMessages = match.ToArray();
        nonMatchingMessages =
        [
            "sshd[999]: unknown operation from 10.0.0.1 port 22",
            "ntpd[4]: accepted v0 from 10.0.0.1 port 123",
            "completely unrelated free-form text without structure",
        ];
        return sb.ToString();
    }
}
