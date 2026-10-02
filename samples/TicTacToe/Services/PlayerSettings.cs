using Microsoft.Maui.Graphics.Platform;

namespace TicTacToe.Services;

public class PlayerSettings
{
    public string Name
    {
        get
        {
            var name = Preferences.Get(nameof(this.Name), DeviceInfo.Current.Name);
            return name.Length > 12 ? name[..12].Trim() : name;
        }
        set => Preferences.Set(nameof(this.Name), value);
    }


    public string? AvatarPath
    {
        get
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "my-avatar.jpg");
            return File.Exists(path) ? path : null;
        }
    }


    /// <summary>
    /// Shrinks the picked photo so the L2CAP transfer is quick
    /// </summary>
    public async Task SaveAvatar(FileResult photo)
    {
        await using var input = await photo.OpenReadAsync();
        var image = PlatformImage.FromStream(input);
        using var resized = image.Downsize(256, true);

        var path = Path.Combine(FileSystem.AppDataDirectory, "my-avatar.jpg");
        await using var output = File.Create(path);
        await resized.SaveAsync(output, ImageFormat.Jpeg, 0.8f);
    }


    public void ClearAvatar()
    {
        if (this.AvatarPath is { } path)
            File.Delete(path);
    }
}
