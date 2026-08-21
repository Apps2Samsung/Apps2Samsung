namespace Apps2Samsung.Collections
{
    /// <summary>
    /// The rule for moving an item up/down in an ordered list, shared so both heads' channel
    /// reordering (and any other list reorder) agree on the bounds. Each head still applies the move
    /// with its own collection type — the desktop's data-bound <c>ObservableCollection.Move</c>, the
    /// mobile head's in-code visual list — this just computes the valid target position.
    /// </summary>
    public static class ListReorder
    {
        /// <summary>
        /// The target index for moving the item at <paramref name="index"/> by <paramref name="delta"/>
        /// positions in a list of <paramref name="count"/> items, or <c>null</c> if the source index is
        /// invalid or the move would fall outside the list.
        /// </summary>
        public static int? TargetIndex(int count, int index, int delta)
        {
            if (index < 0 || index >= count)
                return null;

            var target = index + delta;
            return target >= 0 && target < count ? target : null;
        }
    }
}
