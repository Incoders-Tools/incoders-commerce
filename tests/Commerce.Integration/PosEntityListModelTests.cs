using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// The UI-free half of the reusable desktop entity list (operator-ux-adjustments T4): search across fields, filters,
/// column sort toggle, row action enablement and confirmation, and the selection -> editor state. The WPF control only
/// renders <see cref="IEntityListModel"/>, so every rule a future entity relies on is pinned here.
/// </summary>
public sealed class PosEntityListModelTests
{
    private sealed record Row(int Id, string Name, string? TaxId, string? City, bool Enabled);

    private static readonly Row Ana = new(1, "Ana Pérez", "20-12345678-9", "Ushuaia", Enabled: true);
    private static readonly Row Bruno = new(2, "bruno Díaz", null, "Río Grande", Enabled: false);
    private static readonly Row Carla = new(3, "Carla Gómez", "27111222", null, Enabled: true);

    private static EntityListModel<Row> Model(
        Func<Row, Task>? toggle = null, IReadOnlyList<EntityRowAction<Row>>? actions = null)
    {
        var definition = new EntityListDefinition<Row>(
            row => row.Id,
            [
                new EntityColumn<Row>("name", "Nombre", row => row.Name),
                new EntityColumn<Row>("taxId", "CUIT/DNI", row => row.TaxId) { Width = 110 },
                new EntityColumn<Row>("city", "Ciudad", row => row.City),
                new EntityColumn<Row>("state", "Estado", row => row.Enabled ? "Habilitado" : "Deshabilitado") { Sortable = false },
            ],
            row => [row.Name, row.TaxId, row.City])
        {
            Filters =
            [
                new EntityFilter<Row>("state", "Estado",
                [
                    new EntityFilterOption<Row>("Todos"),
                    new EntityFilterOption<Row>("Habilitados", row => row.Enabled),
                    new EntityFilterOption<Row>("Deshabilitados", row => !row.Enabled),
                ]),
            ],
            RowActions = actions ??
            [
                EntityRowAction<Row>.Edit(),
                new EntityRowAction<Row>("toggle", row => row.Enabled ? "Deshabilitar" : "Habilitar", _ => "*")
                {
                    Run = toggle ?? (_ => Task.CompletedTask),
                    Confirmation = row => row.Enabled ? $"¿Deshabilitar a {row.Name}?" : null,
                },
            ],
            InitialSortKey = "name",
            NewTitle = "Nuevo cliente",
            EditTitle = row => row.Name,
        };
        var model = new EntityListModel<Row>(definition);
        model.SetItems([Carla, Ana, Bruno]);
        return model;
    }

    private static string[] Names(EntityListModel<Row> model) => model.Visible.Select(r => r.Name).ToArray();

    // ---- search ---------------------------------------------------------------------------

    [Theory]
    [InlineData("ana", new[] { "Ana Pérez" })]
    [InlineData("PEREZ", new[] { "Ana Pérez" })]
    [InlineData("rio grande", new[] { "bruno Díaz" })]
    [InlineData("ushuaia ana", new[] { "Ana Pérez" })]
    [InlineData("ushuaia carla", new string[0])]
    [InlineData("20123456789", new[] { "Ana Pérez" })]
    [InlineData("27-111", new[] { "Carla Gómez" })]
    [InlineData("  ", new[] { "Ana Pérez", "bruno Díaz", "Carla Gómez" })]
    public void Search_MatchesEveryTermAcrossTheFields_IgnoringCaseAccentsAndDigitSeparators(string search, string[] expected)
    {
        var model = Model();

        model.SearchText = search;

        Assert.Equal(expected, Names(model));
        Assert.Equal($"{expected.Length} de 3", model.CountText);
    }

    [Fact]
    public void Search_RaisesChanged_SoTheListFiltersAsTheOperatorTypes()
    {
        var model = Model();
        var changes = 0;
        model.Changed += () => changes++;

        model.SearchText = "a";
        model.SearchText = "an";

        Assert.Equal(2, changes);
    }

