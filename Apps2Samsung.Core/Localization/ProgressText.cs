using System;
using System.Linq;

namespace Apps2Samsung.Localization
{
    /// <summary>
    /// How Core reports progress to a head without knowing its language: a message is an en.json
    /// key, optionally followed by <c>|</c>-separated format arguments
    /// (<c>statusInstallingOnTvPercent|42</c>). The head turns it into a sentence with
    /// <see cref="Localize"/> through its own catalog; the CLI through its English one. A message
    /// that is not a key (an older English sentence, an error text) passes through unchanged, so
    /// nothing is lost while the two shapes coexist.
    /// </summary>
    public static class ProgressText
    {
        public const char Separator = '|';

        /// <summary>A key with its format arguments, in the shape <see cref="Localize"/> reads.</summary>
        public static string Key(string key, params object?[] args)
            => args.Length == 0 ? key : key + Separator + string.Join(Separator, args.Select(a => a?.ToString() ?? string.Empty));

        /// <summary>
        /// The message as the user reads it: the key looked up and formatted with its arguments.
        /// A message that holds a space is a sentence already; a key the catalog does not know
        /// comes back as typed (the catalog returns the key itself), so the raw message is kept.
        /// </summary>
        public static string Localize(string? message, Func<string, string> localize)
        {
            if (string.IsNullOrEmpty(message) || message.Contains(' '))
                return message ?? string.Empty;

            var parts = message.Split(Separator);
            var key = parts[0];
            var text = localize(key);
            if (text == key)
                return message;
            if (parts.Length == 1)
                return text;
            try
            {
                return string.Format(text, parts.Skip(1).Cast<object>().ToArray());
            }
            catch (FormatException)
            {
                return text;
            }
        }
    }
}
