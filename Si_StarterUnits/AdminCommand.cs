using System;
using SilicaAdminMod;

namespace Si_StarterUnits
{
    /// <summary>
    /// !starteraudit [layouts] — dry run of the starter placement for the current map, nothing is spawned.
    /// Only this class references the Admin Mod; it is called only when the "Admin Mod" melon is registered
    /// (StarterUnits.OnLateInitializeMelon), so the mod runs without it.
    /// </summary>
    internal static class AdminCommand
    {
        const string Tag = "<color=#9AD07A>[StarterUnits]</color> ";

        public static void Register()
        {
            HelperMethods.RegisterAdminCommand("starteraudit", OnCommand, Power.Generic,
                "Dry-run check of the starter unit positions on this map (no spawn). 'layouts' = every Si_MapBalance layout of the map");
            Melon_Log("!starteraudit registered");
        }

        // args is the whole chat line, the command itself first.
        static void OnCommand(Player? callerPlayer, string args)
        {
            string[] tokens = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool layouts = tokens.Length > 1 && tokens[1].StartsWith("layout", StringComparison.OrdinalIgnoreCase);
            Reply(callerPlayer, layouts ? "auditing every layout of this map..." : "auditing the current bases...");
            StarterUnits.StartAudit(layouts, line => Reply(callerPlayer, line));
        }

        static void Reply(Player? callerPlayer, string line)
        {
            try { if (callerPlayer != null) HelperMethods.SendChatMessageToPlayer(callerPlayer, Tag + line); } catch { }
        }

        static void Melon_Log(string s) => MelonLoader.MelonLogger.Msg("[STARTER] " + s);
    }
}
