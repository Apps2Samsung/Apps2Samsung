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
            SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        }

        private bool? _stacked;

        // The setup check and the certificate card sit side by side only when each gets a readable
        // width; on the default window they go one under the other.
        private void Arrange(double width)
        {
            var stacked = width < 680;
            if (stacked == _stacked)
                return;
            _stacked = stacked;
            Columns.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,*");
            Grid.SetColumn(CertificateColumn, stacked ? 0 : 1);
            Grid.SetRow(CertificateColumn, stacked ? 1 : 0);
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
