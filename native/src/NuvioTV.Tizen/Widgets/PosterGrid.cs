using System;
using NuvioTV.Tizen.Input;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Tizen.NUI.Components;

namespace NuvioTV.Tizen.Widgets
{
    /// <summary>
    /// Poster grid (library/search/discover parity): FlexibleView +
    /// GridLayoutManager. Virtualization budgets from homeConstants.js: the
    /// adapter exposes at most 300 items per snapshot with 4-item load-ahead
    /// notifications via <see cref="NearEnd"/>.
    /// </summary>
    public sealed class PosterGrid : View, IFocusable
    {
        public const int MaxItemsPerSnapshot = 300;
        public const int LoadAhead = 4;

        private readonly GridLayoutManager _layout;
        private readonly FlexibleView _list;
        private readonly GridAdapter _adapter;

        public string FocusKey => "grid:" + GetHashCode();

        /// <summary>Raised when focus/scroll approaches the snapshot end.</summary>
        public event Action NearEnd;

        public PosterGrid(int columns)
        {
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;

            _adapter = new GridAdapter();
            _adapter.NearEnd += () => NearEnd?.Invoke();

            _list = new FlexibleView();
            _list.SetAdapter(_adapter);
            _layout = new GridLayoutManager(0, columns);
            _list.SetLayoutManager(_layout);
            Add(_list);
        }

        public int ItemCount => _adapter.Count;

        public void Reset(Func<int, View> factory, int count)
        {
            _adapter.Bind(factory, Math.Min(count, MaxItemsPerSnapshot));
            _adapter.NotifyDataSetChanged();
        }

        public void EnsureVisible(int index) => _list.ScrollToPositionWithOffset(index, 0);

        public void ApplyFocus(bool focused)
        {
            // The grid itself is a passive container; focused children render
            // their own ring (PosterCard.ApplyFocus).
        }

        private sealed class GridAdapter : FlexibleView.Adapter
        {
            private Func<int, View> _factory;
            private int _count;

            public int Count => _count;

            public event Action NearEnd;

            public void Bind(Func<int, View> factory, int count)
            {
                _factory = factory;
                _count = count;
            }

            public override FlexibleView.ViewHolder OnCreateViewHolder(int viewType) =>
                new FlexibleView.ViewHolder(_factory(viewType));

            public override void OnBindViewHolder(FlexibleView.ViewHolder holder, int position)
            {
                if (_count > 0 && position >= _count - LoadAhead)
                {
                    NearEnd?.Invoke();
                }
            }

            public override void OnDestroyViewHolder(FlexibleView.ViewHolder holder)
            {
                holder.ItemView?.Dispose();
            }

            public override int GetItemCount() => _count;
        }
    }
}
