using Microsoft.Windows.Storage.Pickers;

namespace TrimFetch.Services;

public sealed class FilePickerService
{
    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(App.WindowId)
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
        };
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string extension, string filterName)
    {
        var picker = new FileSavePicker(App.WindowId)
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = suggestedName,
        };

        picker.FileTypeChoices.Add(filterName, [extension]);
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }
}
