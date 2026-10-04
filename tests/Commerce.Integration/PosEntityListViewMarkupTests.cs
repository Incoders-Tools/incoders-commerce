using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Commerce.Integration;

/// <summary>
/// Structural guards for the reusable desktop entity list (operator-ux-adjustments T4; WPF cannot be instantiated in
/// this test host): <c>Controls/EntityListView</c> renders an <c>IEntityListModel</c> (search as you type, filters,
/// sortable headers with an indicator, "Nuevo", per-row actions with an inline confirmation, and an editor slot), and
/// the Clientes section is built on it: list and form side by side, the form scrolling inside its panel.
/// </summary>
public sealed class PosEntityListViewMarkupTests
{
    private static string Src(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Commerce.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine([directory!.FullName, "src", "Commerce.Pos.Windows", .. parts]));
    }

    private static string ViewXaml => Src("Controls", "EntityListView.xaml");

    private static string ViewCode => Src("Controls", "EntityListView.xaml.cs");

    [Fact]
    public void EntityListView_IsALooklessControl_WhoseTemplateIsMergedIntoTheApp_AndUsesOnlyDynamicBrushes()
    {
        var root = XDocument.Parse(ViewXaml).Root!;

        // A lookless control on purpose: a UserControl with its own named XAML cannot host an editor whose fields carry
        // x:Name (MC3093), and every entity form needs named fields.
        Assert.Equal("ResourceDictionary", root.Name.LocalName);
        Assert.Contains("TargetType=\"controls:EntityListView\"", ViewXaml);
        Assert.Contains("public sealed class EntityListView : Control", ViewCode);
        Assert.Contains("Source=\"Controls/EntityListView.xaml\"", Src("App.xaml"));
        Assert.Empty(Regex.Matches(ViewXaml, @"\{StaticResource\s+\w*Brush\w*\}"));
        Assert.Contains("<Setter Property=\"Foreground\" Value=\"{DynamicResource TextBrush}\" />", ViewXaml);
        Assert.Contains("public IEntityListModel? Model", ViewCode);
    }

    [Fact]
    public void Search_FiltersAsTheOperatorTypes()
    {
        Assert.Contains("x:Name=\"PART_SearchBox\"", ViewXaml);
        Assert.Contains("x:Name=\"PART_SearchPlaceholder\"", ViewXaml);
        Assert.Contains("TextChanged +=", ViewCode);
        Assert.Contains(".SearchText = ", ViewCode);
    }

    [Fact]
    public void Filters_AreRenderedFromTheModel_AndForwardTheChoice()
    {
        Assert.Contains("x:Name=\"PART_Filters\"", ViewXaml);
        Assert.Contains("SelectFilterOption(", ViewCode);
    }

    [Fact]
    public void Columns_SortOnHeaderClick_ShowingTheDirection()
    {
        Assert.Contains("x:Name=\"PART_Rows\"", ViewXaml);
        Assert.Contains("CanUserSortColumns=\"True\"", ViewXaml);
        Assert.Contains("Sorting +=", ViewCode);
        Assert.Contains("e.Handled = true", ViewCode);
        Assert.Contains(".SortBy(", ViewCode);
        Assert.Contains("SortIndicator", ViewCode);
    }

    [Fact]
    public void Nuevo_OpensTheEditorEmpty_AndSelectingARowOpensItFilled()
    {
        Assert.Contains("x:Name=\"PART_NewButton\"", ViewXaml);
        Assert.Contains(".BeginNew()", ViewCode);
        Assert.Contains("SelectionChanged +=", ViewCode);
        Assert.Contains(".Select(", ViewCode);
    }

    [Fact]
    public void RowActions_AreButtonsWithLabelIconAndEnablement_AndAskInlineBeforeRunning()
    {
        Assert.Contains("x:Key=\"RowActionsTemplate\"", ViewXaml);
        Assert.Contains("IsEnabled=\"{Binding IsEnabled}\"", ViewXaml);
        Assert.Contains("ToolTip=\"{Binding Label}\"", ViewXaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding Label}\"", ViewXaml);
        Assert.Contains("Text=\"{Binding Icon}\"", ViewXaml);
        Assert.Contains("AddHandler(ButtonBase.ClickEvent", ViewCode);
        Assert.Contains(".InvokeAsync(", ViewCode);

        Assert.Contains("x:Name=\"PART_Confirmation\"", ViewXaml);
        Assert.Contains("x:Name=\"PART_ConfirmButton\"", ViewXaml);
        Assert.Contains("x:Name=\"PART_CancelConfirmationButton\"", ViewXaml);
        Assert.Contains(".ConfirmAsync()", ViewCode);
        Assert.Contains(".CancelConfirmation()", ViewCode);
    }

    [Fact]
    public void PressingARowAction_DoesNotSelectTheRowNorOpenTheEditor()
    {
        // The grid selects the row under the pointer even when a button in it handles the press; the control marks the
        // press as a row action, ignores the resulting selection and puts back the model's.
        Assert.Contains("PreviewMouseLeftButtonDown", ViewCode);
        Assert.Contains("_selectionGate.RowActionPressed()", ViewCode);
        Assert.Contains("_selectionGate.PressReleased()", ViewCode);
        var selectionChanged = ViewCode[ViewCode.IndexOf("private void Grid_SelectionChanged(", StringComparison.Ordinal)..];
        selectionChanged = selectionChanged[..selectionChanged.IndexOf("private void NewButton_Click(", StringComparison.Ordinal)];
        Assert.Contains("_selectionGate.SelectionOpensEditor", selectionChanged);
        Assert.True(
            selectionChanged.IndexOf("_selectionGate.SelectionOpensEditor", StringComparison.Ordinal)
            < selectionChanged.IndexOf("_model.Select(", StringComparison.Ordinal));
    }

    [Fact]
    public void Editor_IsASlotBesideTheList_WithTitleCloseAndEmptyHint_InTheHostsNameScope()
    {
        Assert.Contains("EditorContentProperty", ViewCode);
        Assert.Contains("AddLogicalChild(", ViewCode);
        Assert.Contains("x:Name=\"PART_Editor\"", ViewXaml);
        Assert.Contains("x:Name=\"PART_EditorTitle\"", ViewXaml);
        Assert.Contains("x:Name=\"PART_EmptyEditor\"", ViewXaml);
        Assert.Contains("x:Name=\"PART_CloseEditorButton\"", ViewXaml);
        Assert.Contains(".CloseEditor()", ViewCode);
        Assert.Contains("EditorWidthProperty", ViewCode);
    }

    [Fact]
    public void EntityListView_UsesTheThemeSpacingTokens()
    {
        Assert.Contains("FieldActionSpacing}", ViewXaml);
        Assert.Contains("ActionSpacing}", ViewXaml);
    }

    // ---- Clientes on the component --------------------------------------------------------

    private static string CustomersXaml => Src("CustomersView.xaml");

    private static string CustomersCode => Src("CustomersView.xaml.cs");

    [Fact]
    public void CustomersView_IsBuiltOnTheEntityList_InsteadOfThePlainListBox()
    {
        Assert.Contains("<controls:EntityListView", CustomersXaml);
        Assert.Contains("<controls:EntityListView.EditorContent>", CustomersXaml);
        Assert.DoesNotContain("CustomersListBox", CustomersXaml);
        Assert.DoesNotContain("CustomersListBox", CustomersCode);
        Assert.DoesNotContain("NewCustomerButton", CustomersXaml);
        Assert.Contains("CustomerList.Definition(", CustomersCode);
        Assert.Contains("EditorChanged", CustomersCode);
        Assert.Contains(".SetItems(", CustomersCode);
    }

    [Fact]
    public void CustomersView_ScrollsOnlyTheForm_InsideTheEditorPanel()
    {
        var editor = CustomersXaml[CustomersXaml.IndexOf("<controls:EntityListView.EditorContent>", StringComparison.Ordinal)..];

        Assert.Single(Regex.Matches(CustomersXaml, "<ScrollViewer"));
        Assert.Contains("<ScrollViewer", editor);
        Assert.Contains("Content=\"Guardar\"", editor);
    }

    [Fact]
    public void CustomersView_TogglesThroughTheUpdateEndpoint_AndKeepsAnOpenFormInStep()
    {
        // The toggle re-reads the customer before the PUT (CustomerList.ToggleEnabledAsync); the cached row is never sent.
        Assert.Contains("CustomerList.ToggleEnabledAsync(_adminClient, customer, ", CustomersCode);
        Assert.DoesNotContain("CustomerList.ToggleEnabledRequest(", CustomersCode);
        Assert.Contains("IsEnabledCheckBox.IsChecked", CustomersCode);
    }

    [Fact]
    public void CustomersView_LaysTheAddressOutInRows_InTheAgreedOrder()
    {
        var labels = new[] { "\"Provincia\"", "\"Ciudad\"", "\"Código postal\"", "\"Barrio\"", "\"Calle\"", "\"Número\"", "\"Notas de entrega\"" };
        var positions = labels.Select(l => CustomersXaml.IndexOf("Text=" + l, StringComparison.Ordinal)).ToList();

        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
    }

    [Fact]
    public void CustomersView_PlacesTheFormFieldsInColumns()
    {
        Assert.True(Regex.Matches(CustomersXaml, "<UniformGrid Columns=\"2\"").Count >= 3, "identity, contact and commercial fields in two columns");
        Assert.DoesNotContain("Value=\"520\"", CustomersXaml);
    }
}
