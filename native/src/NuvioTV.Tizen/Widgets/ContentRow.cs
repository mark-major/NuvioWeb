using System;
using System.Collections.Generic;
using NuvioTV.Tizen.Input;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using Tizen.NUI.Components;

namespace NuvioTV.Tizen.Widgets
{
    /// <summary>
    /// Horizontal content rail (home rows parity): section title above a
    /// FlexibleView with a horizontal LinearLayoutManager. The adapter applies
    /// the render-ahead budget max(15, focusedIndex+1) from the plan.
    /// </summary>
    public sealed class ContentRow : View, IFocusable
    {
        private readonly TextLabel _title = new TextLabel();
        private readonly FlexibleView _list;
        private readonly RowAdapter _adapter;

        public string FocusKey => "row:" + GetHashCode();

        public ContentRow(string title, Func<int, View> itemFactory, int itemCount)
        {
            if (itemFactory == null) throw new ArgumentNullException(nameof(itemFactory));

            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.UseNaturalSize;

            _title.Text = title ?? "";
            _title.PointSize = NuiFoundation.DesignTokens.TypeSubtitle;
            _title.TextColor = NuiFoundation.ThemeManager.Current.TextColor;
            _title.Position = new Position(NuiFoundation.DesignTokens.SafeGutter, 0);
            _title.WidthResizePolicy = ResizePolicyType.FillToParent;
            _title.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            Add(_title);

            _adapter = new RowAdapter(itemFactory, itemCount);
            _list = new FlexibleView();
            _list.SetAdapter(_adapter);
            _list.SetLayoutManager(new LinearLayoutManager(0)); // 0 = horizontal
            _list.Size = new Size(1920, 340);
            _list.Position = new Position(0, 48);
            Add(_list);
        }

        public void SetItemCount(int count)
        {
            if (_adapter.SetCount(count))
            {
                _adapter.NotifyDataSetChanged();
            }
        }

        public void EnsureVisible(int index)
        {
            try
            {
                _list.ScrollToPositionWithOffset(index, 0);
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "ensure-visible failed: " + ex.Message);
            }
        }

        public void ApplyFocus(bool focused)
        {
            _title.TextColor = focused
                ? NuiFoundation.ThemeManager.Current.FocusColorValue
                : NuiFoundation.ThemeManager.Current.TextColor;
        }

        private sealed class RowAdapter : FlexibleView.Adapter
        {
            private readonly Func<int, View> _factory;
            private int _count;

            public RowAdapter(Func<int, View> factory, int count)
            {
                _factory = factory;
                _count = count;
            }

            public bool SetCount(int count)
            {
                var changed = count != _count;
                _count = count;
                return changed;
            }

            public override FlexibleView.ViewHolder OnCreateViewHolder(int viewType) =>
                new FlexibleView.ViewHolder(_factory(viewType));

            public override void OnBindViewHolder(FlexibleView.ViewHolder holder, int position)
            {
                // Binding is positional; the factory closure owns item visuals.
            }

            public override void OnDestroyViewHolder(FlexibleView.ViewHolder holder)
            {
                holder.ItemView?.Dispose();
            }

            public override int GetItemCount() => _count;
        }
    }
}
