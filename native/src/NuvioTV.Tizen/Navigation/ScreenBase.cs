using System.Threading.Tasks;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Input;
using Tizen.NUI.BaseComponents;
namespace NuvioTV.Tizen.Navigation
{
    /// <summary>
    /// Contract shared by every screen (router.js Screen interface + key hooks).
    /// </summary>
    public abstract class ScreenBase : View, IFocusable
    {
        /// <summary>Builds and attaches the screen content; called once per navigation.</summary>
        public abstract Task MountAsync(RouteParams p, NavigationContext ctx);

        /// <summary>Releases async work before the view leaves the host.</summary>
        public virtual void Cleanup() { }

        /// <summary>
        /// Back interception (consumeBackRequest): null → router handles;
        /// "history" → router pops one stack entry without calling this again;
        /// true → fully consumed.
        /// </summary>
        public virtual object ConsumeBackRequest() { return null; }

        public virtual bool OnKeyDown(NuvioKey k) { return false; }

        public virtual bool OnKeyUp(NuvioKey k) { return false; }

        /// <summary>Optional state snapshot saved when navigating away.</summary>
        public virtual object CaptureRouteState() { return null; }

        /// <summary>Optional state restore on re-mount; receives the saved snapshot.</summary>
        public virtual void RestoreRouteState(object state) { }

        // ---- IFocusable ----

        public virtual string FocusKey => GetType().Name;

        public void ApplyFocus(bool focused)
        {
            // Screens own their focus visuals via their own FocusController; the
            // screen-level default is intentionally visual-free.
        }
    }
}
