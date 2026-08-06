using System;

namespace AS2.ModApi
{
    /// <summary>
    /// Safe access to the game's input lock.
    ///
    /// The selector screens are EZGUI and read UnityEngine.Input directly, so a mod drawing an
    /// overlay has to call UIManager.LockInput() or clicks fall straight through to the list
    /// underneath. Consuming IMGUI events does not help.
    ///
    /// UIManager.LockInput()/UnlockInput() is itself a counter, so several mods holding the lock at
    /// once already compose correctly -- provided each one releases exactly the manager it locked.
    /// That proviso is the whole reason this type exists: the counter lives on the UIManager
    /// instance, so if the scene changes while a panel is open, the old manager (and its count) is
    /// gone, and unlocking the *new* one decrements a counter this mod never incremented. Every
    /// other mod's lock is then silently released.
    ///
    /// So the handle captures the exact manager it locked and releases that one or nothing:
    ///
    ///     _lock = AS2Input.Lock();     // panel opened
    ///     _lock.Dispose();             // panel closed; safe to call twice
    /// </summary>
    public static class AS2Input
    {
        /// <summary>
        /// Locks game input and returns a handle that releases it. Never returns null, never throws;
        /// if the UI manager is missing the handle is simply inert.
        /// </summary>
        public static IDisposable Lock()
        {
            try
            {
                if (!UIManager.Exists()) return new Handle(null);

                UIManager ui = UIManager.instance;
                if (ui == null) return new Handle(null);

                ui.LockInput();
                return new Handle(ui);
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Could not lock the game's UI input: " + e.Message);
                return new Handle(null);
            }
        }

        private sealed class Handle : IDisposable
        {
            private UIManager _locked;

            internal Handle(UIManager locked) { _locked = locked; }

            public void Dispose()
            {
                UIManager ui = _locked;
                _locked = null;
                if (ui == null) return;

                try { ui.UnlockInput(); }
                catch (Exception e) { ModApiPlugin.Log.LogWarning("Could not unlock the game's UI input: " + e.Message); }
            }
        }
    }
}
