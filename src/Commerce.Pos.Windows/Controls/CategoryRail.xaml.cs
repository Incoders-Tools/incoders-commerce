using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Commerce.Pos.Windows.Controls;

/// <summary>
/// Vertical category filter. With no categories supplied it renders a single
/// "Todos" entry; the host feeds real categories through <see cref="Categories"/>.
/// </summary>
public partial class CategoryRail : UserControl
{
    private static readonly IReadOnlyList<CategoryRailItem> DefaultCategories = new[] { CategoryRailItem.All };

    public CategoryRail()
    {
        InitializeComponent();
        Categories = null;
    }

    public event EventHandler<CategoryRailItem>? CategorySelected;

    /// <summary>The entries to show; null or empty falls back to the single "Todos" entry.</summary>
    public IReadOnlyList<CategoryRailItem>? Categories
    {
        get => RailList.ItemsSource as IReadOnlyList<CategoryRailItem>;
        set
        {
            var items = value is { Count: > 0 } ? value : DefaultCategories;
            var previousKey = SelectedCategory?.Key;
            RailList.ItemsSource = items;
            RailList.SelectedItem = items.FirstOrDefault(i => i.Key == previousKey) ?? items[0];
        }
    }

    public CategoryRailItem? SelectedCategory => RailList.SelectedItem as CategoryRailItem;

    private void RailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedCategory is { } selected)
        {
            CategorySelected?.Invoke(this, selected);
        }
    }
}