    // ---- filters --------------------------------------------------------------------------

    [Fact]
    public void Filters_StartOnTheirFirstOption_AndCombineWithTheSearch()
    {
        var model = Model();
        var filter = Assert.Single(((IEntityListModel)model).Filters);
        Assert.Equal("Estado", filter.Label);
        Assert.Equal(["Todos", "Habilitados", "Deshabilitados"], filter.Options);
        Assert.Equal(0, filter.SelectedIndex);

        model.SelectFilterOption("state", 1);
        Assert.Equal(["Ana Pérez", "Carla Gómez"], Names(model));
        Assert.Equal(1, ((IEntityListModel)model).Filters[0].SelectedIndex);

        model.SearchText = "carla";
        Assert.Equal(["Carla Gómez"], Names(model));

        model.SelectFilterOption("state", 2);
        Assert.Empty(model.Visible);

        model.SelectFilterOption("state", 0);
        Assert.Equal(["Carla Gómez"], Names(model));
    }

    [Fact]
    public void Filters_IgnoreAnUnknownFilterOrAnOptionOutOfRange()
    {
        var model = Model();

        model.SelectFilterOption("nope", 1);
        model.SelectFilterOption("state", 9);

        Assert.Equal(3, model.Visible.Count);
        Assert.Equal(0, ((IEntityListModel)model).Filters[0].SelectedIndex);
    }

    // ---- sort -----------------------------------------------------------------------------

    [Fact]
    public void Sort_StartsOnTheInitialColumnAscending_IgnoringCase()
    {
        var model = Model();

        Assert.Equal(["Ana Pérez", "bruno Díaz", "Carla Gómez"], Names(model));
        Assert.Equal("name", model.SortColumnKey);
        Assert.False(model.SortDescending);
        Assert.Equal("▲", ((IEntityListModel)model).Columns.Single(c => c.Key == "name").SortIndicator);
        Assert.Equal(string.Empty, ((IEntityListModel)model).Columns.Single(c => c.Key == "city").SortIndicator);
    }

    [Fact]
    public void Sort_SameHeaderTogglesTheDirection_AnotherHeaderStartsAscending()
    {
        var model = Model();

        model.SortBy("name");
        Assert.True(model.SortDescending);
        Assert.Equal(["Carla Gómez", "bruno Díaz", "Ana Pérez"], Names(model));
        Assert.Equal("▼", ((IEntityListModel)model).Columns.Single(c => c.Key == "name").SortIndicator);

        model.SortBy("name");
        Assert.False(model.SortDescending);
        Assert.Equal(["Ana Pérez", "bruno Díaz", "Carla Gómez"], Names(model));

        model.SortBy("name");
        model.SortBy("city");
        Assert.Equal("city", model.SortColumnKey);
        Assert.False(model.SortDescending);
        Assert.Equal(string.Empty, ((IEntityListModel)model).Columns.Single(c => c.Key == "name").SortIndicator);
    }

    [Fact]
    public void Sort_PutsEmptyValuesLast_InBothDirections()
    {
        var model = Model();

        model.SortBy("city");
        Assert.Equal(["bruno Díaz", "Ana Pérez", "Carla Gómez"], Names(model));

        model.SortBy("city");
        Assert.Equal(["Ana Pérez", "bruno Díaz", "Carla Gómez"], Names(model));
    }

    [Fact]
    public void Sort_IgnoresANonSortableOrUnknownColumn()
    {
        var model = Model();

        model.SortBy("state");
        model.SortBy("nope");

        Assert.Equal("name", model.SortColumnKey);
        Assert.False(model.SortDescending);
        Assert.False(((IEntityListModel)model).Columns.Single(c => c.Key == "state").Sortable);
    }

