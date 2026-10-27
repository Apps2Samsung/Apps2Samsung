using System;
using Avalonia.Controls;
using Avalonia.Threading;
using Apps2Samsung.ViewModels;

namespace Apps2Samsung.Views
{
    /// <summary>
    /// The Developer Mode guide window. Re-reads the TV every few seconds while open; closes with
    /// true when the install should go on (ready, or Install anyway), false otherwise.
    /// </summary>
    public partial class DevModeGuideWindow : Window
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };

        public DevModeGuideWindow()
        {
            InitializeComponent();
        }

        public DevModeGuideWindow(DevModeGuideViewModel model) : this()
        {
            DataContext = model;
            model.Finished += goOn => Close(goOn);
            _timer.Tick += async (_, _) =>
            {
                if (model.IsReady)
                {
                    _timer.Stop();
                    return;
                }
                await model.CheckAsync(showProgress: false);
            };
            Opened += (_, _) => _timer.Start();
            Closed += (_, _) => _timer.Stop();
        }
    }
}
