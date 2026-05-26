namespace Jellyfin2Samsung.Models
{
    public class LanguageOption
    {
        public string Code { get; set; }
        public string Name { get; set; }

        public override string ToString() => Name;

        public override bool Equals(object? obj)
        {
            if (obj is LanguageOption other)
            {
                return Code == other.Code;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return Code?.GetHashCode() ?? 0;
        }
    }
}