    [Fact]
    public void Columns_CarryHeaderAndWidth_AndRowsCarryOneCellPerColumn()
    {
        var model = Model();
        var columns = ((IEntityListModel)model).Columns;

        Assert.Equal(["Nombre", "CUIT/DNI", "Ciudad", "Estado"], columns.Select(c => c.Header));
        Assert.Equal(110, columns[1].Width);
        Assert.Null(columns[0].Width);

        var row = ((IEntityListModel)model).Rows[0];
        Assert.Same(Ana, row.Item);
        Assert.Equal(["Ana Pérez", "20-12345678-9", "Ushuaia", "Habilitado"], row.Cells);
        Assert.Equal(string.Empty, ((IEntityListModel)model).Rows[2].Cells[2]);
    }

    // ---- row actions ----------------------------------------------------------------------

    [Fact]
    public void RowActions_LabelAndEnablementFollowTheRow()
    {
        var actions = new[]
        {
            new EntityRowAction<Row>("toggle", row => row.Enabled ? "Deshabilitar" : "Habilitar", row => row.Enabled ? "off" : "on")
            {
                Run = _ => Task.CompletedTask,
                IsEnabled = row => row.TaxId is not null,
            },
        };
        var model = Model(actions: actions);
        var rows = ((IEntityListModel)model).Rows;

        var ana = Assert.Single(rows[0].Actions);
        Assert.Equal(("toggle", "Deshabilitar", "off", true), (ana.Key, ana.Label, ana.Icon, ana.IsEnabled));
        Assert.Same(Ana, ana.Item);
        var bruno = Assert.Single(rows[1].Actions);
        Assert.Equal(("Habilitar", "on", false), (bruno.Label, bruno.Icon, bruno.IsEnabled));
        Assert.Equal(1, ((IEntityListModel)model).RowActionCount);
    }

    [Fact]
    public async Task RowAction_WithoutConfirmation_RunsAtOnce()
    {
        var ran = new List<Row>();
        var model = Model(toggle: row => { ran.Add(row); return Task.CompletedTask; });

        await model.InvokeAsync(Bruno, "toggle");

        Assert.Equal([Bruno], ran);
        Assert.Null(model.PendingConfirmation);
    }

    [Fact]
    public async Task RowAction_WithConfirmation_WaitsForTheOperator_ThenRunsOnConfirm()
    {
        var ran = new List<Row>();
        var model = Model(toggle: row => { ran.Add(row); return Task.CompletedTask; });

        await model.InvokeAsync(Ana, "toggle");

        Assert.Empty(ran);
        Assert.Equal(new EntityConfirmation("¿Deshabilitar a Ana Pérez?", "Deshabilitar"), model.PendingConfirmation);

        await model.ConfirmAsync();

        Assert.Equal([Ana], ran);
        Assert.Null(model.PendingConfirmation);
    }

    [Fact]
    public async Task RowAction_CancelledConfirmation_NeverRuns()
    {
        var ran = new List<Row>();
        var model = Model(toggle: row => { ran.Add(row); return Task.CompletedTask; });

        await model.InvokeAsync(Ana, "toggle");
        model.CancelConfirmation();
        await model.ConfirmAsync();

        Assert.Empty(ran);
        Assert.Null(model.PendingConfirmation);
    }

    [Fact]
    public async Task RowAction_DisabledOrUnknown_DoesNothing()
    {
        var ran = new List<Row>();
        var actions = new[]
        {
            new EntityRowAction<Row>("toggle", _ => "Habilitar", _ => "*")
            {
                Run = row => { ran.Add(row); return Task.CompletedTask; },
                IsEnabled = row => row.TaxId is not null,
            },
        };
        var model = Model(actions: actions);

        await model.InvokeAsync(Bruno, "toggle");
        await model.InvokeAsync(Ana, "nope");

        Assert.Empty(ran);
    }

    // ---- editor ---------------------------------------------------------------------------

