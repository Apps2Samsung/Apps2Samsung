using Apps2Samsung.ViewModels;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Apps2Samsung.Views
{
    /// <summary>Hosts a <see cref="TvStoreViewModel"/>: runs the setup check the first time the
    /// section is shown for a view model, like the On the TV section loads its list.</summary>
    public partial class TvStoreView : UserControl
    {
        private TvStoreViewModel? _loaded;

        public TvStoreView()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => Hook();
            AttachedToVisualTree += (_, _) => Hook();
        }

        private async void Hook()
        {
            if (DataContext is not TvStoreViewModel vm || ReferenceEquals(vm, _loaded) || this.GetVisualRoot() is null)
                return;
            _loaded = vm;
            await vm.LoadCommand.ExecuteAsync(null);
        }
    }
}
