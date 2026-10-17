namespace Apps2Samsung.Catalog
{
    /// <summary>One row of the category filter: the id the entries carry, its localized name and how many entries it holds.</summary>
    public sealed record CategoryOption(string Id, string Label, int Count)
    {
        /// <summary>What the dropdown shows: "IPTV &amp; Live TV (8)".</summary>
        public string Display => $"{Label} ({Count})";

        public override string ToString() => Display;
    }
}
