using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Commerce.Pos.Windows.Controls;

/// <summary>
/// The reusable desktop entity list (operator-ux-adjustments T4): a search box that filters as the operator types,
/// one combo per filter, a list whose headers sort (click again to toggle, ▲ / ▼ shows the direction), a "Nuevo"
/// button, one icon button per row action (with an inline confirmation when the action asks first), and an editor
/// panel beside the list that hosts the entity's own form.
///
/// To adopt it, an entity screen:
/// <list type="number">
/// <item>describes its list in an <see cref="EntityListDefinition{T}"/> (columns, search fields, filters, row actions,
/// titles; see <see cref="CustomerList"/>) and builds an <see cref="EntityListModel{T}"/> from it;</item>
/// <item>places <c>&lt;controls:EntityListView&gt;</c> where the list goes, with its form XAML as
/// <see cref="EditorContent"/> (the form scrolls inside the panel if it needs to; its fields may carry
/// <c>x:Name</c>, they stay in the screen's name scope), and sets the model as <see cref="Model"/>;</item>
/// <item>loads rows with <see cref="EntityListModel{T}.SetItems"/> and fills or resets its form on
/// <see cref="EntityListModel{T}.EditorChanged"/>.</item>
/// </list>
/// A lookless control (its template is <c>Controls/EntityListView.xaml</c>, merged into App.xaml): a UserControl with
/// its own named XAML could not host a form with named fields. It holds no rules: it renders the model again whenever
/// it raises <see cref="IEntityListModel.Changed"/> and forwards the operator's clicks.
/// </summary>
[TemplatePart(Name = "PART_Rows", Type = typeof(DataGrid))]
[TemplatePart(Name = "PART_SearchBox", Type = typeof(TextBox))]
public sealed class EntityListView : Control
{
    /// <summary>The entity's edit form, shown in the editor panel while a row is new or open.</summary>
    public static readonly DependencyProperty EditorContentProperty = DependencyProperty.Register(
        nameof(EditorContent), typeof(object), typeof(EntityListView),
        new PropertyMetadata(null, (d, e) => ((EntityListView)d).OnEditorContentChanged(e.OldValue, e.NewValue)));

    /// <summary>The editor panel's share of the width (default 2* against the list's 3*); a pixel width also works.</summary>
    public static readonly DependencyProperty EditorWidthProperty = DependencyProperty.Register(
        nameof(EditorWidth), typeof(GridLength), typeof(EntityListView),
        new PropertyMetadata(new GridLength(2, GridUnitType.Star), (d, _) => ((EntityListView)d).ApplyEditorWidth()));

    /// <summary>
    /// True folds the editor panel away while nothing is open, so the list takes the full width; "Nuevo" or a row
    /// brings it back beside the list. False (the default) keeps the panel with its hint.
    /// </summary>
    public static readonly DependencyProperty HidesClosedEditorProperty = DependencyProperty.Register(
        nameof(HidesClosedEditor), typeof(bool), typeof(EntityListView),
        new PropertyMetadata(false, (d, _) => ((EntityListView)d).ApplyEditorWidth()));

    private readonly List<DataGridColumn> _columns = new();
    private readonly List<ComboBox> _filterBoxes = new();
    private readonly EntityRowSelectionGate _selectionGate = new();
    private IEntityListModel? _model;
    private IReadOnlyList<EntityRowState> _rows = [];
    private bool _rendering;
    private bool _renderQueued;

    private Grid? _layout;
    private DataGrid? _grid;
    private TextBox? _searchBox;
    private UIElement? _searchPlaceholder;
    private Panel? _filters;
    private Button? _newButton;
    private TextBlock? _newLabel;
    private UIElement? _confirmation;
    private TextBlock? _confirmationText;
    private Button? _confirmButton;
    private Button? _cancelConfirmationButton;
    private UIElement? _editorPanel;
    private double _editorMinWidth;
    private double _editorGapWidth;
    private UIElement? _editorHeader;
    private TextBlock? _editorTitle;
    private Button? _closeEditorButton;
    private UIElement? _editor;
    private TextBlock? _emptyEditor;
    private TextBlock? _count;