    [Fact]
    public void Editor_StartsClosed_NewOpensItEmpty()
    {
        var model = Model();
        var events = new List<(EntityEditorMode, Row?)>();
        model.EditorChanged += (mode, item) => events.Add((mode, item));

        Assert.Equal(EntityEditorMode.None, model.EditorMode);

        model.BeginNew();

        Assert.Equal(EntityEditorMode.New, model.EditorMode);
        Assert.Null(model.Editing);
        Assert.Equal("Nuevo cliente", model.EditorTitle);
        Assert.Equal([(EntityEditorMode.New, (Row?)null)], events);
    }

    [Fact]
    public async Task Editor_SelectingARowOrItsEditAction_OpensItFilled()
    {
        var model = Model();
        var events = new List<(EntityEditorMode, Row?)>();
        model.EditorChanged += (mode, item) => events.Add((mode, item));

        ((IEntityListModel)model).Select(Bruno);
        Assert.Equal(EntityEditorMode.Edit, model.EditorMode);
        Assert.Same(Bruno, model.Editing);
        Assert.Same(Bruno, ((IEntityListModel)model).EditingItem);
        Assert.Equal("bruno Díaz", model.EditorTitle);

        await model.InvokeAsync(Ana, "edit");
        Assert.Same(Ana, model.Editing);

        Assert.Equal([(EntityEditorMode.Edit, (Row?)Bruno), (EntityEditorMode.Edit, Ana)], events);
    }

    [Fact]
    public void Editor_SelectingTheRowAlreadyOpen_DoesNotRefillTheForm()
    {
        var model = Model();
        model.BeginEdit(Ana);
        var events = 0;
        model.EditorChanged += (_, _) => events++;

        ((IEntityListModel)model).Select(Ana);

        Assert.Equal(0, events);
    }

    [Fact]
    public void Editor_Close_ReturnsToNone()
    {
        var model = Model();
        model.BeginEdit(Ana);
        var events = new List<(EntityEditorMode, Row?)>();
        model.EditorChanged += (mode, item) => events.Add((mode, item));

        model.CloseEditor();

        Assert.Equal(EntityEditorMode.None, model.EditorMode);
        Assert.Null(model.Editing);
        Assert.Equal([(EntityEditorMode.None, (Row?)null)], events);
    }

    [Fact]
    public void Editor_Reload_KeepsTheOpenRowByKey_WithoutRefillingTheForm()
    {
        var model = Model();
        model.BeginEdit(Ana);
        var events = 0;
        model.EditorChanged += (_, _) => events++;
        var freshAna = Ana with { Enabled = false };

        model.SetItems([Bruno, freshAna]);

        Assert.Same(freshAna, model.Editing);
        Assert.Same(freshAna, ((IEntityListModel)model).EditingItem);
        Assert.Equal(0, events);
        Assert.Equal("2 de 2", model.CountText);
    }
    // ---- R3-row-action-click-selects-row: a row action never opens the editor (Editar does, by design) ------------

    [Fact]
    public async Task RowAction_OtherThanEditar_LeavesTheEditorAsItWas()
    {
        var ran = 0;
        var model = Model(toggle: _ => { ran++; return Task.CompletedTask; });

        await model.InvokeAsync(Bruno, "toggle");
        Assert.Equal((EntityEditorMode.None, (Row?)null), (model.EditorMode, model.Editing));

        model.BeginEdit(Carla);
        await model.InvokeAsync(Ana, "toggle");
        await model.ConfirmAsync();

        Assert.Equal(2, ran);
        Assert.Equal((EntityEditorMode.Edit, (Row?)Carla), (model.EditorMode, model.Editing));
    }

    [Fact]
    public void SelectionGate_ASelectionCausedByPressingARowAction_DoesNotOpenTheEditor()
    {
        var gate = new EntityRowSelectionGate();
        Assert.True(gate.SelectionOpensEditor);

        gate.RowActionPressed();
        Assert.False(gate.SelectionOpensEditor);

        gate.PressReleased();
        Assert.True(gate.SelectionOpensEditor);
    }
}
