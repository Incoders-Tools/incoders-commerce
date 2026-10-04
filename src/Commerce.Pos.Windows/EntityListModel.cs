using System.Globalization;
using System.Text;

namespace Commerce.Pos.Windows;

/// <summary>Whether the editor panel of an entity list is closed, creating a new row, or editing one.</summary>
public enum EntityEditorMode
{
    None,
    New,
    Edit,
}

/// <summary>A column as the control renders it; <see cref="SortIndicator"/> is ▲, ▼ or empty.</summary>
public sealed record EntityColumnState(string Key, string Header, double? Width, bool Sortable, string SortIndicator);

/// <summary>A filter as the control renders it: a label and a combo of options.</summary>
public sealed record EntityFilterState(string Key, string Label, IReadOnlyList<string> Options, int SelectedIndex);

/// <summary>One action button of one row.</summary>
public sealed record EntityActionState(object Item, string Key, string Label, string Icon, bool IsEnabled);

/// <summary>One visible row: its item, one text per column, and its actions.</summary>
public sealed record EntityRowState(object Item, IReadOnlyList<string> Cells, IReadOnlyList<EntityActionState> Actions);

/// <summary>The inline question shown before a row action that asks first.</summary>
public sealed record EntityConfirmation(string Question, string ConfirmLabel);

/// <summary>
/// The non-generic face of <see cref="EntityListModel{T}"/> the WPF control (<c>Controls/EntityListView</c>) binds to.
/// The control renders it again on <see cref="Changed"/> and forwards the operator's clicks; it holds no rules.
/// </summary>
public interface IEntityListModel
{
    string NewLabel { get; }

    string SearchPlaceholder { get; }

    string EmptyEditorHint { get; }

    IReadOnlyList<EntityColumnState> Columns { get; }

    IReadOnlyList<EntityFilterState> Filters { get; }

    /// <summary>The visible rows: searched, filtered and sorted.</summary>
    IReadOnlyList<EntityRowState> Rows { get; }

    /// <summary>The number of action buttons of every row (sizes the actions column).</summary>
    int RowActionCount { get; }

    string SearchText { get; set; }

    /// <summary>"visible de total", e.g. "3 de 10".</summary>
    string CountText { get; }

    EntityEditorMode EditorMode { get; }

    object? EditingItem { get; }

    string EditorTitle { get; }

    EntityConfirmation? PendingConfirmation { get; }

    /// <summary>Raised whenever anything the control renders changed.</summary>
    event Action? Changed;

    void SortBy(string columnKey);

    void SelectFilterOption(string filterKey, int optionIndex);

    void BeginNew();

    /// <summary>The operator selected a row: the editor opens on it.</summary>
    void Select(object item);

    void CloseEditor();

    Task InvokeAsync(object item, string actionKey);

    Task ConfirmAsync();

    void CancelConfirmation();
}

