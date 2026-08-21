using System;
using System.Globalization;
using System.Threading.Tasks;

using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using NuvioTV.Core.Localization;
namespace NuvioTV.Tizen.NuiFoundation
{
    /// <summary>
    /// Port of boot-guard.js: full-screen boot overlay with staged progress
    /// labels (native_boot_stage_*), a fatal-error surface shown when startup
    /// fails before the app shell renders, and unhandled-exception hooks.
    /// </summary>
    public sealed class BootGuard : IDisposable
    {
        private readonly View _root;
        private readonly TextLabel _stageLabel;
        private bool _fatalShown;
        private bool _dismissed;

        public BootGuard(Window window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));

            _root = new View
            {
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent,
                BackgroundColor = new Color(0.051f, 0.067f, 0.082f, 1f) // #0d1117, matches webapp boot overlay
            };

            var title = new TextLabel
            {
                Text = "Nuvio TV",
                PointSize = 64f,
                TextColor = Color.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Position = new Position(0, -80),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            _root.Add(title);

            _stageLabel = new TextLabel
            {
                Text = StageKey("shell"),
                PointSize = DesignTokens.TypeBody,
                TextColor = new Color(0.78f, 0.816f, 0.867f, 1f), // #c7d0dd
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Position = new Position(0, 60),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            _root.Add(_stageLabel);

            window.GetDefaultLayer().Add(_root);
        }

        public void Stage(string stage)
        {
            if (_fatalShown || _dismissed || _stageLabel == null)
            {
                return;
            }
            _stageLabel.Text = I18n.T("native_boot_stage_" + stage, fallback: stage);
        }

        /// <summary>Fatal overlay (boot-guard.js showError): message + detail + code, stays up until exit.</summary>
        public void Fail(string code, string message, string details)
        {
            if (_fatalShown || _dismissed) return;
            _fatalShown = true;

            var layer = NUIApplication.GetDefaultWindow().GetDefaultLayer();
            var panel = new View
            {
                Size = new Size(960, 540),
                Position = new Position((1920 - 960) / 2f, (1080 - 540) / 2f),
                BackgroundColor = new Color(0.09f, 0.106f, 0.133f, 1f), // #171b22
                CornerRadius = 12f
            };

            var heading = new TextLabel
            {
                Text = message ?? "Startup failed",
                PointSize = 42f,
                TextColor = Color.White,
                Position = new Position(40, 40),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize,
                MultiLine = true
            };
            panel.Add(heading);

            var detailText = string.IsNullOrEmpty(details) ? "" : details;
            if (detailText.Length > 1200)
            {
                detailText = detailText.Substring(0, 1200);
            }
            var detail = new TextLabel
            {
                Text = "[" + (code ?? "BOOT") + "] " + detailText,
                PointSize = 18f,
                TextColor = new Color(0.78f, 0.816f, 0.867f, 1f),
                Position = new Position(40, 140),
                Size = new Size(880, 360),
                MultiLine = true
            };
            panel.Add(detail);

            layer.Add(panel);
            global::Tizen.Log.Error("NuvioTV", $"BOOT FATAL [{code}] {message}: {details}");
        }

        public bool IsActive => !_dismissed && !_fatalShown;

        /// <summary>Boot finished: remove the overlay from the layer.</summary>
        public void Dismiss()
        {
            if (_dismissed) return;
            _dismissed = true;
            var parent = _root?.GetParent();
            parent?.Remove(_root);
        }

        /// <summary>Wires AppDomain/TaskScheduler handlers that route to Fail while the guard is active.</summary>
        public static IDisposable InstallGlobalHandlers(Func<string> describeLastStage, Action<string, string, string> fail)
        {
            UnhandledExceptionEventHandler domainHandler = (_, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                fail("APPDOMAIN", ex?.Message ?? "Unhandled exception", ex?.ToString());
            };
            EventHandler<UnobservedTaskExceptionEventArgs> taskHandler = (_, e) =>
            {
                fail("UNOBSERVED-TASK", e.Exception?.Message ?? "Unobserved task exception", e.Exception?.ToString());
            };
            AppDomain.CurrentDomain.UnhandledException += domainHandler;
            TaskScheduler.UnobservedTaskException += taskHandler;
            return new DisposableAction(() =>
            {
                AppDomain.CurrentDomain.UnhandledException -= domainHandler;
                TaskScheduler.UnobservedTaskException -= taskHandler;
            });
        }

        private static string StageKey(string stage) => I18n.T("native_boot_stage_" + stage, fallback: stage);

        public void Dispose()
        {
            Dismiss();
        }

        private sealed class DisposableAction : IDisposable
        {
            private Action _dispose;
            public DisposableAction(Action dispose) { _dispose = dispose; }
            public void Dispose()
            {
                var d = _dispose;
                _dispose = null;
                d?.Invoke();
            }
        }
    }
}
