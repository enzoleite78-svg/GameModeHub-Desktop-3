using System;
using System.IO;

namespace GameModeHub.Core
{
    /// <summary>
    /// Onde ficam os dados. Se existir "portable.flag" ao lado do .exe (ou a pasta "data"),
    /// tudo fica ali (pendrive). Senão usa %APPDATA%\GameModeHub (compatível com a versão 3).
    /// </summary>
    public static class Paths
    {
        public static readonly string AppDir = AppContext.BaseDirectory;
        public static readonly bool Portable =
            File.Exists(Path.Combine(AppDir, "portable.flag")) ||
            Directory.Exists(Path.Combine(AppDir, "data"));

        public static readonly string DataDir = Portable
            ? Path.Combine(AppDir, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameModeHub");

        public static string ConfigFile => Path.Combine(DataDir, "config.json");
        public static string LayoutsFile => Path.Combine(DataDir, "layouts.json");
        public static string LogFile => Path.Combine(DataDir, "gamemodehub.log");
        public static string WallpaperDir => Path.Combine(DataDir, "wallpaper");

        public static void Ensure()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(WallpaperDir);
        }
    }
}
