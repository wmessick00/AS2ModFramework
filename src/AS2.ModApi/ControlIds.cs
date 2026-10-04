using System.Runtime.CompilerServices;

namespace AS2.ModApi
{
    /// <summary>Which IMGUI control id a mod's slider or scrollbar gets, and why it differs from the next mod's</summary>
    // #86. AS2Ui.Slider and AS2Ui.Scrollbar each asked GUIUtility.GetControlID for an id from one
    // fixed hint, shared by every mod that ever draws one, and decided whether a drag was theirs by
    // asking whether GUIUtility.hotControl equals that id. hotControl is one static for the whole
    // process. The ids are only as different as the controls drawn before them, so two mods whose
    // sliders come out as the same-numbered control in their own OnGUI ended up with one id, and a
    // drag on mod A's slider moved mod B's value to wherever the mouse was over A's. Nothing said so
    // The hint is the one part of the id the caller of GetControlID chooses, so a different hint per
    // mod is a different id per mod, whatever Unity's counters do. The mod is the assembly that called
    // the AS2Ui method, which is what ScrollTurns already uses to tell mods apart
    // Kept free of UnityEngine so a cold check can hold it to that
    internal static class ControlIds
    {
        /// <summary>The hint to give GetControlID for a control of one kind, drawn by one mod</summary>
        // <paramref name="owner"/> is compared by identity, never by what it says about itself: two
        // mods are two assemblies even when they are called the same, and a plugin that reloads comes
        // back as a new one, which is harmless because an id only has to hold still while one drag
        // does
        // No owner is the hint on its own, which is what every control got before
        internal static int HintFor(int kind, object owner)
        {
            if (owner == null) return kind;

            return unchecked((kind * 397) ^ RuntimeHelpers.GetHashCode(owner));
        }
    }
}
