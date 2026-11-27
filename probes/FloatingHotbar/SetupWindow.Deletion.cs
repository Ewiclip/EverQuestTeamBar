using W = System.Windows;
using C = System.Windows.Controls;
namespace TeamBar;

public partial class SetupWindow
{
    internal Func<W.Window, DeleteRequest, bool> ConfirmDeletion = DeleteConfirmation.Ask;
    private void DeleteLibraryItem_Click(object sender, W.RoutedEventArgs e) => Act(() =>
    {
        e.Handled = true;
        if (sender is not C.Button { DataContext: { } item } || item is not (ButtonDraft or ButtonGroup)) return;
        string id = LibraryId(item);
        var request = item is ButtonDraft button
            ? new DeleteRequest("Delete button?", button.ManagementName, "This removes the button from your library. Groups using it will no longer have this button available.")
            : new DeleteRequest("Delete button group?", ((ButtonGroup)item).ManagementName, "This removes the group. Its saved buttons remain in your library and in any other groups.");
        if (!ConfirmDeletion(this, request)) return;
        // Use the clicked entry's identity, not whichever row was selected.
        object? current = item is ButtonDraft ? standaloneDrafts.FirstOrDefault(b => b.Id == id) : groupDrafts.FirstOrDefault(g => g.Id == id);
        if (current is null) return;
        ButtonList.SelectedItem = current;
        RemoveSelectedLibraryItem();
    });
}
