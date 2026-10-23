using System;
using Avalonia.Controls;
using Apps2Samsung.Helpers.Core;

namespace Apps2Samsung.Views.Controls
{
    public partial class LicenseFooter : UserControl
    {
        public LicenseFooter()
        {
            InitializeComponent();
            CopyrightText.Text = $"© {DateTime.Now.Year} · ";
            LicenseLink.NavigateUri = new Uri(Constants.Links.License);
            AuthorLink.Content = Constants.Links.AuthorName;
            AuthorLink.NavigateUri = new Uri(Constants.Links.Website);
        }
    }
}
