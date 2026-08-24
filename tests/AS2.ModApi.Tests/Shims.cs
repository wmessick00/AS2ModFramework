using System;
using System.Collections.Generic;
using System.Reflection;

// ---- Stand-ins for what the game and its libraries provide -------------------------------------
//
// Everything below exists so that AS2Events.cs, Patches.cs and ModMenuRegistry.cs can be compiled
// into this project and exercised cold. What is under test in those files is locking, snapshotting
// and graceful degradation -- logic that needed no Unity to write and needs none to check.
//
// One limit is worth stating plainly, because it decides what a green run here does and does not
// mean. These shims copy the *shape* of the game's members, not the game's own assembly. If a
// community patch renames RingDesignManager.selectedSkinRelativePath, these checks keep passing and
// the real build of AS2.ModApi -- which compiles against the shipped Assembly-CSharp -- is what
// fails. That is the correct division: the compiler already guards the binding, so these checks are
// free to guard the parts it cannot.

namespace LuaInterface
{
    /// <summary>
    /// Stands in for LuaInterface's interpreter. AS2Events only ever passes one along, so an empty
    /// reference type is the whole of what the code under test needs from it.
    /// </summary>
    public sealed class Lua { }
}

/// <summary>
/// The game's skin selector. Only the static AS2Events.CurrentKey reads is reproduced, plus the
/// three members Patches looks up by name.
/// </summary>
internal static class RingDesignManager
{
    // A property rather than a field so the compiler can see it assigned. The real member is a
    // field; AS2Events reads it the same way either way.
    internal static string selectedSkinRelativePath { get; set; }

    private static void OnEnable() { }
    private static void OnDisable() { }
    private static void OnListItemSelected(int index) { }
}

/// <summary>The game's mode selector, in the same shape</summary>
internal static class ModeSelect
{
    internal static Mode selectedModeScript { get; set; }

    private static void OnEnable() { }
    private static void OnDisable() { }
    private static void OnListItemSelected(int index) { }
}

/// <summary>The game's mode script record. One member is read</summary>
internal sealed class Mode
{
    internal string relativePath { get; set; }
}

/// <summary>The game's settings dialog. Patches watches it open and close</summary>
internal static class Settings
{
    private static void OnEnable() { }
    private static void OnDisable() { }
}

/// <summary>
/// The game's Lua factory. Patches postfixes this, and the real one returns the sandboxed state.
/// </summary>
internal static class LuaSandbox
{
    internal static LuaInterface.Lua NewLua(string name) { return new LuaInterface.Lua(); }
}

/// <summary>
/// The game's editor. Its two path accessors are the fallback AS2Events.CurrentKey uses when the
/// selector statics are empty.
/// </summary>
internal static class CodeEditor
{
    internal static string skinPath { get; set; }
    internal static string modPath { get; set; }
}

namespace HarmonyLib
{
    /// <summary>Enough of Harmony for Patches.cs to compile and run its lookups</summary>
    // Does no patching, on purpose. These checks care about which members Patches finds, what it
    // does when it cannot find one, and that a failure costs one event rather than the run
    // A real patch needs the runtime code generation the game provides and this project does not
    public sealed class Harmony
    {
        /// <summary>Every patch this instance was asked to apply, in order</summary>
        internal readonly List<string> Applied = new List<string>();

        /// <summary>Set by a check to make the next Patch call throw, as a broken target would</summary>
        internal string ThrowOn;

        public Harmony(string id) { Id = id; }
        internal string Id { get; private set; }

        public MethodInfo Patch(MethodBase original, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            string name = original.DeclaringType.Name + "." + original.Name;

            if (ThrowOn != null && ThrowOn == name)
                throw new InvalidOperationException("Simulated patch failure on " + name);

            if (prefix != null)
                throw new InvalidOperationException(
                    "Patches passed a prefix for " + name + ", which the framework does not do.");

            Applied.Add(name);
            return original as MethodInfo;
        }
    }

    /// <summary>A patch method, as Harmony wraps one</summary>
    public sealed class HarmonyMethod
    {
        public HarmonyMethod(MethodInfo method) { Method = method; }
        internal MethodInfo Method { get; private set; }
    }

    /// <summary>
    /// Harmony's by-name lookups. Resolved against this test assembly, so the shims above stand in
    /// for the game's types and a name with no shim behind it is genuinely missing -- which is the
    /// degradation path worth checking.
    /// </summary>
    public static class AccessTools
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Static | BindingFlags.Instance
                                       | BindingFlags.DeclaredOnly;

        /// <summary>Names a check has hidden, to stand for a member a community patch renamed</summary>
        internal static readonly List<string> Hidden = new List<string>();

        public static Type TypeByName(string name)
        {
            if (Hidden.Contains(name)) return null;

            foreach (Type t in Assembly.GetExecutingAssembly().GetTypes())
                if (t.Name == name) return t;

            return null;
        }

        public static MethodInfo Method(Type type, string name)
        {
            if (type == null || Hidden.Contains(type.Name + "." + name)) return null;
            return type.GetMethod(name, All);
        }

        public static MethodInfo Method(Type type, string name, Type[] parameters)
        {
            if (type == null || Hidden.Contains(type.Name + "." + name)) return null;
            return parameters == null
                ? type.GetMethod(name, All)
                : type.GetMethod(name, All, null, parameters, null);
        }

        public static FieldInfo Field(Type type, string name)
        {
            if (type == null) return null;
            return type.GetField(name, All);
        }
    }
}

namespace BepInEx
{
    /// <summary>
    /// Stands in for BepInEx's own Paths, which the preloader fills in and which therefore has no
    /// value outside a running game. TargetResolver.GameRoot reads GameRootPath and nothing else
    /// from it, so faking that one property is the whole of what the production code needs.
    /// </summary>
    internal static class Paths
    {
        internal static string GameRootPath;
    }
}

namespace AS2.ModApi
{
    /// <summary>
    /// Stands in for the plugin type, purely so the code under test can reach a logger.
    ///
    /// The real ModApiPlugin derives from BepInEx's BaseUnityPlugin, so loading it would drag in
    /// UnityEngine; it is not in this project's Compile list for that reason. TargetResolver only
    /// ever calls Log.LogWarning, so that is all this provides.
    /// </summary>
    internal static class ModApiPlugin
    {
        internal static readonly TestLog Log = new TestLog();
    }

    /// <summary>
    /// Collects the warnings the code under test emits, so a test can assert that a rejection was
    /// reported rather than silently swallowed. Refusing a bad key without saying so would be a
    /// bug of its own here: the whole design leans on a specific log line naming what was refused.
    /// </summary>
    internal sealed class TestLog
    {
        internal readonly List<string> Warnings = new List<string>();

        internal void LogWarning(object message)
        {
            Warnings.Add(message == null ? "" : message.ToString());
        }

        /// <summary>
        /// Collected alongside the warnings rather than separately. Nothing asserts on the severity
        /// of a line, only on whether the code said something before it gave up, so keeping one list
        /// keeps <see cref="Mentions"/> meaning what it says.
        /// </summary>
        internal void LogError(object message)
        {
            Warnings.Add(message == null ? "" : message.ToString());
        }

        internal void LogInfo(object message)
        {
            Warnings.Add(message == null ? "" : message.ToString());
        }

        internal void Clear() { Warnings.Clear(); }

        /// <summary>Whether any warning so far contains the given fragment, ignoring case</summary>
        internal bool Mentions(string fragment)
        {
            foreach (string w in Warnings)
                if (w.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
