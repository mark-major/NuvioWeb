using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NuvioTV.Core.UI;
using NuvioTV.Tizen.Navigation;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Widgets
{
    /// <summary>
    /// List picker built on NuvioDialog (posterOptionsMenu list-picker parity):
    /// shows a set of labeled options and reports the chosen index.
    /// </summary>
    public sealed class ListPickerDialog : IDisposable
    {
        private readonly NuvioDialog _dialog;

        public ListPickerDialog(string heading, IReadOnlyList<string> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            _dialog = new NuvioDialog(heading);
            foreach (var item in items)
            {
                _dialog.AddOption(item);
            }
        }

        public event Action<int> Selected
        {
            add { _dialog.Activated += value; }
            remove { _dialog.Activated -= value; }
        }

        public event Action DismissRequested
        {
            add { _dialog.DismissRequested += value; }
            remove { _dialog.DismissRequested -= value; }
        }

        public View View => _dialog;

        public void Present() => _dialog.Present();

        /// <summary>Key delegation for the modal gate.</summary>
        public bool OnKey(NuvioTV.Core.Input.NuvioKey key) => _dialog.OnKey(key);

        public void Dispose()
        {
            var parent = _dialog.GetParent();
            parent?.Remove(_dialog);
            _dialog.Dispose();
        }
    }

    /// <summary>
    /// Wires Core PosterOptions to a NuvioDialog: opens the option list for a
    /// poster item and routes activations through PosterOptions.Activate.
    /// </summary>
    public sealed class OptionMenuController : IDisposable
    {
        private readonly Func<PosterOptions.OptionsState, Task> _refreshState;
        private readonly Func<string, Task> _openDetails;
        private readonly EffectsSink _effectsSink = new EffectsSink();

        private NuvioDialog _dialog;
        private PosterOptions.OptionsState _state;
        private IReadOnlyList<PosterOptions.Option> _options;

        /// <summary>Injected data effects; defaults to no-op toggles.</summary>
        public PosterOptions.Effects Effects { get; } = new PosterOptions.Effects();

        public OptionMenuController(
            Func<PosterOptions.OptionsState, Task> refreshState,
            Func<string, Task> openDetails)
        {
            _refreshState = refreshState ?? throw new ArgumentNullException(nameof(refreshState));
            _openDetails = openDetails ?? throw new ArgumentNullException(nameof(openDetails));
        }

        public bool IsOpen => _dialog != null;

        public async Task OpenAsync(PosterOptions.OptionsState state)
        {
            await CloseAsync();
            _state = state;
            _options = PosterOptions.Get(state);
            if (_options.Count == 0) return;

            _effectsSink.Effects = Effects;
            _effectsSink.OpenDetails = () => _openDetails(state.Item.Id);
            _effectsSink.Reopen = () => OpenAsync(_state);

            _dialog = new NuvioDialog(state.Item.Title);
            foreach (var option in _options)
            {
                _dialog.AddOption(option.Label);
            }
            _dialog.DismissRequested += async () => await CloseAsync();
            _dialog.Activated += async i =>
            {
                if (i >= 0 && i < _options.Count)
                {
                    await ActivateAsync(_options[i].Action);
                }
            };
        }

        private async Task ActivateAsync(string action)
        {
            var result = PosterOptions.Activate(_state, action, _effectsSink.Effects);
            switch (result.Type)
            {
                case "details":
                    await CloseAsync();
                    await _openDetails(_state.Item.Id);
                    break;
                case "listPicker":
                    // List picker UI lands with Phase 12.2 library tabs; keep menu open.
                    break;
                case "updated":
                    _state = result.State;
                    await CloseAsync();
                    await _refreshState(_state);
                    break;
            }
        }

        public async Task CloseAsync()
        {
            if (_dialog == null) return;
            var dialog = _dialog;
            _dialog = null;
            var parent = dialog.GetParent();
            parent?.Remove(dialog);
            dialog.Dispose();
            await Task.CompletedTask;
        }

        public void Dispose() => CloseAsync().Wait();

        private sealed class EffectsSink
        {
            public PosterOptions.Effects Effects;
            public Func<Task> OpenDetails;
            public Func<Task> Reopen;
        }
    }
}
