// =============================================================================
// MCPTerminal AccessKeys - the authentication layer for shared terminals.
//
// A terminal belongs to a TAB (a conversation group). Every tab has one random
// access key, and every session in that tab stores a copy of it in state.json.
// No session can be read or driven without presenting its key, so one chat can
// never see or touch another chat's terminals. A caller with no valid key can
// still create terminals - they simply land in a brand new tab of their own.
//
// The key is printed in the terminal's own banner and by `info`, so the user
// can hand it to whichever assistant they want to let in.
// =============================================================================
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace MCPTerminal;

public static class AccessKeys
{
    public const string LocalTab = "Local";

    public static string TabsFile(string root) => Path.Combine(root, "tabs.json");

    // The user's own tab, matched case-insensitively so "local" cannot be used
    // to slip past the check and create a second, assistant-reachable "Local".
    public static bool IsLocal(string label) =>
        string.IsNullOrWhiteSpace(label) || label.Trim().Equals(LocalTab, StringComparison.OrdinalIgnoreCase);

    public static string NewKey()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(6);
        return "mt_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    static JsonObject Load(string root)
    {
        try
        {
            string p = TabsFile(root);
            if (File.Exists(p)) return JsonNode.Parse(File.ReadAllText(p)) as JsonObject ?? new JsonObject();
        }
        catch { }
        return new JsonObject();
    }

    static void Save(string root, JsonObject tabs)
    {
        // tabs.json is written from several processes; retry briefly on contention.
        for (int i = 0; i < 5; i++)
        {
            try
            {
                File.WriteAllText(TabsFile(root), tabs.ToJsonString(), new UTF8Encoding(false));
                return;
            }
            catch { System.Threading.Thread.Sleep(60 * (i + 1)); }
        }
    }

    // Resolve which tab a new session belongs to.
    //
    // trusted = the request came from the user at the keyboard (Studio's own UI
    // or a hand-launched window), so it may join the named tab without a key.
    // An untrusted caller (CLI / MCP client) must present the tab's key; if it
    // cannot, it gets a NEW tab instead of access to the existing one.
    public static (string Label, string Key) ClaimTab(string root, string controller,
        string suppliedKey, bool trusted)
    {
        string label = string.IsNullOrWhiteSpace(controller) ? LocalTab : controller.Trim();

        // A key is a credential for one specific TAB, so if it opens a tab the
        // caller joins THAT tab whatever it calls itself. This is what lets a
        // second conversation share a tab the user invited it into: it holds the
        // key but has its own name, and its terminals land beside the first
        // chat's instead of branching off into a tab of their own.
        if (!string.IsNullOrEmpty(suppliedKey))
        {
            foreach (var (name, node) in Load(root).Where(p => !IsLocal(p.Key))
                         .Select(p => (p.Key, p.Value)))
                if (node is JsonObject o && KeysEqual(o["key"]?.GetValue<string>() ?? "", suppliedKey))
                    return (name, suppliedKey);
        }

        // LOCAL IS YOURS ALONE. It never gets a key, so there is nothing to hand
        // out and nothing an assistant can present - the CLI refuses every
        // operation on a Local session. An untrusted caller that asks for Local
        // (or names no tab at all) is given its own tab instead of being let in.
        if (IsLocal(label))
        {
            if (trusted)
            {
                // Installs predating the unclaimed-Local model may still carry a
                // key for Local. Nothing honours it any more, so drop it rather
                // than leave a key lying around implying Local can be claimed.
                var t = Load(root);
                bool pruned = false;
                foreach (var legacy in t.Select(p => p.Key).Where(IsLocal).ToList())
                { t.Remove(legacy); pruned = true; }
                if (pruned) Save(root, t);
                return (LocalTab, "");
            }
            var own = Load(root);
            string mine;
            int n = 1;
            do { mine = $"chat-{n++}"; } while (own[mine] != null);
            string k = NewKey();
            own[mine] = new JsonObject { ["key"] = k, ["createdAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            Save(root, own);
            return (mine, k);
        }

        var tabs = Load(root);

        if (tabs[label] is JsonObject existing)
        {
            string have = existing["key"]?.GetValue<string>() ?? "";
            if (trusted || (!string.IsNullOrEmpty(suppliedKey) && KeysEqual(have, suppliedKey)))
                return (label, have);

            // Occupied by another conversation and no valid key - branch off.
            int n = 2;
            while (tabs[$"{label} #{n}"] != null) n++;
            label = $"{label} #{n}";
        }

        string key = NewKey();
        tabs[label] = new JsonObject { ["key"] = key, ["createdAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
        Save(root, tabs);
        return (label, key);
    }

    // Constant-time-ish comparison; keys are short but there is no reason to
    // leak position information through an early exit.
    public static bool KeysEqual(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