    public EntityListView()
    {
        // Row action buttons live in a data template: one handler on the control serves all of them.
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnRowActionClick));
        // The grid selects the row under a press even when an action button in it takes the press: remember where the
        // press started so that selection does not open the editor.
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPreviewPress), handledEventsToo: true);
        AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => _selectionGate.PressReleased()), handledEventsToo: true);
    }

    public object? EditorContent
    {
        get => GetValue(EditorContentProperty);
        set => SetValue(EditorContentProperty, value);
    }

    public GridLength EditorWidth
    {
        get => (GridLength)GetValue(EditorWidthProperty);
        set => SetValue(EditorWidthProperty, value);
    }

    public bool HidesClosedEditor
    {
        get => (bool)GetValue(HidesClosedEditorProperty);
        set => SetValue(HidesClosedEditorProperty, value);
    }

    /// <summary>The list to render; the columns and filters are built from it.</summary>
    public IEntityListModel? Model
    {
        get => _model;
        set
        {
            if (_model is not null)
            {
                _model.Changed -= QueueRender;
            }

            _model = value;
            if (_model is not null)
            {
                _model.Changed += QueueRender;
            }

            _rows = [];
            BuildColumns();
            BuildFilters();
            Render();
        }
    }

    /// <summary>The form is a logical child, so it inherits from and is found through the screen that declared it.</summary>
    protected override IEnumerator LogicalChildren =>
        EditorContent is { } content ? new[] { content }.GetEnumerator() : Enumerable.Empty<object>().GetEnumerator();

    public override void OnApplyTemplate()
    {
        if (_searchBox is not null)
        {
            _searchBox.TextChanged -= SearchBox_TextChanged;
        }

        if (_grid is not null)
        {
            _grid.Sorting -= Grid_Sorting;
            _grid.SelectionChanged -= Grid_SelectionChanged;
        }

        Unhook(_newButton, NewButton_Click);
        Unhook(_confirmButton, ConfirmButton_Click);
        Unhook(_cancelConfirmationButton, CancelConfirmationButton_Click);
        Unhook(_closeEditorButton, CloseEditorButton_Click);

        base.OnApplyTemplate();

        _layout = GetTemplateChild("PART_Layout") as Grid;
        _grid = GetTemplateChild("PART_Rows") as DataGrid;
        _searchBox = GetTemplateChild("PART_SearchBox") as TextBox;
        _searchPlaceholder = GetTemplateChild("PART_SearchPlaceholder") as UIElement;
        _filters = GetTemplateChild("PART_Filters") as Panel;
        _newButton = GetTemplateChild("PART_NewButton") as Button;
        _newLabel = GetTemplateChild("PART_NewLabel") as TextBlock;
        _confirmation = GetTemplateChild("PART_Confirmation") as UIElement;
        _confirmationText = GetTemplateChild("PART_ConfirmationText") as TextBlock;
        _confirmButton = GetTemplateChild("PART_ConfirmButton") as Button;
        _cancelConfirmationButton = GetTemplateChild("PART_CancelConfirmationButton") as Button;
        _editorPanel = GetTemplateChild("PART_EditorPanel") as UIElement;
        _editorHeader = GetTemplateChild("PART_EditorHeader") as UIElement;
        _editorTitle = GetTemplateChild("PART_EditorTitle") as TextBlock;
        _closeEditorButton = GetTemplateChild("PART_CloseEditorButton") as Button;
        _editor = GetTemplateChild("PART_Editor") as UIElement;
        _emptyEditor = GetTemplateChild("PART_EmptyEditor") as TextBlock;
        _count = GetTemplateChild("PART_Count") as TextBlock;

        if (_searchBox is not null)
        {
            _searchBox.TextChanged += SearchBox_TextChanged;
        }

        if (_grid is not null)
        {
            _grid.Sorting += Grid_Sorting;
            _grid.SelectionChanged += Grid_SelectionChanged;
        }

        Hook(_newButton, NewButton_Click);
        Hook(_confirmButton, ConfirmButton_Click);
        Hook(_cancelConfirmationButton, CancelConfirmationButton_Click);
        Hook(_closeEditorButton, CloseEditorButton_Click);

        if (_layout is { ColumnDefinitions.Count: 3 })
        {
            _editorGapWidth = _layout.ColumnDefinitions[1].Width.Value;
            _editorMinWidth = _layout.ColumnDefinitions[2].MinWidth;
        }

        ApplyEditorWidth();
        _rows = [];
        BuildColumns();
        BuildFilters();
        Render();
    }

    private static void Hook(Button? button, RoutedEventHandler handler)
    {
        if (button is not null)
        {
            button.Click += handler;
        }
    }

    private static void Unhook(Button? button, RoutedEventHandler handler)
    {
        if (button is not null)
        {
            button.Click -= handler;
        }
    }

    private void OnEditorContentChanged(object? oldContent, object? newContent)
    {
        if (oldContent is not null)
        {
            RemoveLogicalChild(oldContent);
        }

        if (newContent is not null)
        {
            AddLogicalChild(newContent);
        }
    }

    /// <summary>The editor's share of the width, or none (the list takes it all) while a hidden editor is closed.</summary>
    private void ApplyEditorWidth()
    {
        var shown = EntityEditorPanel.IsShown(HidesClosedEditor, _model?.EditorMode ?? EntityEditorMode.None);
        SetVisible(_editorPanel, shown);
        if (_layout is { ColumnDefinitions.Count: 3 })
        {
            _layout.ColumnDefinitions[1].Width = new GridLength(shown ? _editorGapWidth : 0);
            _layout.ColumnDefinitions[2].MinWidth = shown ? _editorMinWidth : 0;
            _layout.ColumnDefinitions[2].Width = shown ? EditorWidth : new GridLength(0);
        }

        // The secondary columns step aside while the editor takes part of the width.
        var editing = shown && _model?.EditorMode != EntityEditorMode.None;
        var states = _model?.Columns;
        for (var i = 0; states is not null && i < _columns.Count && i < states.Count; i++)
        {
            _columns[i].Visibility = editing && states[i].HideWhileEditing ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Several changes in one handler (open the editor, reload the rows) render once, after the handler.</summary>
    private void QueueRender()
    {
        if (_renderQueued)
        {
            return;
        }

        _renderQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _renderQueued = false;
            Render();
        }, DispatcherPriority.DataBind);
    }

    private void BuildColumns()
    {
        if (_grid is null)
        {
            return;
        }

        _grid.ItemsSource = null;
        _grid.Columns.Clear();
        _columns.Clear();
        if (_model is null)
        {
            return;
        }

        var cellStyle = new Style(typeof(TextBlock));
        cellStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        cellStyle.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));

        var columns = _model.Columns;
        for (var i = 0; i < columns.Count; i++)
        {
            var column = new DataGridTextColumn
            {
                Binding = new Binding($"Cells[{i}]") { Mode = BindingMode.OneWay },
                SortMemberPath = columns[i].Key,
                CanUserSort = columns[i].Sortable,
                Width = columns[i].Width is { } width ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 72,
                ElementStyle = cellStyle,
            };
            _columns.Add(column);
            _grid.Columns.Add(column);
        }

        if (_model.RowActionCount > 0 && _layout?.TryFindResource("RowActionsTemplate") is DataTemplate actions)
        {
            _grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Acciones",
                CanUserSort = false,
                Width = new DataGridLength(_model.RowActionCount * 40 + 20),
                CellTemplate = actions,
            });
        }

        ApplyEditorWidth();
    }

    private void BuildFilters()
    {
        if (_filters is null)
        {
            return;
        }

        _filters.Children.Clear();
        _filterBoxes.Clear();
        if (_model is null)
        {
            return;
        }

        foreach (var filter in _model.Filters)
        {
            var label = new TextBlock { Text = filter.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var box = new ComboBox { ItemsSource = filter.Options, MinWidth = 130, Tag = filter.Key };
            AutomationProperties.SetName(box, filter.Label);
            box.SelectionChanged += FilterBox_SelectionChanged;
            _filters.Children.Add(label);
            _filters.Children.Add(box);
            _filterBoxes.Add(box);
        }
    }

    private void Render()
    {
        if (_grid is null)
        {
            return;
        }

        _rendering = true;
        try
        {
            var model = _model;
            SetEnabled(_newButton, model is not null);
            SetText(_newLabel, model?.NewLabel ?? "Nuevo");
            if (_searchPlaceholder is TextBox placeholder)
            {
                placeholder.Text = model?.SearchPlaceholder ?? string.Empty;
            }

            if (model is null)
            {
                _grid.ItemsSource = null;
                SetText(_count, string.Empty);
                SetVisible(_confirmation, false);
                RenderEditor(EntityEditorMode.None, string.Empty, string.Empty);
                RenderSearchPlaceholder();
                return;
            }

            if (_searchBox is not null && _searchBox.Text != model.SearchText)
            {
                _searchBox.Text = model.SearchText;
            }

            RenderSearchPlaceholder();

            var filters = model.Filters;
            for (var i = 0; i < filters.Count && i < _filterBoxes.Count; i++)
            {
                _filterBoxes[i].SelectedIndex = filters[i].SelectedIndex;
            }

            var columns = model.Columns;
            for (var i = 0; i < columns.Count && i < _columns.Count; i++)
            {
                _columns[i].Header = columns[i].SortIndicator.Length == 0
                    ? columns[i].Header
                    : $"{columns[i].Header} {columns[i].SortIndicator}";
            }

            // Rebinding resets the list's scroll and selection; only a real change of the rows does it.
            var rows = model.Rows;
            if (_grid.ItemsSource is null || !SameRows(_rows, rows))
            {
                _rows = rows;
                _grid.ItemsSource = rows;
            }

            var selected = _rows.FirstOrDefault(r => ReferenceEquals(r.Item, model.EditingItem));
            if (!ReferenceEquals(_grid.SelectedItem, selected))
            {
                _grid.SelectedItem = selected;
                if (selected is not null)
                {
                    _grid.ScrollIntoView(selected);
                }
            }

            SetText(_count, model.CountText);

            var confirmation = model.PendingConfirmation;
            SetVisible(_confirmation, confirmation is not null);
            SetText(_confirmationText, confirmation?.Question ?? string.Empty);
            if (_confirmButton is not null)
            {
                _confirmButton.Content = confirmation?.ConfirmLabel ?? string.Empty;
            }

            RenderEditor(model.EditorMode, model.EditorTitle, model.EmptyEditorHint);
        }
        finally
        {
            _rendering = false;
        }
    }

    private void RenderSearchPlaceholder() =>
        SetVisible(_searchPlaceholder, string.IsNullOrEmpty(_searchBox?.Text));

    private void RenderEditor(EntityEditorMode mode, string title, string emptyHint)
    {
        ApplyEditorWidth();
        var open = mode != EntityEditorMode.None;
        SetVisible(_editorHeader, open);
        SetVisible(_editor, open);
        SetVisible(_emptyEditor, !open);
        SetText(_editorTitle, title);
        SetText(_emptyEditor, emptyHint);
    }

    private static void SetVisible(UIElement? element, bool visible)
    {
        if (element is not null)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void SetText(TextBlock? block, string text)
    {
        if (block is not null)
        {
            block.Text = text;
        }
    }

    private static void SetEnabled(UIElement? element, bool enabled)
    {
        if (element is not null)
        {
            element.IsEnabled = enabled;
        }
    }

    private static bool SameRows(IReadOnlyList<EntityRowState> current, IReadOnlyList<EntityRowState> next) =>
        current.Count == next.Count
        && current.Zip(next).All(pair =>
            ReferenceEquals(pair.First.Item, pair.Second.Item)
            && pair.First.Cells.SequenceEqual(pair.Second.Cells)
            && pair.First.Actions.SequenceEqual(pair.Second.Actions));

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderSearchPlaceholder();
        if (!_rendering && _model is not null && _searchBox is not null)
        {
            _model.SearchText = _searchBox.Text;
        }
    }

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_rendering && _model is not null && sender is ComboBox { Tag: string key } box)
        {
            _model.SelectFilterOption(key, box.SelectedIndex);
        }
    }

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // The model sorts (empty values last, accents and case ignored); the grid's own sort would fight it.
        e.Handled = true;
        _model?.SortBy(e.Column.SortMemberPath);
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles; only the list's own selection opens a row.
        if (!ReferenceEquals(e.OriginalSource, _grid))
        {
            return;
        }

        if (!_selectionGate.SelectionOpensEditor)
        {
            // A press on a row action selected the row: put back the model's selection, the editor stays as it was.
            QueueRender();
            return;
        }

        if (!_rendering && _model is not null && _grid?.SelectedItem is EntityRowState row)
        {
            _model.Select(row.Item);
        }
    }

    private void OnPreviewPress(object sender, MouseButtonEventArgs e)
    {
        if (IsInRowAction(e.OriginalSource as DependencyObject))
        {
            _selectionGate.RowActionPressed();
        }
        else
        {
            _selectionGate.PressReleased();
        }
    }

    /// <summary>True when <paramref name="element"/> is (inside) a row action button of this list.</summary>
    private bool IsInRowAction(DependencyObject? element)
    {
        while (element is not null && !ReferenceEquals(element, this))
        {
            if (element is Button { DataContext: EntityActionState })
            {
                return true;
            }

            element = element is Visual or Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    private void NewButton_Click(object sender, RoutedEventArgs e) => _model?.BeginNew();

    private void CloseEditorButton_Click(object sender, RoutedEventArgs e) => _model?.CloseEditor();

    private async void OnRowActionClick(object sender, RoutedEventArgs e)
    {
        if (_model is not null && e.OriginalSource is Button { DataContext: EntityActionState action })
        {
            e.Handled = true;
            await _model.InvokeAsync(action.Item, action.Key);
        }
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            await _model.ConfirmAsync();
        }
    }

    private void CancelConfirmationButton_Click(object sender, RoutedEventArgs e) => _model?.CancelConfirmation();
}
