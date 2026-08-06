namespace Eslee.TrayFolder.Services;

public sealed record AppPaths(string DataDirectory)
{
    public string ConfigFile => Path.Combine(DataDirectory, "config.json");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static AppPaths ForCurrentUser()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Windows 로컬 앱 데이터 폴더를 확인할 수 없습니다.");
        }

        return new AppPaths(Path.Combine(localAppData, "eslee-tray-folder"));
    }
}