/// <summary>
/// The UI-free state of a reusable desktop entity list (operator-ux-adjustments T4): the loaded rows, the search text
/// (every typed word must appear in one of the row's search fields, ignoring case, accents and the separators of
/// numbers such as a CUIT or a phone), the filters, the sort (clicking a header sorts ascending, clicking it again
/// toggles; empty values always go last), the row actions (with an optional inline confirmation) and the editor state
/// (closed, new, or editing a row). An entity screen builds one from an <see cref="EntityListDefinition{T}"/>, loads it
/// with <see cref="SetItems"/>, and fills or resets its form on <see cref="EditorChanged"/>.
/// </summary>
public sealed class EntityListModel<T> : IEntityListModel where T : class
{
    private static readonly StringComparer TextComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("es-AR"), CompareOptions.IgnoreCase);

    private readonly EntityListDefinition<T> _definition;
    private readonly int[] _filterSelection;
    private IReadOnlyList<T> _items = [];
    private IReadOnlyList<T> _visible = [];
    private string _searchText = string.Empty;
    private (EntityRowAction<T> Action, T Item)? _pending;

    public EntityListModel(EntityListDefinition<T> definition)
    {
        _definition = definition;
        _filterSelection = new int[definition.Filters.Count];
        SortColumnKey = definition.InitialSortKey;
    }

    public event Action? Changed;

    /// <summary>The editor opened (New / Edit, with the row) or closed (None): fill, reset or leave the form.</summary>
    public event Action<EntityEditorMode, T?>? EditorChanged;

    public IReadOnlyList<T> Items => _items;

    /// <summary>The rows the list shows, in order.</summary>
    public IReadOnlyList<T> Visible => _visible;

    public string? SortColumnKey { get; private set; }

    public bool SortDescending { get; private set; }

    public EntityEditorMode EditorMode { get; private set; }

    /// <summary>The row open in the editor (null unless <see cref="EditorMode"/> is Edit).</summary>
    public T? Editing { get; private set; }

    public string EditorTitle => EditorMode switch
    {
        EntityEditorMode.New => _definition.NewTitle,
        EntityEditorMode.Edit when Editing is not null => _definition.EditTitle(Editing),
        _ => string.Empty,
    };

    public EntityConfirmation? PendingConfirmation =>
        _pending is { } pending
            ? new EntityConfirmation(pending.Action.Confirmation!(pending.Item)!, pending.Action.Label(pending.Item))
            : null;

    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value ?? string.Empty;
            Refresh();
        }
    }

    public string CountText => $"{_visible.Count} de {_items.Count}";

    public string NewLabel => _definition.NewLabel;

    public string SearchPlaceholder => _definition.SearchPlaceholder;

    public string EmptyEditorHint => _definition.EmptyEditorHint;

    public int RowActionCount => _definition.RowActions.Count;

    public IReadOnlyList<EntityColumnState> Columns => _definition.Columns
        .Select(c => new EntityColumnState(c.Key, c.Header, c.Width, c.Sortable, SortIndicator(c.Key)))
        .ToList();

    public IReadOnlyList<EntityFilterState> Filters => _definition.Filters
        .Select((f, i) => new EntityFilterState(f.Key, f.Label, f.Options.Select(o => o.Label).ToList(), _filterSelection[i]))
        .ToList();

    public IReadOnlyList<EntityRowState> Rows => _visible
        .Select(item => new EntityRowState(
            item,
            _definition.Columns.Select(c => c.Text(item) ?? string.Empty).ToList(),
            _definition.RowActions.Select(a => new EntityActionState(item, a.Key, a.Label(item), a.Icon(item), IsEnabled(a, item))).ToList()))
        .ToList();

    object? IEntityListModel.EditingItem => Editing;

    /// <summary>
    /// Replaces the rows (a reload). A row open in the editor is matched by key and replaced by its fresh copy without
    /// raising <see cref="EditorChanged"/>, so what the operator is typing is never overwritten.
    /// </summary>
    public void SetItems(IEnumerable<T> items)
    {
        _items = items.ToList();
        if (Editing is not null)
        {
            var key = _definition.Key(Editing);
            Editing = _items.FirstOrDefault(i => Equals(_definition.Key(i), key)) ?? Editing;
        }

        Refresh();
    }

    public void SortBy(string columnKey)
    {
        var column = _definition.Columns.FirstOrDefault(c => c.Key == columnKey);
        if (column is null || !column.Sortable)
        {
            return;
        }

        SortDescending = SortColumnKey == columnKey && !SortDescending;
        SortColumnKey = columnKey;
        Refresh();
    }

    public void SelectFilterOption(string filterKey, int optionIndex)
    {
        for (var i = 0; i < _definition.Filters.Count; i++)
        {
            var filter = _definition.Filters[i];
            if (filter.Key == filterKey && optionIndex >= 0 && optionIndex < filter.Options.Count)
            {
                _filterSelection[i] = optionIndex;
                Refresh();
                return;
            }
        }
    }

    public void BeginNew() => OpenEditor(EntityEditorMode.New, null);

    public void BeginEdit(T item)
    {
        if (EditorMode == EntityEditorMode.Edit && Editing is not null && Equals(_definition.Key(Editing), _definition.Key(item)))
        {
            return;
        }

        OpenEditor(EntityEditorMode.Edit, item);
    }

    public void CloseEditor() => OpenEditor(EntityEditorMode.None, null);

    /// <summary>
    /// Runs the row action <paramref name="actionKey"/> on <paramref name="item"/>: opens the editor, asks first (see
    /// <see cref="PendingConfirmation"/>), or runs it. A disabled or unknown action does nothing.
    /// </summary>
    public async Task InvokeAsync(T item, string actionKey)
    {
        var action = _definition.RowActions.FirstOrDefault(a => a.Key == actionKey);
        if (action is null || !IsEnabled(action, item))
        {
            return;
        }

        if (action.OpensEditor)
        {
            BeginEdit(item);
            return;
        }

        if (action.Confirmation?.Invoke(item) is not null)
        {
            _pending = (action, item);
            Changed?.Invoke();
            return;
        }

        if (action.Run is { } run)
        {
            await run(item);
        }
    }

    /// <summary>The operator confirmed the pending question: the action runs once.</summary>
    public async Task ConfirmAsync()
    {
        if (_pending is not { } pending)
        {
            return;
        }

        _pending = null;
        Changed?.Invoke();
        if (pending.Action.Run is { } run)
        {
            await run(pending.Item);
        }
    }

    public void CancelConfirmation()
    {
        _pending = null;
        Changed?.Invoke();
    }

    void IEntityListModel.Select(object item) => BeginEdit((T)item);

    Task IEntityListModel.InvokeAsync(object item, string actionKey) => InvokeAsync((T)item, actionKey);

    private void OpenEditor(EntityEditorMode mode, T? item)
    {
        EditorMode = mode;
        Editing = item;
        EditorChanged?.Invoke(mode, item);
        Changed?.Invoke();
    }

    private static bool IsEnabled(EntityRowAction<T> action, T item) => action.IsEnabled?.Invoke(item) ?? true;

    private string SortIndicator(string columnKey) =>
        columnKey != SortColumnKey ? string.Empty : SortDescending ? "▼" : "▲";

    private void Refresh()
    {
        var terms = _searchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(SearchTerm.Of).ToList();
        IEnumerable<T> rows = _items.Where(item => MatchesFilters(item) && MatchesSearch(item, terms));

        if (_definition.Columns.FirstOrDefault(c => c.Key == SortColumnKey) is { } column)
        {
            var sortKey = column.SortKey ?? (item => column.Text(item));
            var comparer = Comparer<object?>.Create(CompareValues);
            var ordered = rows.OrderBy(item => IsEmpty(sortKey(item)));
            rows = SortDescending
                ? ordered.ThenByDescending(sortKey, comparer)
                : ordered.ThenBy(sortKey, comparer);
        }

        _visible = rows.ToList();
        Changed?.Invoke();
    }

    private bool MatchesFilters(T item)
    {
        for (var i = 0; i < _definition.Filters.Count; i++)
        {
            if (_definition.Filters[i].Options[_filterSelection[i]].Matches is { } matches && !matches(item))
            {
                return false;
            }
        }

        return true;
    }

    private bool MatchesSearch(T item, IReadOnlyList<SearchTerm> terms)
    {
        if (terms.Count == 0)
        {
            return true;
        }

        var fields = _definition.SearchFields(item)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => (Text: Normalize(f!), Digits: DigitsOf(f!)))
            .ToList();
        return terms.All(term => fields.Any(field =>
            field.Text.Contains(term.Text, StringComparison.Ordinal)
            || (term.Digits is not null && field.Digits.Contains(term.Digits, StringComparison.Ordinal))));
    }

    private static bool IsEmpty(object? value) => value is null || (value is string text && string.IsNullOrWhiteSpace(text));

    private static int CompareValues(object? left, object? right) => (left, right) switch
    {
        (string a, string b) => TextComparer.Compare(a, b),
        (IComparable a, _) => a.CompareTo(right),
        _ => 0,
    };

    /// <summary>Lower case without accents: "Río" and "rio" match.</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static string DigitsOf(string text) => new(text.Where(char.IsAsciiDigit).ToArray());

    /// <summary>One typed word; a word made of digits and separators also matches by its digits alone.</summary>
    private sealed record SearchTerm(string Text, string? Digits)
    {
        public static SearchTerm Of(string word)
        {
            var digits = DigitsOf(word);
            var numeric = digits.Length > 0 && !word.Any(char.IsLetter);
            return new SearchTerm(Normalize(word), numeric ? digits : null);
        }
    }
}
