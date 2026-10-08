namespace Commerce.Pos.Windows;

/// <summary>
/// One column of an entity list. <paramref name="Text"/> is what the cell shows (null shows empty); the same text is
/// what the column sorts by unless <see cref="SortKey"/> supplies another value (a number, a date).
/// </summary>
/// <param name="Key">Stable id of the column (sorting and <see cref="EntityListDefinition{T}.InitialSortKey"/>).</param>
/// <param name="Header">The Spanish header the operator clicks to sort.</param>
/// <param name="Text">The cell text of one row.</param>
public sealed record EntityColumn<T>(string Key, string Header, Func<T, string?> Text)
{
    /// <summary>Fixed width in pixels; null shares the remaining width with the other unsized columns.</summary>
    public double? Width { get; init; }

    /// <summary>Whether clicking the header sorts by this column (toggling ascending / descending).</summary>
    public bool Sortable { get; init; } = true;

    /// <summary>Value to sort by instead of <see cref="Text"/>; it must be <see cref="IComparable"/> or null.</summary>
    public Func<T, object?>? SortKey { get; init; }

    /// <summary>A secondary column, hidden while the editor is open beside the list so the main ones keep room to read.</summary>
    public bool HideWhileEditing { get; init; }
}

/// <summary>One choice of a filter; a null <paramref name="Matches"/> lets every row through ("Todos").</summary>
public sealed record EntityFilterOption<T>(string Label, Func<T, bool>? Matches = null);

/// <summary>A filter shown as a labelled combo next to the search box; it starts on its first option.</summary>
public sealed record EntityFilter<T>(string Key, string Label, IReadOnlyList<EntityFilterOption<T>> Options);

/// <summary>
/// One button of every row. Label and icon may depend on the row (Habilitar / Deshabilitar). Either it opens the
/// editor on the row (<see cref="OpensEditor"/>, see <see cref="Edit"/>) or it runs <see cref="Run"/>, after the
/// operator confirmed <see cref="Confirmation"/> when that returns a question.
/// </summary>
/// <param name="Key">Stable id of the action (<see cref="EntityListModel{T}.InvokeAsync"/>).</param>
/// <param name="Label">The Spanish label (tooltip and accessible name) for one row.</param>
/// <param name="Icon">An icon glyph of the theme's icon font (see <see cref="EntityIcons"/>) for one row.</param>
public sealed record EntityRowAction<T>(string Key, Func<T, string> Label, Func<T, string> Icon)
{
    public const string EditKey = "edit";

    /// <summary>The work of the action; the entity screen runs it through its busy controller.</summary>
    public Func<T, Task>? Run { get; init; }

    /// <summary>Opens the editor on the row instead of running <see cref="Run"/>.</summary>
    public bool OpensEditor { get; init; }

    /// <summary>Whether the action is available for one row; null means always.</summary>
    public Func<T, bool>? IsEnabled { get; init; }

    /// <summary>The question asked inline before <see cref="Run"/>; null (or a null result) runs at once.</summary>
    public Func<T, string?>? Confirmation { get; init; }

    /// <summary>The standard "Editar" action: opens the editor filled with the row.</summary>
    public static EntityRowAction<T> Edit(string label = "Editar") =>
        new(EditKey, _ => label, _ => EntityIcons.Edit) { OpensEditor = true };
}

/// <summary>
/// Everything an entity screen supplies to the reusable desktop list (<c>Controls/EntityListView</c> over an
/// <see cref="EntityListModel{T}"/>): how to identify a row, the columns, the fields the search covers, the filters,
/// the row actions and the editor titles. The edit form itself is the screen's own XAML, set as the control's
/// <c>EditorContent</c>; the screen fills or resets it from <see cref="EntityListModel{T}.EditorChanged"/>.
/// </summary>
/// <param name="Key">Identity of a row, so a reloaded list keeps the row open in the editor.</param>
/// <param name="Columns">The list columns, left to right.</param>
/// <param name="SearchFields">The texts of one row the search box looks into (every typed word must match one).</param>
public sealed record EntityListDefinition<T>(
    Func<T, object> Key,
    IReadOnlyList<EntityColumn<T>> Columns,
    Func<T, IEnumerable<string?>> SearchFields)
{
    public IReadOnlyList<EntityFilter<T>> Filters { get; init; } = [];

    public IReadOnlyList<EntityRowAction<T>> RowActions { get; init; } = [];

    /// <summary>The column the list starts sorted by (ascending); null keeps the loaded order.</summary>
    public string? InitialSortKey { get; init; }

    public string NewLabel { get; init; } = "Nuevo";

    public string SearchPlaceholder { get; init; } = "Buscar…";

    /// <summary>The editor title while creating.</summary>
    public string NewTitle { get; init; } = "Nuevo";

    /// <summary>The editor title while editing one row.</summary>
    public Func<T, string> EditTitle { get; init; } = _ => "Editar";

    /// <summary>What the editor panel says while it is closed.</summary>
    public string EmptyEditorHint { get; init; } = "Elegí una fila de la lista o tocá «Nuevo».";
}

/// <summary>Glyphs of the theme's icon font (Segoe Fluent Icons / MDL2) for row actions.</summary>
public static class EntityIcons
{
    public const string Edit = "\uE70F";
    public const string Enable = "\uE73E";
    public const string Disable = "\uE733";
    public const string Password = "\uE8D7";
}
