using System;

namespace AS2.ModApi
{
    /// <summary>Safe access to the game's input lock</summary>
    // The selector screens are EZGUI and read UnityEngine.Input directly, so a mod drawing an
    // overlay has to call UIManager.LockInput() or clicks fall through to the list underneath
    // Consuming IMGUI events does not help
    // LockInput/UnlockInput is a counter, so several mods holding it at once already compose --
    // provided each one releases exactly the manager it locked
    // That proviso is why this type exists: the counter lives on the UIManager instance, so a scene
    // change while a panel is open leaves the old manager and its count gone, and unlocking the new
    // one decrements a counter this mod never incremented, releasing every other mod's lock
    // The handle captures the exact manager it locked and releases that one or nothing:
    //     _lock = AS2Input.Lock();     // Panel opened
    //     _lock.Dispose();             // Panel closed, safe to call twice
    public static class AS2Input
    {
        /// <summary>Locks game input and returns a handle that releases it</summary>
        // Never returns null, never throws. An inert handle when the UI manager is missing
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
