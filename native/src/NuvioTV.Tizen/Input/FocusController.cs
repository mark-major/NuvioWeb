using System;
using System.Collections.Generic;
using NuvioTV.Core.Input;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Input
{
    /// <summary>Implemented by widgets that can receive focus.</summary>
    public interface IFocusable
    {
        /// <summary>Applies or removes the focused visual state (.focused parity).</summary>
        void ApplyFocus(bool focused);

        /// <summary>Stable identity for state restore; may be null.</summary>
        string FocusKey { get; }
    }

    /// <summary>
    /// Port of js/ui/navigation/screen.js focus management: tracks the focused
    /// child inside a container, applies focus visuals, and moves focus with
    /// index steps (±1) or spatial direction via SpatialFocusSolver.
    /// </summary>
    public sealed class FocusController
    {
        private readonly List<FocusableRect> _scratch = new List<FocusableRect>();
        private View _container;
        private IFocusable _focused;

        /// <summary>Currently focused widget inside the active container, if any.</summary>
        public IFocusable Focused => _focused;

        /// <summary>Binds the controller to a container and clears focus state.</summary>
        public void SetContainer(View container)
        {
            _container = container;
            _focused = null;
        }

        /// <summary>Focuses the first focusable descendant (setInitialFocus parity).</summary>
        public bool SetInitialFocus()
        {
            var first = FindFocusables(_container);
            if (first.Count == 0)
            {
                return false;
            }
            ApplyFocused(first[0]);
            return true;
        }

        /// <summary>Restores focus to a specific widget if it is in the container.</summary>
        public bool RestoreFocus(IFocusable target)
        {
            if (_container == null || target == null) return false;
            var list = FindFocusables(_container);
            foreach (var item in list)
            {
                if (ReferenceEquals(item, target))
                {
                    ApplyFocused(item);
                    return true;
                }
            }
            return false;
        }

        /// <summary>moveFocus(container, ±1): index-step through registration order.</summary>
        public bool MoveFocus(int delta)
        {
            var list = FindFocusables(_container);
            if (list.Count == 0) return false;

            var currentIndex = list.IndexOf(_focused);
            if (currentIndex < 0) return SetInitialFocus();

            var next = currentIndex + delta;
            if (next < 0 || next >= list.Count)
            {
                return false; // boundary: no wrap in webapp moveFocus
            }
            ApplyFocused(list[next]);
            return true;
        }

        /// <summary>moveFocusDirectional: spatial selection via the solver.</summary>
        public bool MoveFocusDirectional(FocusDirection direction)
        {
            var list = FindFocusables(_container);
            if (list.Count == 0) return false;
            if (_focused == null || !list.Contains(_focused)) return SetInitialFocus();

            _scratch.Clear();
            foreach (var item in list)
            {
                if (ReferenceEquals(item, _focused)) continue;
                var view = item as View;
                if (view == null) continue;

                // Window-space rects match the DOM getBoundingClientRect model.
                var world = view.ScreenPosition;
                _scratch.Add(new FocusableRect
                {
                    Id = item.FocusKey ?? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item).ToString(),
                    X = world.X,
                    Y = world.Y,
                    Width = view.SizeWidth,
                    Height = view.SizeHeight
                });
            }

            var currentView = (View)_focused;
            var currentWorld = currentView.ScreenPosition;
            var current = new FocusableRect
            {
                Id = _focused.FocusKey ?? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(currentView).ToString(),
                X = currentWorld.X,
                Y = currentWorld.Y,
                Width = currentView.SizeWidth,
                Height = currentView.SizeHeight
            };

            var best = SpatialFocusSolver.FindBest(current, direction, _scratch);
            if (!best.HasValue) return false;

            var bestId = best.Value.Id;
            foreach (var item in list)
            {
                var id = item.FocusKey ?? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item).ToString();
                if (id == bestId)
                {
                    ApplyFocused(item);
                    return true;
                }
            }
            return false;
        }

        private void ApplyFocused(IFocusable target)
        {
            if (ReferenceEquals(_focused, target)) return;
            _focused?.ApplyFocus(false);
            _focused = target;
            _focused.ApplyFocus(true);
        }

        /// <summary>Collects visible IFocusable views in traversal order.</summary>
        internal static List<IFocusable> FindFocusables(View root)
        {
            var result = new List<IFocusable>();
            Collect(root, result);
            return result;
        }

        private static void Collect(View node, List<IFocusable> result)
        {
            if (node == null) return;
            if (node is IFocusable f && node.Visibility == true &&
                node.SizeWidth > 0 && node.SizeHeight > 0)
            {
                result.Add(f);
            }
            var childCount = node.ChildCount;
            for (uint i = 0; i < childCount; i++)
            {
                Collect(node.GetChildAt(i), result);
            }
        }
    }
}
