using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AS2.Bootstrap
{
    /// <summary>
    /// A deliberately primitive file logger for the window before BepInEx exists.
    ///
    /// It cannot use UnityEngine.Debug (Unity's internal calls are not registered at doorstop time)
    /// and it cannot use BepInEx's logger (that is what we are in the middle of starting). Once
    /// StartBepInEx has handed off, everything else in the stack should log through BepInEx's
    /// ManualLogSource instead -- this file only ever covers the handoff.
    ///
    /// Never throws. A logger that can take the game down is worse than no logger.
    /// </summary>
    internal static class BootLog
    {
        private static string _path;

        internal static void Init(string directory)
        {
            if (Bootstrap.IsBlank(directory)) return;

            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "bootstrap.log");

            // Truncated every launch: this log covers one handoff and nothing else, so history is noise.
            File.WriteAllText(_path, "", Encoding.UTF8);
        }

        internal static void Info(string message) { Write("INFO ", message, null); }
        internal static void Warn(string message) { Write("WARN ", message, null); }
        internal static void Error(string message, Exception e) { Write("ERROR", message, e); }

        private static void Write(string level, string message, Exception e)
        {
            if (_path == null) return;

            try
            {
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                sb.Append(' ').Append(level).Append(' ').Append(message);
                if (e != null) sb.Append(Environment.NewLine).Append(e);
                sb.Append(Environment.NewLine);

                File.AppendAllText(_path, sb.ToString(), Encoding.UTF8);
            }
            catch { /* best-effort by design */ }
        }
    }
}
