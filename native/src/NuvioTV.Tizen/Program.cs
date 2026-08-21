using System;
using Tizen.NUI;

namespace NuvioTV.Tizen
{
    /// <summary>
    /// Device entry point. Phase 8.1 replaces OnCreate with the real boot
    /// sequence (BootGuard → theme/i18n → home); until then this shell proves
    /// packaging, install, launch and exit on hardware.
    /// </summary>
    internal sealed class Program : NUIApplication
    {
        private static void Main(string[] args)
        {
            var program = new Program();
            program.Run(args);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            GetDefaultWindow().KeyEvent += OnWindowKeyEvent;
            global::Tizen.Log.Info("NuvioTV", "native shell started");
        }

        private void OnWindowKeyEvent(object sender, Window.KeyEventArgs eventArgs)
        {
            if (eventArgs.Key.State == Key.StateType.Down &&
                eventArgs.Key.KeyPressedName == "Escape")
            {
                Exit();
            }
        }

        protected override void OnTerminate()
        {
            global::Tizen.Log.Info("NuvioTV", "native shell terminated");
            base.OnTerminate();
        }
    }
}
