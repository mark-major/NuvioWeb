using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NuvioTV.Tizen.Screens;

namespace NuvioTV.Tizen.Navigation
{
    /// <summary>
    /// Pure-stack port of js/ui/navigation/router.js: NON_BACKSTACK routes never
    /// push history, Home + Back exits the app, and route state is captured and
    /// restored per navigation. The History-API layer of the webapp has no
    /// native equivalent — this stack is authoritative.
    /// </summary>
    public sealed class Router
    {
        private static readonly HashSet<Route> NonBackstackRoutes = new HashSet<Route>
        {
            Route.ProfileSelection,
            Route.AuthQrSignIn,
            Route.AuthSignIn,
            Route.SyncCode,
            Route.ExperienceModeSelection,
            Route.EssentialAddonSetup
        };

        private readonly ScreenHost _host;
        private readonly Func<Route, ScreenBase> _screenFactory;
        private readonly Action _exitApp;
        private readonly Stack<StackEntry> _stack = new Stack<StackEntry>();
        private readonly RouteStateStore _stateStore = new RouteStateStore();

        public Router(ScreenHost host, Func<Route, ScreenBase> screenFactory, Action exitApp)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _screenFactory = screenFactory ?? throw new ArgumentNullException(nameof(screenFactory));
            _exitApp = exitApp;
        }

        public Route Current { get; private set; }
        public bool HasCurrent { get; private set; }
        public RouteParams CurrentParams { get; private set; } = new RouteParams();
        public ScreenBase CurrentScreen => _host.CurrentScreen;
        public int StackDepth => _stack.Count;
        public RouteStateStore StateStore => _stateStore;

        public async Task NavigateAsync(Route route, RouteParams p = null, NavigateOptions o = null)
        {
            var options = o ?? new NavigateOptions();
            var targetParams = p ?? new RouteParams();

            var hadCurrent = HasCurrent;
            var previousRoute = Current;
            // NON_BACKSTACK previous routes never leave a stack entry behind.
            var shouldSkipPush = options.SkipStackPush ||
                (hadCurrent && NonBackstackRoutes.Contains(previousRoute));

            if (hadCurrent)
            {
                CaptureCurrentRouteState();
                _host.CurrentScreen?.Cleanup();
                if (!shouldSkipPush && previousRoute != route)
                {
                    _stack.Push(new StackEntry(previousRoute, CurrentParams));
                }
            }

            Current = route;
            HasCurrent = true;
            CurrentParams = targetParams;

            var context = new NavigationContext(route, options.IsBackNavigation, _stateStore);
            await _host.MountAsync(_screenFactory(route), targetParams, context);
        }

        public async Task BackAsync(BackOptions o = null)
        {
            var options = o ?? new BackOptions();

            // 1) Screen-level consumption. true → consumed; "history" → pop one
            //    stack entry without re-consulting the screen; null/false → router.
            if (!options.SkipConsume && _host.CurrentScreen != null)
            {
                var consumeResult = _host.CurrentScreen.ConsumeBackRequest();
                if (consumeResult is bool consumed && consumed)
                {
                    return;
                }
                if ("history".Equals(consumeResult as string, StringComparison.Ordinal))
                {
                    await PopOneAsync();
                    return;
                }
            }

            // 2) Home + Back exits.
            if (HasCurrent && Current == Route.Home)
            {
                _exitApp?.Invoke();
                return;
            }

            // 3) Pop one stack entry.
            if (_stack.Count > 0)
            {
                await PopOneAsync();
                return;
            }

            // 4) Empty stack: fall back to Home (router.js back() fallback).
            await NavigateAsync(Route.Home, new RouteParams(), new NavigateOptions
            {
                SkipStackPush = true,
                IsBackNavigation = true
            });
        }

        private async Task PopOneAsync()
        {
            var previous = _stack.Pop();
            CaptureCurrentRouteState();
            _host.CurrentScreen?.Cleanup();

            Current = previous.Route;
            HasCurrent = true;
            CurrentParams = previous.Params ?? new RouteParams();
            var context = new NavigationContext(previous.Route, true, _stateStore);
            await _host.MountAsync(_screenFactory(previous.Route), CurrentParams, context);
        }

        private void CaptureCurrentRouteState()
        {
            var screen = _host.CurrentScreen;
            if (screen == null || !HasCurrent) return;
            var snapshot = screen.CaptureRouteState();
            if (snapshot != null)
            {
                _stateStore.Set(RouteStateKey(Current), snapshot);
            }
        }

        /// <summary>Default state key: route name (families may share via prefixes).</summary>
        public string RouteStateKey(Route route) =>
            "route:" + route.ToString().ToLowerInvariant();

        private struct StackEntry
        {
            public readonly Route Route;
            public readonly RouteParams Params;

            public StackEntry(Route route, RouteParams p)
            {
                Route = route;
                Params = p;
            }
        }
    }
}
