using System;
using System.Reflection;
using MelonLoader;

namespace Si_StarterUnits
{
    /// <summary>
    /// Si_VersusVote by reflection (no compile-time reference, tolerated missing): type Si_VersusVote.VersusApi with
    /// ApiVersion >= 2, public static bool IsLoneTeam(Team) and public static int LoneStarterUnitsMult { get; }.
    /// Both are false / 1 unless a 2v1 round is active (VersusApi.TwoVsOneActive, set when the layout is decided), so
    /// the multiplier is read from the flag and the lone team, not from the later alliance confirmation.
    /// The lookup runs once per spawn pass (once per round) until it succeeds; a successful link is cached.
    /// </summary>
    internal static class VersusVoteBridge
    {
        const string TypeName = "Si_VersusVote.VersusApi";
        const int MinApi = 2;
        internal const int MaxMult = 10;

        static MethodInfo? _isLone;
        static PropertyInfo? _mult, _loneName;
        static bool _linked, _tooOldWarned;

        static bool Resolve()
        {
            if (_linked) return true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? t;
                    try { t = asm.GetType(TypeName, false); } catch { continue; }
                    if (t == null) continue;
                    int api = ReadApiVersion(t);
                    if (api < MinApi)
                    {
                        if (!_tooOldWarned)
                        {
                            _tooOldWarned = true;
                            MelonLogger.Warning("[StarterUnits] " + TypeName + " ApiVersion " + (api < 0 ? "?" : api.ToString()) + " in " + asm.GetName().Name +
                                                " (need >= " + MinApi + ", Si_VersusVote 0.3.0+): no 2v1 lone-team multiplier");
                        }
                        return false;
                    }
                    var isLone = t.GetMethod("IsLoneTeam", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Team) }, null);
                    var mult = t.GetProperty("LoneStarterUnitsMult", BindingFlags.Public | BindingFlags.Static);
                    if (isLone == null || isLone.ReturnType != typeof(bool) || mult == null || mult.PropertyType != typeof(int))
                    {
                        if (!_tooOldWarned)
                        {
                            _tooOldWarned = true;
                            MelonLogger.Warning("[StarterUnits] " + TypeName + " v" + api + " without IsLoneTeam(Team) / int LoneStarterUnitsMult: no 2v1 lone-team multiplier");
                        }
                        return false;
                    }
                    _isLone = isLone;
                    _mult = mult;
                    _loneName = t.GetProperty("LoneTeam", BindingFlags.Public | BindingFlags.Static);
                    _linked = true;
                    MelonLogger.Msg("[StarterUnits] Si_VersusVote linked: " + asm.GetName().Name + " " + asm.GetName().Version + ", VersusApi v" + api);
                    return true;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[StarterUnits] Si_VersusVote lookup failed: " + ex.GetBaseException().Message); }
            return false;
        }

        static int ReadApiVersion(Type t)
        {
            try
            {
                var f = t.GetField("ApiVersion", BindingFlags.Public | BindingFlags.Static);
                if (f != null) return Convert.ToInt32(f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(null));
                var p = t.GetProperty("ApiVersion", BindingFlags.Public | BindingFlags.Static);
                if (p != null) return Convert.ToInt32(p.GetValue(null));
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// The integer starter-unit multiplier for <paramref name="team"/>: Si_VersusVote's LoneStarterUnitsMult when the
        /// team is the 2v1 lone team, else 1 (also when Si_VersusVote is missing, too old, or throws). Clamped to 1..10.
        /// </summary>
        internal static int MultFor(Team team, out string loneName)
        {
            loneName = "";
            if (team == null || !Resolve()) return 1;
            try
            {
                if (!(bool)_isLone!.Invoke(null, new object[] { team })) return 1;
                int m = (int)_mult!.GetValue(null);
                try { loneName = _loneName?.GetValue(null) as string ?? ""; } catch { }
                if (loneName.Length == 0) { try { loneName = team.TeamShortName; } catch { } }
                if (m < 1) m = 1;
                if (m > MaxMult) m = MaxMult;
                return m;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[StarterUnits] Si_VersusVote VersusApi call failed: " + ex.GetBaseException().Message);
                return 1;
            }
        }
    }
}
