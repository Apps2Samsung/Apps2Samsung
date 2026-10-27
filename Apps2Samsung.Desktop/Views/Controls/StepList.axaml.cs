using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Apps2Samsung.Views.Controls
{
    public enum StepState { Todo, Active, Done, Failed }

    /// <summary>One row of a <see cref="StepList"/>.</summary>
    public partial class StepItem : ObservableObject
    {
        public StepItem(int number, string title)
        {
            Number = number;
            Title = title;
        }

        public int Number { get; }
        public string Title { get; }
        public bool IsFirst => Number == 1;

        /// <summary>Whether a done step keeps its detail line.</summary>
        public bool KeepDoneDetail { get; init; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowDetail))]
        private string? detail;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsTodo), nameof(IsActive), nameof(IsDone), nameof(IsFailed), nameof(ShowDetail))]
        private StepState state;

        public bool IsTodo => State == StepState.Todo;
        public bool IsActive => State == StepState.Active;
        public bool IsDone => State == StepState.Done;
        public bool IsFailed => State == StepState.Failed;

        public bool ShowDetail => !string.IsNullOrEmpty(Detail)
                                  && (IsActive || IsFailed || (IsDone && KeepDoneDetail));
    }

    /// <summary>Paints a list of <see cref="StepItem"/>s; <see cref="SetCurrent"/> moves them on.</summary>
    public partial class StepList : UserControl
    {
        public static readonly StyledProperty<IList<StepItem>?> ItemsProperty =
            AvaloniaProperty.Register<StepList, IList<StepItem>?>(nameof(Items));

        public StepList()
        {
            InitializeComponent();
        }

        public IList<StepItem>? Items
        {
            get => GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        /// <summary>Builds the rows for <paramref name="titles"/>, the first one current.</summary>
        public static ObservableCollection<StepItem> Build(IEnumerable<string> titles, bool keepDoneDetails = false)
        {
            var items = new ObservableCollection<StepItem>(
                titles.Select((t, i) => new StepItem(i + 1, t) { KeepDoneDetail = keepDoneDetails }));
            SetCurrent(items, 0);
            return items;
        }

        /// <summary>Step <paramref name="index"/> becomes current; <c>Count</c> ticks them all.</summary>
        public static void SetCurrent(IList<StepItem> items, int index)
        {
            if (items.Any(i => i.IsFailed))
                return;
            for (var i = 0; i < items.Count; i++)
                items[i].State = i < index ? StepState.Done : i == index ? StepState.Active : StepState.Todo;
        }

        /// <summary>The current step failed with <paramref name="message"/>.</summary>
        public static void Fail(IList<StepItem> items, string message)
        {
            var current = items.FirstOrDefault(i => i.IsActive) ?? items.LastOrDefault();
            if (current is null)
                return;
            current.Detail = message;
            current.State = StepState.Failed;
        }
    }
}
