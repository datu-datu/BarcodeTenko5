using System.Windows;
using BarcodeTenko.Client.Models;

namespace BarcodeTenko.Client;

public partial class LocationSelectWindow : Window
{
    public LocationSelectWindow(IEnumerable<Location> locations)
    {
        InitializeComponent();

        var list = new List<Location>(locations);
        LocationCombo.ItemsSource = list;
        if (list.Count > 0)
        {
            LocationCombo.SelectedIndex = 0;
        }
    }

    public Location? SelectedLocation { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (LocationCombo.SelectedItem is not Location location)
        {
            return;
        }

        SelectedLocation = location;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
