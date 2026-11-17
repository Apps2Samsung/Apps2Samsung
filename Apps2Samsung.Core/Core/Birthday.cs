using System;

namespace Apps2Samsung.Helpers.Core
{
    /// <summary>
    /// The project's birthday: the first commit and v1.0 are from 13 May 2025. On that day of every
    /// later year the heads show one line saying how old the app is; the date alone decides, so it
    /// works offline and needs no server.
    /// </summary>
    public static class Birthday
    {
        /// <summary>The day of the first commit and the v1.0 release.</summary>
        public static readonly DateOnly FirstRelease = new(2025, 5, 13);

        /// <summary>How old the app turns on <paramref name="day"/>, or null when that day is not a
        /// birthday (any other date, or the day of the first release itself).</summary>
        public static int? AgeOn(DateOnly day) =>
            day.Month == FirstRelease.Month && day.Day == FirstRelease.Day && day.Year > FirstRelease.Year
                ? day.Year - FirstRelease.Year
                : null;

        /// <summary><see cref="AgeOn"/> for today, by the clock of this device.</summary>
        public static int? AgeToday() => AgeOn(DateOnly.FromDateTime(DateTime.Now));
    }
}
