using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;

namespace AS2.Probe
{
    /// <summary>
    /// Development probe. See AS2.Probe.csproj for what it is for; it is not shipped.
    /// </summary>
    [BepInPlugin(Id, "AS2 Probe", "0.1.0")]
    public sealed class ProbePlugin : BaseUnityPlugin
    {
        internal const string Id = "as2.probe";

        /// <summary>Types AS2.ModApi will need to hook. Dumped so the hooks can be written against fact.</summary>
        private static readonly string[] TypesOfInterest =
        {
            "LuaSandbox",
            "LuaController",
            "LuaMods",
            "RingDesignManager",
            "ModeSelect",
            "SkinSelectorLoader",
            "ModeSelectorLoader",
            "CodeEditor",
            "UIManager",
        };

        internal static ProbePlugin Instance;

        private void Awake()
        {
            Instance = this;

            // If this line reaches LogOutput.log, question 1 is answered: BepInEx started under the
            // community patch's Doorstop 3.4.1 without its own Doorstop 4 proxy.
            Logger.LogInfo("AS2.Probe loaded. BepInEx is running under the community patch's doorstop.");

            try { DumpMembers(); }
            catch (Exception e) { Logger.LogError("Member dump failed: " + e); }

            try { ApplyProbePatch(); }
            catch (Exception e) { Logger.LogError("Probe patch failed: " + e); }
        }

        /// <summary>
        /// Postfixes LuaSandbox.NewLua, the factory every Lua state in the game is born from.
        /// A line in the log when a song starts proves a Harmony detour fires on legacy Mono, and
        /// tells us the 'kind' string each caller passes, which is what AS2.ModApi will key on.
        /// </summary>
        private void ApplyProbePatch()
        {
            Type sandbox = AccessTools.TypeByName("LuaSandbox");
            if (sandbox == null) { Logger.LogWarning("No LuaSandbox type found; skipping probe patch."); return; }

            MethodInfo target = AccessTools.Method(sandbox, "NewLua");
            if (target == null) { Logger.LogWarning("No LuaSandbox.NewLua found; skipping probe patch."); return; }

            var postfix = new HarmonyMethod(AccessTools.Method(typeof(ProbePlugin), nameof(NewLuaPostfix)));
            new Harmony(Id).Patch(target, null, postfix);

            Logger.LogInfo("Patched " + Describe(target) + " -- waiting for a song to start.");
        }

        /// <summary>
        /// Indexed injection (__0) rather than __args: __args needs a newer HarmonyX than BepInEx 5
        /// necessarily ships, and __result declared as object can trip Harmony's assignability check.
        /// luacontroller.cs:50 (`LuaSandbox.NewLua("Skin")`) already establishes the first parameter
        /// is a string, so this binds against a known signature.
        /// </summary>
        private static void NewLuaPostfix(string __0)
        {
            Instance.Logger.LogInfo("HARMONY HIT: LuaSandbox.NewLua(kind=\"" + (__0 ?? "<null>") + "\")");
        }

        /// <summary>
        /// Writes every declared member of the types AS2.ModApi cares about to a file. Reading real
        /// signatures beats guessing at Awake/Start/OnEnable and then debugging a silent no-op patch.
        /// </summary>
        private void DumpMembers()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            var sb = new StringBuilder();
            sb.AppendLine("AS2.Probe member dump");
            sb.AppendLine("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            foreach (string name in TypesOfInterest)
            {
                Type t = AccessTools.TypeByName(name);
                if (t == null) { sb.AppendLine("### " + name + "  <NOT FOUND>").AppendLine(); continue; }

                sb.AppendLine("### " + t.FullName + "   (assembly: " + t.Assembly.GetName().Name + ")");

                var fields = new List<string>();
                foreach (FieldInfo f in t.GetFields(All))
                    fields.Add("    " + (f.IsStatic ? "static " : "") + (f.IsPublic ? "public " : "private ")
                               + Pretty(f.FieldType) + " " + f.Name);
                fields.Sort();
                sb.AppendLine("  -- fields --");
                foreach (string s in fields) sb.AppendLine(s);

                var methods = new List<string>();
                foreach (MethodInfo m in t.GetMethods(All))
                    methods.Add("    " + (m.IsStatic ? "static " : "") + (m.IsPublic ? "public " : "private ") + Describe(m));
                methods.Sort();
                sb.AppendLine("  -- methods --");
                foreach (string s in methods) sb.AppendLine(s);

                sb.AppendLine();
            }

            // Create the folder first. The probe can run on an install where AS2ModLoader is not
            // there yet, and File.WriteAllText throws DirectoryNotFoundException on a missing
            // folder -- which Awake() catches, so the one file this tool exists to write is lost.
            string dir = Path.Combine(Paths.GameRootPath, "AS2ModLoader");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, "probe-dump.txt");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Logger.LogInfo("Member dump written to " + path);
        }

        private static string Describe(MethodInfo m)
        {
            var ps = new List<string>();
            foreach (ParameterInfo p in m.GetParameters()) ps.Add(Pretty(p.ParameterType) + " " + p.Name);
            return Pretty(m.ReturnType) + " " + m.Name + "(" + string.Join(", ", ps.ToArray()) + ")";
        }

        private static string Pretty(Type t)
        {
            if (t == typeof(void)) return "void";
            if (t == typeof(string)) return "string";
            if (t == typeof(int)) return "int";
            if (t == typeof(float)) return "float";
            if (t == typeof(bool)) return "bool";
            return t.Name;
        }
    }
}
